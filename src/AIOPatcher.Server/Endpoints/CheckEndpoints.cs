using System.Diagnostics;
using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Testseite: prüft auf Knopfdruck, ob alles funktioniert, bevor man sich nachts darauf verlässt.
/// Status: Ok, Warning, Failed. Alle Prüfungen außer "Snapshot" verändern nichts.
/// </summary>
public static class CheckEndpoints
{
    public enum CheckStatus
    {
        Ok,
        Warning,
        Failed,
    }

    public sealed record CheckResult(string Key, CheckStatus Status, string Message, long DurationMs);
    public sealed record WhoAmI(string Name, string Role, string AuthType);

    public static void MapCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/checks").RequireAuthorization();
        api.MapGet("/server", Server);
        api.MapPost("/external", External);
        api.MapPost("/hosts/{id:guid}/agent", Agent);
        api.MapPost("/hosts/{id:guid}/backup", Backup);
        api.MapPost("/hosts/{id:guid}/restore", Restore);
        api.MapPost("/hosts/{id:guid}/snapshot", Snapshot).RequireAuthorization(UserAccounts.OperatorPolicy);

        app.MapGet("/api/whoami", (ClaimsPrincipal user) => new WhoAmI(
            user.Identity?.Name ?? "?",
            user.FindFirstValue(ClaimTypes.Role) ?? "?",
            user.HasClaim(c => c.Type == "aiopatcher:token") ? "token" : "session")).RequireAuthorization();
    }

    private static async Task<CheckResult> Measure(string key, Func<Task<(CheckStatus, string)>> check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var (status, message) = await check();
            return new CheckResult(key, status, message, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IntegrationException or HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new CheckResult(key, CheckStatus.Failed, ex is TaskCanceledException ? L.T("Zeitüberschreitung.", "Timed out.") : ex.Message, watch.ElapsedMilliseconds);
        }
    }

    // --- Server ---------------------------------------------------------------------------------------

    private static async Task<List<CheckResult>> Server(HttpContext http, AppDbContext db, AIOPatcherOptions options, CertificateAuthority ca)
    {
        var now = DateTime.UtcNow;
        var results = new List<CheckResult>
        {
            await Measure("serverCertificate", () =>
            {
                var cert = ca.GetServerCertificate();
                var days = (cert.NotAfter.ToUniversalTime() - now).TotalDays;
                return Task.FromResult(days > 30
                    ? (CheckStatus.Ok, L.T($"Gültig bis {cert.NotAfter:yyyy-MM-dd}, wird automatisch erneuert.", $"Valid until {cert.NotAfter:yyyy-MM-dd}, renewed automatically."))
                    : (CheckStatus.Warning, L.T($"Läuft am {cert.NotAfter:yyyy-MM-dd} ab.", $"Expires on {cert.NotAfter:yyyy-MM-dd}.")));
            }),
            await Measure("caCertificate", () =>
            {
                var years = (ca.RootCertificate.NotAfter.ToUniversalTime() - now).TotalDays / 365;
                return Task.FromResult(years > 1
                    ? (CheckStatus.Ok, L.T($"Root-CA gültig bis {ca.RootCertificate.NotAfter:yyyy-MM-dd}.", $"Root CA valid until {ca.RootCertificate.NotAfter:yyyy-MM-dd}."))
                    : (CheckStatus.Warning, L.T("Root-CA läuft in unter einem Jahr ab.", "Root CA expires within a year.")));
            }),
            await Measure("diskSpace", () =>
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!);
                var freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
                var percent = 100d * drive.AvailableFreeSpace / drive.TotalSize;
                return Task.FromResult(percent > 10 && freeGb > 2
                    ? (CheckStatus.Ok, L.T($"{freeGb:0.0} GB frei ({percent:0} %).", $"{freeGb:0.0} GB free ({percent:0} %)."))
                    : (CheckStatus.Warning, L.T($"Nur noch {freeGb:0.0} GB frei ({percent:0} %).", $"Only {freeGb:0.0} GB free ({percent:0} %).")));
            }),
            await Measure("database", async () =>
            {
                var size = new FileInfo(options.DatabasePath).Length / 1024d / 1024;
                var check = await db.Database.SqlQueryRaw<string>("PRAGMA quick_check").ToListAsync();
                return check.FirstOrDefault() == "ok"
                    ? (CheckStatus.Ok, L.T($"Integrität ok, {size:0.0} MB.", $"Integrity ok, {size:0.0} MB."))
                    : (CheckStatus.Failed, string.Join("; ", check.Take(3)));
            }),
            await Measure("agentBuilds", () =>
            {
                var downloads = new AgentDownloads(options);
                var missing = AgentDownloads.RuntimeIds.Where(rid => downloads.Find(rid) is null).ToList();
                var unsigned = AgentDownloads.RuntimeIds.Where(rid => downloads.Find(rid) is { } f && !File.Exists(f.FullName + ".sig")).ToList();
                return Task.FromResult(missing.Count > 0
                    ? (CheckStatus.Warning, L.T($"Agent-Builds fehlen: {string.Join(", ", missing)}.", $"Agent builds missing: {string.Join(", ", missing)}."))
                    : unsigned.Count > 0
                        ? (CheckStatus.Warning, L.T("Agents nicht signiert: Self-Update ist aus.", "Agents not signed: self-update is off."))
                        : (CheckStatus.Ok, L.T($"Alle Agents vorhanden und signiert (Version {AgentDownloads.AgentVersion}).", $"All agents present and signed (version {AgentDownloads.AgentVersion}).")));
            }),
            await Measure("clientIp", () =>
            {
                var (ok, message) = TrustedProxies.Describe(http);
                return Task.FromResult((ok ? CheckStatus.Ok : CheckStatus.Warning, message));
            }),
        };
        return results;
    }

    // --- Externe Dienste ------------------------------------------------------------------------------

    private static async Task<List<CheckResult>> External(IHttpClientFactory httpFactory, FeatureFlags features, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(CveService.HttpClientName);
        var targets = new List<(string Key, string Url, bool Enabled)>
        {
            ("osv", "https://api.osv.dev/v1/vulns/CVE-2024-3094", features.IsOn(Feature.CveLookup)),
            ("kev", "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", features.IsOn(Feature.CveLookup)),
            ("epss", "https://api.first.org/data/v1/epss?cve=CVE-2024-3094", features.IsOn(Feature.CveLookup)),
            ("msrc", "https://api.msrc.microsoft.com/cvrf/v3.0/updates", features.IsOn(Feature.CveLookup)),
            ("dockerHub", "https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/alpine:pull", features.IsOn(Feature.ContainerChecks)),
        };

        var results = new List<CheckResult>();
        foreach (var (key, url, enabled) in targets)
        {
            if (!enabled)
            {
                results.Add(new CheckResult(key, CheckStatus.Warning, L.T("In den Einstellungen abgeschaltet.", "Disabled in the settings."), 0));
                continue;
            }

            results.Add(await Measure(key, async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return response.IsSuccessStatusCode
                    ? (CheckStatus.Ok, L.T("Erreichbar.", "Reachable."))
                    : (CheckStatus.Failed, $"HTTP {(int)response.StatusCode}");
            }));
        }

        return results;
    }

    // --- Pro Host -------------------------------------------------------------------------------------

    private static async Task<IResult> Agent(Guid id, AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var result = await Measure("agent", () =>
        {
            if (host.IsRevoked)
            {
                return Task.FromResult((CheckStatus.Failed, L.T("Agent ist gesperrt.", "Agent is revoked.")));
            }

            if (host.LastSeenAt is not { } seen)
            {
                return Task.FromResult((CheckStatus.Failed, L.T("Agent hat sich noch nie gemeldet.", "Agent has never checked in.")));
            }

            var ago = DateTime.UtcNow - seen;
            var text = L.T($"Letzter Kontakt vor {Ago(ago)}, Agent {host.AgentVersion}.", $"Last contact {Ago(ago)} ago, agent {host.AgentVersion}.");
            return Task.FromResult(ago <= options.OfflineAfter ? (CheckStatus.Ok, text) : (CheckStatus.Failed, text));
        });
        return Results.Ok(result);
    }

    private static async Task<IResult> Backup(Guid id, AppDbContext db, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        var (host, provider, problem) = await ResolveAsync(id, db, providers, ct);
        if (problem is not null)
        {
            return problem;
        }

        return Results.Ok(await Measure("backup", async () =>
        {
            var backup = await provider!.LatestBackupAsync(host!.ExternalId!, ct);
            if (backup is null)
            {
                return (CheckStatus.Failed, L.T("Kein Backup gefunden.", "No backup found."));
            }

            var age = DateTime.UtcNow - backup.CreatedAt;
            var text = L.T($"Letztes Backup vor {Ago(age)} ({backup.CreatedAt:yyyy-MM-dd HH:mm} UTC){Size(backup.SizeBytes)}.",
                $"Last backup {Ago(age)} ago ({backup.CreatedAt:yyyy-MM-dd HH:mm} UTC){Size(backup.SizeBytes)}.");
            return (age < TimeSpan.FromHours(26) ? CheckStatus.Ok : CheckStatus.Warning, text);
        }));
    }

    /// <summary>
    /// Wiederherstellbarkeit, ohne etwas zurückzuspielen: Gibt es ein Backup, und hat PBS es erfolgreich verifiziert?
    /// Ein echter Restore-Test (in eine Test-VM) ist bewusst nicht automatisch.
    /// </summary>
    private static async Task<IResult> Restore(Guid id, AppDbContext db, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        var (host, provider, problem) = await ResolveAsync(id, db, providers, ct);
        if (problem is not null)
        {
            return problem;
        }

        return Results.Ok(await Measure("restore", async () =>
        {
            var backup = await provider!.LatestBackupAsync(host!.ExternalId!, ct);
            return backup switch
            {
                null => (CheckStatus.Failed, L.T("Kein Backup vorhanden, also nichts wiederherstellbar.", "No backup, so nothing to restore.")),
                { VerifyState: "ok" } => (CheckStatus.Ok, L.T($"Letztes Backup ist verifiziert: {backup.Location}.", $"Latest backup is verified: {backup.Location}.")),
                { VerifyState: "failed" } => (CheckStatus.Failed, L.T($"Verifikation des letzten Backups FEHLGESCHLAGEN: {backup.Location}.", $"Verification of the latest backup FAILED: {backup.Location}.")),
                _ => (CheckStatus.Warning, L.T("Backup vorhanden, aber nicht verifiziert. In PBS einen Verify-Job einrichten.", "Backup exists but is not verified. Set up a verify job in PBS.")),
            };
        }));
    }

    /// <summary>Legt einen Test-Snapshot an, wartet auf das Ergebnis und löscht ihn wieder.</summary>
    private static async Task<IResult> Snapshot(Guid id, AppDbContext db, VirtualizationProviderFactory providers, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.SnapshotTests))
        {
            return Results.Ok(new CheckResult("snapshot", CheckStatus.Warning, FeatureFlags.DisabledMessage(Feature.SnapshotTests), 0));
        }

        var (host, provider, problem) = await ResolveAsync(id, db, providers, ct);
        if (problem is not null)
        {
            return problem;
        }

        var name = $"aiop_test_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = await Measure("snapshot", async () =>
        {
            var task = await provider!.StartSnapshotAsync(host!.ExternalId!, name, "AIOPatcher test (deleted again)", ct);
            var created = await WaitAsync(provider, host.ExternalId!, task, TimeSpan.FromMinutes(3), ct);
            if (created.State != ProviderTaskState.Succeeded)
            {
                return (CheckStatus.Failed, created.State == ProviderTaskState.Running
                    ? L.T($"Snapshot {name} nach 3 Minuten noch nicht fertig; bitte später manuell löschen.", $"Snapshot {name} not done after 3 minutes; please delete it manually later.")
                    : L.T($"Snapshot fehlgeschlagen: {created.Error}", $"Snapshot failed: {created.Error}"));
            }

            var deleteTask = await provider.DeleteSnapshotAsync(host.ExternalId!, name, ct);
            if (deleteTask is not null)
            {
                await WaitAsync(provider, host.ExternalId!, deleteTask, TimeSpan.FromMinutes(2), ct);
            }

            return (CheckStatus.Ok, L.T($"Snapshot {name} angelegt und wieder gelöscht.", $"Snapshot {name} created and deleted again."));
        });

        audit.Add("check.snapshot", $"{host!.Name}: {result.Status}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<ProviderTaskStatus> WaitAsync(IVirtualizationProvider provider, string externalId, string task, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var status = await provider.GetTaskAsync(externalId, task, ct);
            if (status.State != ProviderTaskState.Running || DateTime.UtcNow > deadline)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<(ManagedHost? Host, IVirtualizationProvider? Provider, IResult? Problem)> ResolveAsync(Guid id, AppDbContext db, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        var host = await db.Hosts.Include(h => h.Integration).SingleOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return (null, null, Results.NotFound());
        }

        if (host.Integration is not { Enabled: true } integration || host.ExternalId is null || providers.For(integration) is not { } provider)
        {
            return (host, null, Results.Ok(new CheckResult("mapping", CheckStatus.Warning,
                L.T("Keine Proxmox-/Hetzner-Zuordnung. Unter Host → Virtualisierung zuordnen.", "No Proxmox/Hetzner mapping. Assign it under host → Virtualization."), 0)));
        }

        return (host, provider, null);
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes < 120
        ? L.T($"{span.TotalMinutes:0} Min.", $"{span.TotalMinutes:0} min")
        : span.TotalHours < 48 ? L.T($"{span.TotalHours:0} Std.", $"{span.TotalHours:0} h") : L.T($"{span.TotalDays:0} Tagen", $"{span.TotalDays:0} days");

    private static string Size(long? bytes) => bytes is { } b ? $", {b / 1024d / 1024 / 1024:0.0} GB" : "";
}
