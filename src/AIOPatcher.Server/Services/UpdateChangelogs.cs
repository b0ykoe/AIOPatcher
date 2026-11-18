using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Services.Security;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Changelogs anstehender Linux-Updates (Patchnotes): der Agent holt sie per <c>apt-get changelog</c> gleich nach jedem
/// Scan, hier liegen sie je Paket und Version (für alle Hosts mit demselben Paket nur einmal). Nur im Speicher: nach
/// einem Neustart holt der nächste Scan oder ein Klick auf "i" sie neu.
/// </summary>
public sealed partial class UpdateChangelogs
{
    /// <summary>Höchstens so viele Changelogs im Speicher (je bis 32 KB); die ältesten fallen zuerst heraus.</summary>
    public const int Capacity = 2000;

    /// <summary>Kein Changelog zu haben (z. B. Quelle bietet keine an): erst nach dieser Zeit wieder versuchen.</summary>
    public static readonly TimeSpan RetryErrorsAfter = TimeSpan.FromHours(6);

    public sealed record Entry(string? Text, string? Error, DateTime FetchedAt);

    /// <param name="Version">null, wenn der Text kein Debian-Changelog war (dann steht alles in Text).</param>
    public sealed record Section(string? Version, string? Date, string Text);

    private readonly ConcurrentDictionary<(string Name, string Version), Entry> _cache = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<(string Name, string Version), byte>> _pending = new();

    public Entry? Get(string name, string version, DateTime now) =>
        _cache.TryGetValue((name, version), out var entry) && (entry.Text is not null || now - entry.FetchedAt < RetryErrorsAfter) ? entry : null;

    /// <summary>Für diesen Host vormerken, was noch fehlt; der nächste Abruf des Agents holt es (UpdatesServerModule).</summary>
    public int Request(Guid hostId, IEnumerable<(string Name, string Version)> packages, DateTime now)
    {
        var queue = _pending.GetOrAdd(hostId, _ => new());
        var added = 0;
        foreach (var package in packages)
        {
            if (Get(package.Name, package.Version, now) is null && queue.TryAdd(package, 0))
            {
                added++;
            }
        }

        return added;
    }

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

    public void Store(IEnumerable<PackageChangelog> changelogs, DateTime now)
    {
        foreach (var c in changelogs)
        {
            if (c.Name.Length > 100 || c.Version.Length > 100)
            {
                continue;
            }

            var text = c.Text is { Length: > 64_000 } big ? big[..64_000] : c.Text;
            _cache[(c.Name, c.Version)] = new Entry(text, text is null ? Truncate(c.Error ?? "No changelog.") : null, now);
        }

        // Grob begrenzen: älteste zuerst entfernen
        if (_cache.Count > Capacity)
        {
            foreach (var old in _cache.OrderBy(e => e.Value.FetchedAt).Take(_cache.Count - Capacity).Select(e => e.Key).ToList())
            {
                _cache.TryRemove(old, out _);
            }
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
