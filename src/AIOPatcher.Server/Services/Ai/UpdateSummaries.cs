using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Ai;

/// <summary>
/// KI-Zusammenfassungen der Patchnotes: je Paket (was bringt diese Version) und je Host (was bringen alle anstehenden
/// Updates zusammen). Läuft automatisch im Hintergrund, sobald neue Patchnotes da sind oder ein Scan die Liste ändert;
/// ein Host nach dem anderen, damit die KI nicht überrannt wird. Einstellungen gecacht, gelten ohne Neustart.
/// Sparsam: Pakete werden gebündelt (eine Anfrage für bis zu <see cref="BatchPackages"/> Pakete), kurze Changelogs
/// ("New upstream release") gehen gar nicht an die KI, und jede Paketversion wird nur einmal zusammengefasst.
/// </summary>
public sealed partial class UpdateSummaries : BackgroundService
{
    /// <summary>Höchstens so viele Pakete je Durchlauf eines Hosts zusammenfassen (Rest beim nächsten Mal).</summary>
    public const int MaxPackagesPerRun = 100;

    /// <summary>Pakete je Anfrage.</summary>
    public const int BatchPackages = 25;

    /// <summary>Changelog-Zeichen je Anfrage (alle Pakete zusammen).</summary>
    public const int BatchChars = 30_000;

    /// <summary>Changelog-Zeichen je Paket: die neuesten Einträge reichen für eine Zusammenfassung.</summary>
    public const int PackageChars = 2_000;

    /// <summary>Kürzere Changelogs (ohne Aufzählungszeichen) werden so angezeigt, wie sie sind, ohne KI.</summary>
    public const int BriefChars = 160;

    /// <summary>Markierung in SummaryModel: "Zusammenfassung" ist der kurze Changelog selbst, keine KI-Anfrage.</summary>
    public const string FromChangelog = "changelog";

    /// <summary>Fehlgeschlagene Paket-Zusammenfassungen erst nach dieser Zeit erneut versuchen (kostet sonst bei jedem Scan).</summary>
    public static readonly TimeSpan RetryErrorsAfter = TimeSpan.FromHours(6);

    /// <summary>So lange bleibt jede einzelne Anfrage im Protokoll.</summary>
    public static readonly TimeSpan KeepRequests = TimeSpan.FromDays(90);

    private const string ProtectorPurpose = "AIOPatcher.AiKey.v1";

    /// <summary>Erst so lange nach dem Vormerken bearbeiten: das auslösende Ergebnis (Scan, Patchnotes) ist dann gespeichert.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private readonly Channel<(Guid HostId, DateTime At)> _queue = Channel.CreateUnbounded<(Guid, DateTime)>();
    private readonly HashSet<Guid> _queued = [];
    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly AiClient _ai;
    private readonly ILogger<UpdateSummaries> _logger;
    private volatile AiSettingsRecord _settings = new();
    private long _pausedUntilTicks;

    public UpdateSummaries(IServiceScopeFactory scopes, IDataProtectionProvider protection, AiClient ai, UpdateChangelogs changelogs, ILogger<UpdateSummaries> logger)
    {
        _scopes = scopes;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _ai = ai;
        _logger = logger;
        changelogs.Stored += (hostId, _) => Enqueue(hostId);
    }

    public AiSettingsRecord Settings => _settings;

    public bool Active => _settings.Enabled;

    /// <summary>Ratenlimit erreicht: bis dahin keine Anfragen, danach geht es mit den vorgemerkten Hosts weiter.</summary>
    public DateTime PausedUntil => new(Interlocked.Read(ref _pausedUntilTicks), DateTimeKind.Utc);

    private void Pause(DateTime until)
    {
        var max = DateTime.UtcNow + RetryErrorsAfter;
        Interlocked.Exchange(ref _pausedUntilTicks, (until > max ? max : until).Ticks);
        _logger.LogInformation("AI rate limit reached, summaries paused until {Until:u}", PausedUntil);
    }

    public async Task LoadAsync(AppDbContext db, CancellationToken ct) =>
        _settings = await db.AiSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new AiSettingsRecord();

    public void Use(AiSettingsRecord settings) => _settings = settings;

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    public string? ApiKeyOf(AiSettingsRecord settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedApiKey))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(settings.ProtectedApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // Schlüsselring verloren: Schlüssel neu eintragen
        }
    }

    /// <summary>Host vormerken (doppelte Einträge werden zusammengefasst).</summary>
    public void Enqueue(Guid hostId)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        lock (_queued)
        {
            if (!_queued.Add(hostId))
            {
                return;
            }
        }

        _queue.Writer.TryWrite((hostId, DateTime.UtcNow));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (hostId, at) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // erst nach dem Settle, bei erreichtem Ratenlimit erst danach
            var due = at + Settle > PausedUntil ? at + Settle : PausedUntil;
            if (due - DateTime.UtcNow is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait, stoppingToken);
            }

            lock (_queued)
            {
                _queued.Remove(hostId);
            }

            try
            {
                using var scope = _scopes.CreateScope();
                await RunHostAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), hostId, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Update summary for host {HostId} failed", hostId);
            }
        }
    }

    /// <summary>Fehlende Paket-Zusammenfassungen eines Hosts erstellen, danach die Zusammenfassung für den Host.</summary>
    public async Task RunHostAsync(AppDbContext db, Guid hostId, DateTime now, CancellationToken ct)
    {
        var settings = _settings;
        if (!settings.Enabled)
        {
            return;
        }

        var key = ApiKeyOf(settings);
        var label = AiClient.Label(settings);
        var updates = await db.PendingUpdates.AsNoTracking()
            .Where(u => u.HostId == hostId && u.AvailableVersion != null)
            .OrderBy(u => u.Name)
            .Select(u => new { u.Name, u.CurrentVersion, AvailableVersion = u.AvailableVersion!, u.IsSecurity })
            .ToListAsync(ct);

        var names = updates.Select(u => u.Name).ToList();
        var records = (await db.PackageChangelogs.Where(c => names.Contains(c.Name)).ToListAsync(ct)).ToDictionary(c => (c.Name, c.Version));

        // 1) je Paket: was bringt diese Version gegenüber der installierten. Kurze Changelogs ohne KI, der Rest gebündelt.
        var todo = new List<PackageInput>();
        foreach (var update in updates)
        {
            if (todo.Count >= MaxPackagesPerRun || !records.TryGetValue((update.Name, update.AvailableVersion), out var record) || record.Text is null
                || IsDone(record, settings.Language, label, now))
            {
                continue;
            }

            var sections = UpdateChangelogs.Since(record.Text, update.CurrentVersion);
            if (Brief(sections) is { } brief)
            {
                record.Summary = brief.Length > 0 ? brief : null;
                record.SummaryError = null;
                record.SummaryModel = FromChangelog;
                record.SummaryLanguage = null;
                record.SummaryFrom = update.CurrentVersion;
                record.SummaryAt = now;
                continue;
            }

            todo.Add(new PackageInput(record, update.Name, update.CurrentVersion, update.AvailableVersion, Excerpt(sections, PackageChars)));
        }

        await db.SaveChangesAsync(ct);
        foreach (var batch in Batches(todo))
        {
            // Fehler (Schlüssel falsch, Anbieter nicht erreichbar): die übrigen Bündel gar nicht erst schicken
            var result = await SummarizeBatchAsync(db, settings, key, label, hostId, batch, now, ct);
            if (result == BatchResult.RateLimited)
            {
                Enqueue(hostId); // nach der Pause weiter, auch mit der Zusammenfassung für den Host
                return;
            }

            if (result == BatchResult.Failed)
            {
                break;
            }
        }

        // 2) je Host: alle anstehenden Updates zusammen, nur wenn sich die Liste geändert hat
        var fingerprint = Fingerprint(updates.Select(u => $"{u.Name}={u.AvailableVersion}").Append($"lang={settings.Language};model={label}"));
        var summary = await db.HostUpdateSummaries.SingleOrDefaultAsync(s => s.HostId == hostId, ct);
        if (updates.Count == 0)
        {
            if (summary is not null)
            {
                db.HostUpdateSummaries.Remove(summary);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (summary?.Fingerprint == fingerprint && summary.Text is not null)
        {
            return;
        }

        var lines = updates.Select(u =>
        {
            var text = records.TryGetValue((u.Name, u.AvailableVersion), out var r) ? r.Summary ?? Excerpt(r.Text is null ? [] : UpdateChangelogs.Since(r.Text, u.CurrentVersion), 600) : "";
            return $"- {u.Name} {u.CurrentVersion ?? "(new)"} -> {u.AvailableVersion}{(u.IsSecurity ? " [security]" : "")}: {(text.Length > 0 ? text : "no release notes")}";
        });

        string? text = null;
        string? error = null;
        try
        {
            text = (await CallAsync(db, settings, key, "host", hostId, updates.Count, HostPrompt(settings.Language),
                $"{updates.Count} pending updates on one server:\n{Truncate(string.Join("\n", lines), 40_000)}", 2048, now, ct)).Text;
        }
        catch (AiRateLimitException ex)
        {
            // kein Fehler: nach der Pause neu versuchen, bis dahin bleibt die alte Zusammenfassung
            Pause(ex.RetryAt);
            Enqueue(hostId);
            return;
        }
        catch (AiException ex)
        {
            error = Truncate(ex.Message);
        }

        if (summary is null)
        {
            db.HostUpdateSummaries.Add(summary = new HostUpdateSummary { HostId = hostId, Fingerprint = fingerprint });
        }

        summary.Text = text;
        summary.Error = error;
        summary.Fingerprint = fingerprint;
        summary.Model = label;
        summary.UpdateCount = updates.Count;
        summary.CreatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Kurzer Testlauf für die Einstellungsseite (dasselbe Format wie die echten Paket-Anfragen).</summary>
    public async Task<string> TestAsync(AppDbContext db, AiSettingsRecord settings, string? apiKey, DateTime now, CancellationToken ct)
    {
        var answer = await CallAsync(db, settings, apiKey, "test", null, 1, PackagePrompt(settings.Language), PackageRequest([
            new PackageInput(null!, "openssl", "3.0.15-1~deb12u1", "3.0.16-1~deb12u1",
                "3.0.16-1~deb12u1:\n  * Import 3.0.16\n    - CVE-2024-13176 (Timing side-channel in ECDSA signature computation).\n" +
                "    - CVE-2024-9143 (Low-level invalid GF(2^m) parameters lead to OOB memory access)."),
        ]), MaxTokensFor(1), now, ct);
        return answer.Text;
    }

    /// <summary>
    /// Eine Anfrage an die KI und ihr Eintrag im Protokoll (Tokens, Dauer, Ergebnis) samt Tagessumme. Speichert dabei
    /// auch übrige Änderungen im Kontext.
    /// </summary>
    private async Task<AiAnswer> CallAsync(AppDbContext db, AiSettingsRecord settings, string? apiKey, string kind, Guid? hostId, int items, string system,
        string user, int maxTokens, DateTime now, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var entry = new AiRequestRecord { At = now, Kind = kind, Model = AiClient.Label(settings), HostId = hostId, Items = items, Outcome = "ok" };
        try
        {
            var answer = await _ai.CompleteAsync(settings, apiKey, system, user, maxTokens, ct);
            entry.InputTokens = answer.InputTokens;
            entry.OutputTokens = answer.OutputTokens;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            db.AiRequests.Add(entry);
            await RecordUsageAsync(db, answer, now, ct);
            return answer;
        }
        catch (AiException ex)
        {
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            entry.Outcome = ex is AiRateLimitException ? "rateLimited" : "error";
            entry.Error = Truncate(ex.Message);
            db.AiRequests.Add(entry);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <summary>Verbrauch der letzten Tage (Anfragen, Tokens laut Anbieter).</summary>
    public static async Task<(int Requests, long InputTokens, long OutputTokens)> UsageAsync(AppDbContext db, DateTime now, int days, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(now).AddDays(1 - days);
        var rows = await db.AiUsage.AsNoTracking().Where(u => u.Day >= from).ToListAsync(ct);
        return (rows.Sum(r => r.Requests), rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens));
    }

    public static async Task RecordUsageAsync(AppDbContext db, AiAnswer answer, DateTime now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now);
        var row = await db.AiUsage.FindAsync([day], ct);
        if (row is null)
        {
            db.AiUsage.Add(row = new AiUsageDay { Day = day });
        }

        row.Requests++;
        row.InputTokens += answer.InputTokens;
        row.OutputTokens += answer.OutputTokens;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ein Paket für die gebündelte Anfrage (Excerpt = Changelog seit der installierten Version, gekürzt).</summary>
    private sealed record PackageInput(PackageChangelogRecord Record, string Name, string? From, string To, string Excerpt);

    private enum BatchResult { Done, Failed, RateLimited }

    private async Task<BatchResult> SummarizeBatchAsync(AppDbContext db, AiSettingsRecord settings, string? key, string label, Guid hostId,
        List<PackageInput> batch, DateTime now, CancellationToken ct)
    {
        Dictionary<string, string> answers;
        string? error = null;
        try
        {
            var answer = await CallAsync(db, settings, key, "packages", hostId, batch.Count, PackagePrompt(settings.Language), PackageRequest(batch),
                MaxTokensFor(batch.Count), now, ct);
            answers = ParsePackageAnswer(answer.Text, batch.Select(p => p.Name));
        }
        catch (AiRateLimitException ex)
        {
            // kein Fehler am Paket: nach der Pause neu
            Pause(ex.RetryAt);
            return BatchResult.RateLimited;
        }
        catch (AiException ex)
        {
            answers = [];
            error = Truncate(ex.Message);
        }

        foreach (var p in batch)
        {
            var record = p.Record;
            if (answers.TryGetValue(p.Name, out var text))
            {
                record.Summary = text;
                record.SummaryError = null;
            }
            else
            {
                record.Summary = null;
                record.SummaryError = error ?? L.T("Die KI hat zu diesem Paket nichts geliefert.", "The AI returned nothing for this package.");
            }

            record.SummaryModel = label;
            record.SummaryLanguage = settings.Language;
            record.SummaryFrom = p.From;
            record.SummaryAt = now;
        }

        await db.SaveChangesAsync(ct);
        return error is null ? BatchResult.Done : BatchResult.Failed;
    }

    /// <summary>Schon erledigt: kurzer Changelog, oder Zusammenfassung mit derselben Sprache und demselben Modell (Fehler erst nach einer Weile neu).</summary>
    private static bool IsDone(PackageChangelogRecord record, string language, string label, DateTime now) =>
        record.SummaryModel == FromChangelog
        || record.SummaryLanguage == language && record.SummaryModel == label && (record.Summary is not null || record.SummaryAt > now - RetryErrorsAfter);

    /// <summary>
    /// Kurzer Changelog (z. B. "New upstream release", "No-change rebuild", ein einzelner Fix): der Text selbst, ohne
    /// Aufzählungszeichen und Namenszeilen ("[ Name ]"); "" ohne neue Einträge. null, wenn er zu lang ist (dann KI).
    /// </summary>
    public static string? Brief(IReadOnlyList<UpdateChangelogs.Section> sections)
    {
        var lines = sections.SelectMany(s => s.Text.Split('\n'))
            .Select(l => l.Trim().TrimStart('*', '-', '+').Trim())
            .Where(l => l.Length > 0 && !(l.StartsWith('[') && l.EndsWith(']')));
        var text = string.Join(" ", lines);
        return text.Length <= BriefChars ? text : null;
    }

    private static IEnumerable<List<PackageInput>> Batches(IEnumerable<PackageInput> items)
    {
        var batch = new List<PackageInput>();
        var chars = 0;
        foreach (var item in items)
        {
            if (batch.Count > 0 && (batch.Count >= BatchPackages || chars + item.Excerpt.Length > BatchChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }

            batch.Add(item);
            chars += item.Excerpt.Length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static int MaxTokensFor(int packages) => Math.Clamp(200 * packages + 256, 1024, 8192);

    private static string PackageRequest(IReadOnlyList<PackageInput> batch)
    {
        var text = new StringBuilder($"{batch.Count} package update(s):\n");
        foreach (var p in batch)
        {
            text.Append($"\n### {p.Name}\nInstalled: {p.From ?? "(new)"}\nOffered: {p.To}\nChangelog since the installed version:\n{p.Excerpt}\n");
        }

        return text.ToString();
    }

    // "pve-manager: Fixes …", auch "- **pve-manager**: …" oder "`pve-manager`: …". Paketnamen sind klein geschrieben (Debian),
    // daher gilt "Note: …" als Folgezeile.
    [GeneratedRegex(@"^(?:[-*•]\s+)?(?:\*\*|`)?(?<name>[a-z0-9][a-z0-9+._-]*)(?:\*\*|`)?\s*:(?:\*\*)?\s*(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AnswerLine();

    /// <summary>Antwort "paket: zusammenfassung" je Zeile den angefragten Paketen zuordnen; Folgezeilen gehören zum Paket davor.</summary>
    public static Dictionary<string, string> ParsePackageAnswer(string text, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        StringBuilder? current = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (AnswerLine().Match(line) is { Success: true } m)
            {
                // unbekanntes oder doppeltes Paket: nicht dem vorigen anhängen
                var name = m.Groups["name"].Value;
                current = wanted.Contains(name) && !found.ContainsKey(name) ? found[name] = new StringBuilder(m.Groups["text"].Value.Trim()) : null;
                continue;
            }

            if (current is not null)
            {
                current.Append(current.Length > 0 ? " " : "").Append(line);
            }
        }

        return found.Where(f => f.Value.Length > 0)
            .ToDictionary(f => f.Key, f => Truncate(f.Value.ToString(), 1_000), StringComparer.OrdinalIgnoreCase);
    }

    private static string PackagePrompt(string language) =>
        "You summarize Debian/Ubuntu/Proxmox package changelogs for a system administrator who decides whether to install updates. " +
        $"Answer in {(language == "en" ? "English" : "German")}. " +
        "The input lists one or more packages, each starting with '### <name>'. For EVERY package write exactly one line in the form " +
        "'<name>: <summary>' with the package name exactly as given, in the same order, nothing else (no heading, no preamble, no markdown). " +
        "The summary is 1 to 3 short sentences: what the update fixes or changes, security fixes first with their CVE numbers, " +
        "and say so if a service restart or a reboot is likely needed (kernel, libc, systemd, firmware). " +
        "Only use what the changelog says; if it only says 'new upstream release', say that.";

    private static string HostPrompt(string language) =>
        "You summarize the pending package updates of one server for its administrator. " +
        $"Answer in {(language == "en" ? "English" : "German")}. " +
        "Write 3 to 6 short bullet points ('- ' at the start of each line), no heading: what the updates bring overall, " +
        "security fixes first (name the most important CVEs or packages), then notable changes or risks (kernel or reboot, " +
        "major version jumps, changed defaults or configuration, services that will restart). Group small fixes instead of listing every package. " +
        "Only use the information given.";

    private static string Excerpt(IReadOnlyList<UpdateChangelogs.Section> sections, int max)
    {
        var text = string.Join("\n\n", sections.Select(s => s.Version is null ? s.Text : $"{s.Version}:\n{s.Text}"));
        return Truncate(text, max);
    }

    public static string Fingerprint(IEnumerable<string> items) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", items.OrderBy(i => i, StringComparer.Ordinal))))).ToLowerInvariant();

    private static string Truncate(string text, int max = 300) => text.Length > max ? text[..max] : text;
}
