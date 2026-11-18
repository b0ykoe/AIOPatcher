using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Changelogs anstehender Linux-Updates (Patchnotes): der Agent holt sie per <c>apt-get changelog</c> nach jedem Scan
/// und bei jeder Meldung alles, was für die anstehenden Updates noch fehlt. Sie liegen dauerhaft in der Datenbank
/// (<see cref="PackageChangelogRecord"/>, je Paket und Version einmal für alle Hosts). Nur die Warteschlange, was ein
/// Agent noch holen soll, liegt im Speicher.
/// </summary>
public sealed partial class UpdateChangelogs
{
    /// <summary>Kein Changelog zu haben (z. B. Quelle bietet keine an): erst nach dieser Zeit wieder versuchen.</summary>
    public static readonly TimeSpan RetryErrorsAfter = TimeSpan.FromHours(6);

    /// <summary>So lange bleiben Patchnotes in der Datenbank (Historie an Patch-Jobs).</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(400);

    /// <param name="Version">null, wenn der Text kein Debian-Changelog war (dann steht alles in Text).</param>
    public sealed record Section(string? Version, string? Date, string Text);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<(string Name, string Version), byte>> _pending = new();

    /// <summary>Wird nach dem Speichern neuer Patchnotes eines Hosts aufgerufen (z. B. KI-Zusammenfassung anstoßen).</summary>
    public event Action<Guid, IReadOnlyList<(string Name, string Version)>>? Stored;

    /// <summary>Gilt als vorhanden: mit Text immer, ohne Text (Fehler) nur eine Weile, danach neuer Versuch.</summary>
    public static bool IsUsable(PackageChangelogRecord? record, DateTime now) =>
        record is not null && (record.Text is not null || now - record.FetchedAt < RetryErrorsAfter);

    public static Task<PackageChangelogRecord?> GetAsync(AppDbContext db, string name, string version, CancellationToken ct) =>
        db.PackageChangelogs.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name && c.Version == version, ct);

    /// <summary>Für diesen Host vormerken, was noch fehlt; der nächste Abruf des Agents holt es (UpdatesServerModule).</summary>
    public async Task<int> RequestAsync(AppDbContext db, Guid hostId, IEnumerable<(string Name, string Version)> packages, DateTime now, CancellationToken ct)
    {
        var wanted = packages.Where(p => p.Name.Length <= 100 && p.Version.Length <= 100).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var names = wanted.Select(p => p.Name).Distinct().ToList();
        var known = (await db.PackageChangelogs.AsNoTracking().Where(c => names.Contains(c.Name)).ToListAsync(ct))
            .Where(c => IsUsable(c, now))
            .Select(c => (c.Name, c.Version))
            .ToHashSet();

        var queue = _pending.GetOrAdd(hostId, _ => new());
        var added = 0;
        foreach (var package in wanted)
        {
            if (!known.Contains(package) && queue.TryAdd(package, 0))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>Alle anstehenden Linux-Updates eines Hosts vormerken, deren Patchnotes noch fehlen.</summary>
    public async Task<int> RequestMissingAsync(AppDbContext db, ManagedHost host, DateTime now, CancellationToken ct)
    {
        var updates = await db.PendingUpdates.AsNoTracking()
            .Where(u => u.HostId == host.Id && u.AvailableVersion != null)
            .Select(u => new { u.Name, u.AvailableVersion })
            .ToListAsync(ct);
        return await RequestAsync(db, host.Id, updates.Select(u => (u.Name, u.AvailableVersion!)), now, ct);
    }

    /// <summary>Liste der anstehenden Updates eines Hosts hat sich geändert (Scan): Zusammenfassung ggf. erneuern.</summary>
    public void Touched(Guid hostId) => Stored?.Invoke(hostId, []);

    public bool HasPending(Guid hostId) => _pending.TryGetValue(hostId, out var queue) && !queue.IsEmpty;

    /// <summary>Bis zu <paramref name="max"/> vorgemerkte Pakete für einen Auftrag entnehmen.</summary>
    public List<ChangelogPackage> TakePending(Guid hostId, int max)
    {
        var taken = new List<ChangelogPackage>();
        if (!_pending.TryGetValue(hostId, out var queue))
        {
            return taken;
        }

        foreach (var key in queue.Keys.Take(max))
        {
            if (queue.TryRemove(key, out _))
            {
                taken.Add(new ChangelogPackage(key.Name, key.Version));
            }
        }

        return taken;
    }

    /// <summary>Antwort des Agents speichern (sofort, unabhängig vom Rest des Ergebnisses).</summary>
    public async Task StoreAsync(AppDbContext db, Guid hostId, IEnumerable<PackageChangelog> changelogs, DateTime now, CancellationToken ct)
    {
        var incoming = changelogs.Where(c => c.Name.Length <= 100 && c.Version.Length <= 100).ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        var names = incoming.Select(c => c.Name).Distinct().ToList();
        var existing = (await db.PackageChangelogs.Where(c => names.Contains(c.Name)).ToListAsync(ct)).ToDictionary(c => (c.Name, c.Version));
        var withText = new List<(string, string)>();
        foreach (var c in incoming)
        {
            var text = c.Text is { Length: > 64_000 } big ? big[..64_000] : c.Text;
            if (!existing.TryGetValue((c.Name, c.Version), out var row))
            {
                db.PackageChangelogs.Add(row = new PackageChangelogRecord { Name = c.Name, Version = c.Version });
                existing[(c.Name, c.Version)] = row;
            }
            else if (row.Text is not null && text is null)
            {
                continue; // vorhandenen Text nicht durch einen späteren Fehler ersetzen
            }

            row.Text = text;
            row.Error = text is null ? Truncate(c.Error ?? "No changelog.") : null;
            row.FetchedAt = now;
            if (text is not null)
            {
                withText.Add((c.Name, c.Version));
            }
        }

        // Gleich speichern: liefern zwei Hosts dasselbe Paket gleichzeitig, verliert nur einer (eindeutiger Index), nicht
        // das ganze Ergebnis des Auftrags.
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<PackageChangelogRecord>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        if (withText.Count > 0)
        {
            Stored?.Invoke(hostId, withText);
        }
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;

    // Kopfzeile eines Debian-Changelog-Eintrags: "paket (version) suite; urgency=..."
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.+-]* \(([^()\s]+)\) [^;]*;", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    // Fußzeile: " -- Name <mail>  Datum"
    [GeneratedRegex(@"^ -- .*>  (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailerPattern();

    /// <summary>
    /// Debian-Changelog in Einträge zerlegen und nur die behalten, die neuer als die installierte Version sind (was das
    /// Update mitbringt). Kein Debian-Format erkannt: ganzer Text als ein Abschnitt.
    /// </summary>
    public static List<Section> Since(string text, string? installedVersion, int maxSections = 50)
    {
        var sections = new List<Section>();
        string? version = null;
        string? date = null;
        var body = new System.Text.StringBuilder();

        void Flush()
        {
            if (version is not null)
            {
                sections.Add(new Section(version, date, body.ToString().Trim('\n')));
            }

            body.Clear();
            date = null;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (HeaderPattern().Match(line) is { Success: true } header)
            {
                Flush();
                version = header.Groups[1].Value;
                continue;
            }

            if (version is null)
            {
                continue;
            }

            // Fußzeile nicht in den Text: das Datum steht in der Anzeige neben der Version
            if (TrailerPattern().Match(line) is { Success: true } trailer)
            {
                date = trailer.Groups[1].Value.Trim();
                continue;
            }

            body.Append(line).Append('\n');
        }

        Flush();

        if (sections.Count == 0)
        {
            return string.IsNullOrWhiteSpace(text) ? [] : [new Section(null, null, text.Trim())];
        }

        // neueste zuerst; ältere als die installierte Version hat der Host schon
        return sections
            .TakeWhile(s => installedVersion is null || DebianVersion.Compare(s.Version, installedVersion) > 0)
            .Take(maxSections)
            .ToList();
    }
}
