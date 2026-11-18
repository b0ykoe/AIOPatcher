using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Ai;

/// <summary>
/// KI-Zusammenfassungen der Patchnotes: je Paket (was bringt diese Version) und je Host (was bringen alle anstehenden
/// Updates zusammen). Läuft automatisch im Hintergrund, sobald neue Patchnotes da sind oder ein Scan die Liste ändert;
/// ein Host nach dem anderen, damit die KI nicht überrannt wird. Einstellungen gecacht, gelten ohne Neustart.
/// </summary>
public sealed class UpdateSummaries : BackgroundService
{
    /// <summary>Höchstens so viele Pakete je Durchlauf eines Hosts zusammenfassen (Rest beim nächsten Mal).</summary>
    public const int MaxPackagesPerRun = 40;

    private const string ProtectorPurpose = "AIOPatcher.AiKey.v1";
    private const int ChangelogChars = 12_000;

    /// <summary>Erst so lange nach dem Vormerken bearbeiten: das auslösende Ergebnis (Scan, Patchnotes) ist dann gespeichert.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private readonly Channel<(Guid HostId, DateTime At)> _queue = Channel.CreateUnbounded<(Guid, DateTime)>();
    private readonly HashSet<Guid> _queued = [];
    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly AiClient _ai;
    private readonly ILogger<UpdateSummaries> _logger;
    private volatile AiSettingsRecord _settings = new();

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
            if (at + Settle - DateTime.UtcNow is { Ticks: > 0 } wait)
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

        // 1) je Paket: was bringt diese Version gegenüber der installierten
        var done = 0;
        foreach (var update in updates)
        {
            if (done >= MaxPackagesPerRun || !records.TryGetValue((update.Name, update.AvailableVersion), out var record) || record.Text is null
                || record.Summary is not null && record.SummaryLanguage == settings.Language && record.SummaryModel == label)
            {
                continue;
            }

            var sections = UpdateChangelogs.Since(record.Text, update.CurrentVersion);
            var excerpt = Excerpt(sections);
            try
            {
                record.Summary = await _ai.CompleteAsync(settings, key, PackagePrompt(settings.Language),
                    $"Package: {update.Name}\nInstalled: {update.CurrentVersion ?? "(new)"}\nOffered: {update.AvailableVersion}\n\nChangelog since the installed version:\n{excerpt}",
                    1024, ct);
                record.SummaryError = null;
            }
            catch (AiException ex)
            {
                record.Summary = null;
                record.SummaryError = Truncate(ex.Message);
            }

            record.SummaryModel = label;
            record.SummaryLanguage = settings.Language;
            record.SummaryFrom = update.CurrentVersion;
            record.SummaryAt = now;
            done++;
            await db.SaveChangesAsync(ct);
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

        if (summary is null)
        {
            db.HostUpdateSummaries.Add(summary = new HostUpdateSummary { HostId = hostId, Fingerprint = fingerprint });
        }

        try
        {
            summary.Text = await _ai.CompleteAsync(settings, key, HostPrompt(settings.Language),
                $"{updates.Count} pending updates on one server:\n{Truncate(string.Join("\n", lines), 40_000)}", 2048, ct);
            summary.Error = null;
        }
        catch (AiException ex)
        {
            summary.Text = null;
            summary.Error = Truncate(ex.Message);
        }

        summary.Fingerprint = fingerprint;
        summary.Model = label;
        summary.UpdateCount = updates.Count;
        summary.CreatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Kurzer Testlauf für die Einstellungsseite.</summary>
    public Task<string> TestAsync(AiSettingsRecord settings, string? apiKey, CancellationToken ct) =>
        _ai.CompleteAsync(settings, apiKey, PackagePrompt(settings.Language),
            "Package: openssl\nInstalled: 3.0.15-1~deb12u1\nOffered: 3.0.16-1~deb12u1\n\nChangelog since the installed version:\n" +
            "openssl (3.0.16-1~deb12u1) bookworm-security; urgency=medium\n\n  * Import 3.0.16\n    - CVE-2024-13176 (Timing side-channel in ECDSA signature computation).\n" +
            "    - CVE-2024-9143 (Low-level invalid GF(2^m) parameters lead to OOB memory access).\n", 512, ct);

    private static string PackagePrompt(string language) =>
        "You summarize Debian/Ubuntu/Proxmox package changelogs for a system administrator who decides whether to install an update. " +
        $"Answer in {(language == "en" ? "English" : "German")}. " +
        "Write 1 to 3 short sentences, no heading, no preamble, no markdown list: what the update fixes or changes, security fixes first " +
        "with their CVE numbers, and say so if a service restart or a reboot is likely needed (kernel, libc, systemd, firmware). " +
        "Only use what the changelog says; if it only says 'new upstream release', say that.";

    private static string HostPrompt(string language) =>
        "You summarize the pending package updates of one server for its administrator. " +
        $"Answer in {(language == "en" ? "English" : "German")}. " +
        "Write 3 to 6 short bullet points ('- ' at the start of each line), no heading: what the updates bring overall, " +
        "security fixes first (name the most important CVEs or packages), then notable changes or risks (kernel or reboot, " +
        "major version jumps, changed defaults or configuration, services that will restart). Group small fixes instead of listing every package. " +
        "Only use the information given.";

    private static string Excerpt(IReadOnlyList<UpdateChangelogs.Section> sections, int max = ChangelogChars)
    {
        var text = string.Join("\n\n", sections.Select(s => s.Version is null ? s.Text : $"{s.Version}:\n{s.Text}"));
        return Truncate(text, max);
    }

    public static string Fingerprint(IEnumerable<string> items) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", items.OrderBy(i => i, StringComparer.Ordinal))))).ToLowerInvariant();

    private static string Truncate(string text, int max = 300) => text.Length > max ? text[..max] : text;
}
