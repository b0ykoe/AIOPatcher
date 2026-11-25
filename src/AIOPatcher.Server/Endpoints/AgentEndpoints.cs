using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Agent-API auf dem Agent-Port (mTLS). Siehe <see cref="PortIsolation"/>.</summary>
public static class AgentEndpoints
{
    internal const string HostItemKey = "aiopatcher.agent.host";
    private const int MaxUpdatesPerCheckin = 5000;
    private const int MaxAddressesPerCheckin = 256;
    private const int MaxLogLinesPerRequest = 2000;
    private const int MaxLogLineLength = 4000;

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/agent");

        // Anonym: CA-Zertifikat ist öffentlich. Der Agent prüft es gegen den Fingerprint aus dem Installationsbefehl,
        // bevor er den Enrollment-Token schickt.
        agent.MapGet("/ca", (CertificateAuthority ca) => Results.Text(ca.RootCertificatePem, "application/x-pem-file"));
        agent.MapGet("/ping", () => Results.Ok(new { status = "ok" }));
        agent.MapPost("/enroll", Enroll).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        var authenticated = agent.MapGroup("").AddEndpointFilter(AuthenticateAgent);
        authenticated.MapPost("/checkin", Checkin); // Protokoll v1 (Push), nur noch für den Übergang
        authenticated.MapPullEndpoints();
        authenticated.MapPost("/renew", Renew);
        authenticated.MapGet("/hello", (HttpContext http) =>
        {
            var host = (ManagedHost)http.Items[HostItemKey]!;
            return Results.Ok(new HelloResponse(host.Id, host.Name, AgentDownloads.AgentVersion));
        });
        authenticated.MapPost("/jobs/{jobId:guid}/log", AppendJobLog);
        authenticated.MapPost("/jobs/{jobId:guid}/result", CompleteJob);
    }

    public static string HashEnrollmentToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    private static async Task<IResult> Enroll(
        EnrollRequest request,
        HttpContext http,
        AppDbContext db,
        CertificateAuthority ca,
        AuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AIOPatcher.Enrollment");
        var remote = http.Connection.RemoteIpAddress;
        var denied = Results.Problem(L.T("Enrollment-Token ungültig, abgelaufen oder bereits benutzt.", "Enrollment token invalid, expired or already used."),statusCode: StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem))
        {
            return Results.BadRequest();
        }

        var hash = HashEnrollmentToken(request.EnrollmentToken);
        var token = await db.EnrollmentTokens.Include(t => t.Site).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("Enrollment denied (invalid token) from {Remote}", remote);
            audit.Add("agent.enroll.denied", "Token invalid/expired/used",actor: $"agent:{remote}");
            await db.SaveChangesAsync(ct);
            return denied;
        }

        if (!AgentNetworkPolicy.IsAllowed(token.Site!.AllowedAgentCidrs, remote))
        {
            logger.LogWarning("Enrollment denied: {Remote} not in allowlist of site {Site}", remote, token.Site.Name);
            audit.Add("agent.enroll.denied", $"IP {remote} not in allowlist",siteId: token.SiteId, actor: $"agent:{remote}");
            await db.SaveChangesAsync(ct);
            return denied;
        }

        // Token atomar beanspruchen: Zwei gleichzeitige Enrollments mit demselben Token dürfen nicht beide durchgehen.
        var claimedAt = DateTime.UtcNow;
        var claimed = await db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, claimedAt), ct);
        if (claimed != 1)
        {
            return denied;
        }

        var hostname = Truncate(request.Hostname?.Trim(), 255);
        var hostId = Guid.NewGuid();
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = ca.IssueClientCertificate(request.CertificateSigningRequestPem, hostId, token.SiteId);
        }
        catch (CryptographicException ex)
        {
            return Results.Problem(L.T($"CSR ungültig: {ex.Message}", $"Invalid CSR: {ex.Message}"),statusCode: StatusCodes.Status400BadRequest);
        }

        using (certificate)
        {
            var host = new ManagedHost
            {
                Id = hostId,
                SiteId = token.SiteId,
                Name = string.IsNullOrEmpty(hostname) ? hostId.ToString()[..8] : hostname,
                Hostname = hostname,
                OsFamily = request.OsFamily,
                AgentVersion = Truncate(request.AgentVersion, 32),
                CertificateThumbprint = CertificateAuthority.GetIdentityThumbprint(certificate),
                CertificateExpiresAt = certificate.NotAfter.ToUniversalTime(),
            };
            db.Hosts.Add(host);

            token.UsedAt = claimedAt;
            token.UsedByHostId = hostId;
            audit.Add("agent.enrolled", $"{host.Name} ({request.OsFamily}) from {remote}", siteId: token.SiteId, hostId: hostId, actor: $"agent:{host.Name}");
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Agent {Host} enrolled in site {Site}", host.Name, token.Site.Name);
            return Results.Ok(new EnrollResponse(hostId, token.SiteId, certificate.ExportCertificatePem(), ca.RootCertificatePem));
        }
    }

    private static async ValueTask<object?> AuthenticateAgent(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        // Direkt: TLS-Client-Zertifikat. Über den Proxy-Port: von PublicChannelGate geprüftes, signiertes Zertifikat.
        var viaProxy = http.Items[Security.PublicChannelGate.CertificateItem] is System.Security.Cryptography.X509Certificates.X509Certificate2;
        var certificate = http.Items[Security.PublicChannelGate.CertificateItem] as System.Security.Cryptography.X509Certificates.X509Certificate2
                          ?? await http.Connection.GetClientCertificateAsync(http.RequestAborted);
        if (certificate is null)
        {
            return Results.Unauthorized();
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var thumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
        var host = await db.Hosts.Include(h => h.Site)
            .SingleOrDefaultAsync(h => h.CertificateThumbprint == thumbprint || h.PendingCertificateThumbprint == thumbprint, http.RequestAborted);

        if (host is null)
        {
            return Results.Unauthorized();
        }

        // Agent benutzt das erneuerte Zertifikat: ab jetzt gilt nur noch dieses.
        if (host.PendingCertificateThumbprint == thumbprint)
        {
            host.CertificateThumbprint = thumbprint;
            host.CertificateExpiresAt = host.PendingCertificateExpiresAt ?? certificate.NotAfter.ToUniversalTime();
            host.PendingCertificateThumbprint = null;
            host.PendingCertificateExpiresAt = null;
            host.CertificateExpiryNotifiedAt = null;
            db.AuditEntries.Add(new AuditEntry { Actor = $"agent:{host.Name}", Action = "agent.certificate.renewed", SiteId = host.SiteId, HostId = host.Id, Details = $"valid until {host.CertificateExpiresAt:u}" });
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var remote = viaProxy ? http.Items[Security.PublicChannelGate.ClientIpItem] as System.Net.IPAddress : http.Connection.RemoteIpAddress;
        if (host.IsRevoked || !AgentNetworkPolicy.IsAllowed(host.Site!.AllowedAgentCidrs, remote))
        {
            return Results.Problem(L.T("Agent gesperrt oder IP nicht erlaubt.", "Agent revoked or IP not allowed."),statusCode: StatusCodes.Status403Forbidden);
        }

        host.LastChannel = viaProxy ? "public" : "direct";
        http.Items[HostItemKey] = host;
        return await next(context);
    }

    private static async Task<IResult> Checkin(
        CheckinRequest request,
        HttpContext http,
        AppDbContext db,
        AIOPatcherOptions options,
        Notifier notifier,
        AgentDownloads downloads,
        FeatureFlags features,
        UpdateSigningKey updateKey,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection,
        CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[HostItemKey]!;
        var now = DateTime.UtcNow;

        // Alte Agents schicken bei jedem Check-in ungefragt Daten. Nur noch zum Aktualisieren auf den Pull-Agent.
        if (!features.IsOn(Feature.LegacyAgentProtocol))
        {
            return Results.Problem(L.T("Alter Check-in ist abgeschaltet: Agent per Installationsbefehl aktualisieren.",
                "Legacy check-in is turned off: update the agent with the install command."), statusCode: StatusCodes.Status410Gone);
        }

        host.ProtocolVersion = 1;
        host.Architecture = request.Architecture is "x64" or "arm64" ? request.Architecture : host.Architecture;

        if (request.UpdateReport is { } report)
        {
            RecordUpdateReport(db, notifier, host, report, now);
        }

        MarkSeen(host, notifier, now);
        host.AgentVersion = Truncate(request.AgentVersion, 32);
        host.Hostname = Truncate(request.Hostname, 255);
        host.OsFamily = request.OsFamily;
        host.OsName = Truncate(request.OsName, 200);
        host.OsVersion = Truncate(request.OsVersion, 100);
        host.KernelVersion = request.KernelVersion is null ? null : Truncate(request.KernelVersion, 100);
        host.Uptime = request.Uptime;
        host.BootedAt = now - request.Uptime;
        host.RebootRequired = request.RebootRequired;
        host.NativeAutoUpdatesEnabled = request.NativeAutoUpdatesEnabled;

        if (request.Updates is not null)
        {
            var newSecurity = await ReplaceUpdatesAsync(db, host, request.Updates, now, ct);
            if (newSecurity.Count > 0)
            {
                notifier.Publish(new Notification(NotificationEvents.NewSecurityUpdates,
                    L.T($"{host.Name}: {newSecurity.Count} neue Security-Updates", $"{host.Name}: {newSecurity.Count} new security updates"),
                    string.Join(", ", newSecurity.Take(10)) + (newSecurity.Count > 10 ? " …" : ""),
                    NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
            }
        }

        if (request.Software is not null)
        {
            await SoftwareEndpoints.ReplaceAsync(db, host, request.Software, ct);
        }

        if (request.Containers is not null)
        {
            await ContainerUpdateService.ReplaceContainersAsync(db, host, request.Containers, now, ct);
        }

        if (request.Compliance is not null && features.IsOn(Feature.ComplianceChecks))
        {
            await ComplianceEndpoints.StoreAsync(db, host, request.Compliance, now, ct);
        }

        await ReplaceAddressesAsync(db, host, request.Addresses ?? [], now, ct);
        var job = await DispatchJobAsync(db, host, request.RunningJobId, now, ct, features, ComplianceEndpoints.SecretProtector(protection));
        await db.SaveChangesAsync(ct);

        var renew = host.CertificateExpiresAt - now < CertificateRenewBefore;
        var selfUpdate = features.IsOn(Feature.AgentSelfUpdate);
        var update = job is null && selfUpdate ? downloads.OfferFor(host.OsFamily, request.Architecture, host.AgentVersion, host.AgentUpdateBlockedVersion) : null;
        return Results.Ok(new CheckinResponse(options.AgentCheckinIntervalSeconds, job, update, renew, selfUpdate ? updateKey.PublicKeySpkiBase64 : null,
            features.IsOn(Feature.ComplianceChecks)));
    }

    /// <summary>
    /// Ergebnis eines Self-Updates (meldet der Agent einmalig). Gescheiterte Versionen werden für diesen Host gesperrt,
    /// sonst würde der Agent sie nach dem Rollback sofort wieder angeboten bekommen.
    /// </summary>
    public static void RecordUpdateReport(AppDbContext db, Notifier notifier, ManagedHost host, AgentUpdateReport report, DateTime now)
    {
        host.AgentUpdateOutcome = report.Outcome;
        host.AgentUpdateFromVersion = Truncate(report.FromVersion, 32);
        host.AgentUpdateToVersion = Truncate(report.ToVersion, 32);
        host.AgentUpdateMessage = report.Message is null ? null : Truncate(report.Message, 1000);
        host.AgentUpdateAt = report.AtUtc > now || report.AtUtc == default ? now : report.AtUtc;

        if (report.Outcome == AgentUpdateOutcome.Installed)
        {
            host.AgentUpdateBlockedVersion = null;
            db.AuditEntries.Add(new AuditEntry { Actor = $"agent:{host.Name}", Action = "agent.updated", SiteId = host.SiteId, HostId = host.Id, Details = $"{report.FromVersion} → {report.ToVersion}" });
            return;
        }

        host.AgentUpdateBlockedVersion = host.AgentUpdateToVersion;
        var rolledBack = report.Outcome == AgentUpdateOutcome.RolledBack;
        db.AuditEntries.Add(new AuditEntry
        {
            Actor = $"agent:{host.Name}",
            Action = rolledBack ? "agent.update.rolled-back" : "agent.update.rejected",
            SiteId = host.SiteId,
            HostId = host.Id,
            Details = $"{report.FromVersion} → {report.ToVersion}: {host.AgentUpdateMessage}",
        });
        notifier.Publish(new Notification(NotificationEvents.AgentUpdateFailed,
            rolledBack
                ? L.T($"{host.Name}: Agent-Update auf {report.ToVersion} zurückgerollt", $"{host.Name}: agent update to {report.ToVersion} rolled back")
                : L.T($"{host.Name}: Agent-Update auf {report.ToVersion} abgelehnt", $"{host.Name}: agent update to {report.ToVersion} rejected"),
            (rolledBack
                ? L.T($"Version {report.FromVersion} läuft wieder. ", $"Version {report.FromVersion} is running again. ")
                : L.T($"Selbsttest fehlgeschlagen, Version {report.FromVersion} bleibt. ", $"Self-test failed, version {report.FromVersion} stays. "))
            + host.AgentUpdateMessage,
            NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
    }

    /// <summary>Host hat sich gemeldet: zuletzt gesehen, ggf. "wieder online" melden.</summary>
    internal static void MarkSeen(ManagedHost host, Notifier notifier, DateTime now)
    {
        if (host.OfflineNotifiedAt is not null)
        {
            host.OfflineNotifiedAt = null;
            notifier.Publish(new Notification(NotificationEvents.HostOnline,
                L.T($"{host.Name} ist wieder online", $"{host.Name} is back online"),
                L.T($"Site {host.Site?.Name}", $"Site {host.Site?.Name}"),
                NotificationSeverity.Success, host.SiteId, host.Id, $"/hosts/{host.Id}"));
        }

        host.LastSeenAt = now;
    }

    /// <summary>So lange vor Ablauf bittet der Server den Agent, ein neues Zertifikat anzufordern.</summary>
    public static readonly TimeSpan CertificateRenewBefore = TimeSpan.FromDays(60);

    private static async Task<IResult> Renew(RenewRequest request, HttpContext http, AppDbContext db, CertificateAuthority ca, CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[HostItemKey]!;
        if (host.CertificateExpiresAt - DateTime.UtcNow > CertificateRenewBefore)
        {
            return Results.Problem(L.T("Zertifikat ist noch lange gültig.", "Certificate is still valid for a long time."), statusCode: StatusCodes.Status409Conflict);
        }

        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = ca.IssueClientCertificate(request.CertificateSigningRequestPem ?? "", host.Id, host.SiteId);
        }
        catch (CryptographicException ex)
        {
            return Results.Problem(L.T($"CSR ungültig: {ex.Message}", $"Invalid CSR: {ex.Message}"), statusCode: StatusCodes.Status400BadRequest);
        }

        host.PendingCertificateThumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
        host.PendingCertificateExpiresAt = certificate.NotAfter.ToUniversalTime();
        db.AuditEntries.Add(new AuditEntry { Actor = $"agent:{host.Name}", Action = "agent.certificate.issued", SiteId = host.SiteId, HostId = host.Id, Details = $"renewal, valid until {host.PendingCertificateExpiresAt:u}" });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RenewResponse(certificate.ExportCertificatePem()));
    }

    /// <summary>
    /// Ein Agent führt immer nur einen Job aus. Meldet er sich ohne laufenden Job, obwohl einer als "Running" gilt,
    /// hat er ihn verloren (Absturz, Neustart ohne Ergebnis). Ergebnisse nach einem Neustart liefert der Agent
    /// immer vor dem Check-in ab, daher ist das eindeutig.
    /// </summary>
    /// <remarks>
    /// Letzte Kontrolle vor dem Agent: abgeschaltete Funktionen werden hier durchgesetzt, egal wie der Job entstanden ist
    /// (Dashboard, API-Token, Planer, Ablauf). Automatische Jobs warten bei Notaus, statt zu scheitern.
    /// </remarks>
    public static async Task<AgentJob?> DispatchJobAsync(AppDbContext db, ManagedHost host, Guid? runningJobId, DateTime now, CancellationToken ct, FeatureFlags? features = null,
        Microsoft.AspNetCore.DataProtection.IDataProtector? secrets = null)
    {
        var next = await SelectJobAsync(db, host, runningJobId, now, ct, features);
        if (next is null)
        {
            return null;
        }

        var holds = next.Type == JobType.Patch ? await PatchRules.HoldsForHostAsync(db, host, now, ct) : null;
        var packages = next.Type == JobType.AppUpdate ? next.Packages?.Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
        var change = next.Type == JobType.SystemChange ? ComplianceEndpoints.TakeChange(next, secrets) : null;
        return new AgentJob(next.Id, next.Type, next.Scope, next.AllowReboot, holds is { Count: > 0 } ? holds : null, packages, change);
    }

    /// <summary>Nächsten Job wählen (Durchsetzung der Schalter, Notaus, Wartungsfenster) und auf Running setzen. Für Check-in (v1) und Pull (v2).</summary>
    public static async Task<PatchJob?> SelectJobAsync(AppDbContext db, ManagedHost host, Guid? runningJobId, DateTime now, CancellationToken ct, FeatureFlags? features = null)
    {
        if (runningJobId is not null)
        {
            return null;
        }

        var lost = await db.Jobs.Where(j => j.HostId == host.Id && j.Status == JobStatus.Running).ToListAsync(ct);
        foreach (var job in lost)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.Summary = L.T("Agent hat den Job nicht abgeschlossen (Abbruch, Absturz oder Neustart ohne Ergebnis).",
                "The agent did not finish the job (cancelled, crashed or restarted without a result).");
        }

        var queued = await db.Jobs
            .Where(j => j.HostId == host.Id && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        // Auto-Jobs nur innerhalb ihres Wartungsfensters vergeben, danach verwerfen.
        foreach (var expired in queued.Where(j => j.NotAfter <= now))
        {
            AutoPatchScheduler.ExpireJob(expired, now);
            expired.ProtectedSecret = null;
        }

        PatchJob? next = null;
        foreach (var candidate in queued.Where(j => j.NotAfter is null || j.NotAfter > now))
        {
            if (features is null)
            {
                next = candidate;
                break;
            }

            var automated = candidate.RequestedBy == AutoPatchScheduler.Actor || candidate.RequestedBy.StartsWith("runbook:", StringComparison.Ordinal);
            if (automated && !features.IsOn(Feature.Automation))
            {
                continue; // Notaus: liegen lassen, verfällt ggf. am Fensterende
            }

            Feature? blocked = candidate.Type switch
            {
                JobType.Patch when !features.IsOn(Feature.PatchJobs) => Feature.PatchJobs,
                JobType.Reboot when !features.IsOn(Feature.Reboots) => Feature.Reboots,
                JobType.AppUpdate when !features.IsOn(Feature.AppUpdates) => Feature.AppUpdates,
                JobType.ContainerUpdate when !features.IsOn(Feature.AppUpdates) => Feature.AppUpdates,
                JobType.ContainerManage when !features.IsOn(Feature.ContainerManagement) => Feature.ContainerManagement,
                JobType.SystemChange when ComplianceEndpoints.BlockedFeature(candidate, features) is { } needed => needed,
                _ => null,
            };
            if (blocked is { } feature)
            {
                candidate.Status = JobStatus.Rejected;
                candidate.FinishedAt = now;
                candidate.Summary = FeatureFlags.DisabledMessage(feature);
                candidate.ProtectedSecret = null;
                ContainerManagementEndpoints.TakeRequest(candidate); // Compose-Inhalt nicht liegen lassen
                continue;
            }

            if (candidate.AllowReboot && candidate.Type == JobType.Patch && !features.IsOn(Feature.Reboots))
            {
                candidate.AllowReboot = false; // patchen ja, neu starten nein
            }

            next = candidate;
            break;
        }

        if (next is null)
        {
            return null;
        }

        next.Status = JobStatus.Running;
        next.StartedAt = now;
        if (next.Type == JobType.Patch)
        {
            next.UpdatesSnapshot = await ChangelogEndpoints.SnapshotAsync(db, next.HostId, next.Scope, ct);
        }

        return next;
    }

    private static async Task<IResult> AppendJobLog(Guid jobId, JobLogRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[HostItemKey]!;
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.HostId == host.Id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.Status != JobStatus.Running)
        {
            return Results.Conflict();
        }

        AppendLog(db, job, request);
        host.LastSeenAt = DateTime.UtcNow; // Lange Jobs: Host bleibt "online", obwohl er nicht eincheckt.
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static void AppendLog(AppDbContext db, PatchJob job, JobLogRequest request)
    {
        foreach (var line in (request.Lines ?? []).Take(MaxLogLinesPerRequest))
        {
            if (line is null)
            {
                continue;
            }

            db.JobLogEntries.Add(new JobLogEntry
            {
                JobId = job.Id,
                Timestamp = line.TimestampUtc == default ? DateTime.UtcNow : DateTime.SpecifyKind(line.TimestampUtc, DateTimeKind.Utc),
                Text = Truncate(line.Text, MaxLogLineLength),
            });
        }
    }

    private static async Task<IResult> CompleteJob(Guid jobId, JobResultRequest request, HttpContext http, AppDbContext db, AuditLog audit, Notifier notifier, CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[HostItemKey]!;
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.HostId == host.Id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        // Nach Neustart kann das Ergebnis zweimal ankommen oder der Job schon als verloren markiert sein:
        // dann gewinnt das echte Ergebnis des Agents.
        if (job.Status is not (JobStatus.Running or JobStatus.Failed))
        {
            return Results.Conflict();
        }

        await CompleteJobCoreAsync(db, audit, notifier, host, job, request.Outcome, request.Summary, request.RebootPerformed, ct);
        return Results.NoContent();
    }

    /// <summary>Job abschließen: Status, Audit, Benachrichtigung. Für Check-in (v1) und Pull (v2).</summary>
    internal static async Task CompleteJobCoreAsync(AppDbContext db, AuditLog audit, Notifier notifier, ManagedHost host, PatchJob job,
        JobOutcome outcome, string? summary, bool rebootPerformed, CancellationToken ct)
    {
        job.Status = outcome switch
        {
            JobOutcome.Succeeded => JobStatus.Succeeded,
            JobOutcome.Rejected => JobStatus.Rejected,
            _ => JobStatus.Failed,
        };
        job.Summary = summary is null ? null : Truncate(summary, 2000);
        job.RebootPerformed = rebootPerformed;
        job.FinishedAt = DateTime.UtcNow;
        host.LastSeenAt = DateTime.UtcNow;
        if (job.Status == JobStatus.Succeeded && job.Type == JobType.SystemChange)
        {
            ComplianceEndpoints.OnChangeSucceeded(host, job);
        }

        audit.Add($"job.{job.Status.ToString().ToLowerInvariant()}", $"{job.Type}: {job.Summary}", siteId: host.SiteId, hostId: host.Id, actor: $"agent:{host.Name}");
        await db.SaveChangesAsync(ct);

        var succeeded = job.Status == JobStatus.Succeeded;
        notifier.Publish(new Notification(
            succeeded ? NotificationEvents.JobSucceeded : NotificationEvents.JobFailed,
            succeeded
                ? L.T($"{host.Name}: {job.Type} erfolgreich", $"{host.Name}: {job.Type} succeeded")
                : L.T($"{host.Name}: {job.Type} fehlgeschlagen", $"{host.Name}: {job.Type} failed"),
            job.Summary ?? "",
            succeeded ? NotificationSeverity.Success : NotificationSeverity.Critical,
            host.SiteId, host.Id, $"/jobs/{job.Id}"));
    }

    /// <summary>Ersetzt die offenen Updates. Gibt die Namen neu aufgetauchter Security-Updates zurück (nicht beim ersten Scan).</summary>
    internal static async Task<List<string>> ReplaceUpdatesAsync(AppDbContext db, ManagedHost host, IReadOnlyList<AvailableUpdate> updates, DateTime now, CancellationToken ct)
    {
        var firstScan = host.LastScanAt is null;
        host.LastScanAt = now;
        var existing = await db.PendingUpdates.Where(u => u.HostId == host.Id).ToListAsync(ct);
        var known = existing.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
        // CVE-Bewertung vom vorherigen Scan übernehmen, damit sie nicht bis zum nächsten Abgleich verschwindet.
        var scored = existing
            .Where(u => u.CveCheckedAt != null)
            .GroupBy(u => (u.Name, u.CurrentVersion, u.AvailableVersion))
            .ToDictionary(g => g.Key, g => g.First());
        var newSecurity = new List<string>();
        var firstSeen = existing
            .GroupBy(u => (u.Name, u.AvailableVersion))
            .ToDictionary(g => g.Key, g => g.Min(u => u.FirstSeenAt));

        db.PendingUpdates.RemoveRange(existing);
        foreach (var update in updates.Where(u => u is not null && !string.IsNullOrWhiteSpace(u.Name)).Take(MaxUpdatesPerCheckin))
        {
            var name = Truncate(update.Name, 300);
            var version = update.AvailableVersion is null ? null : Truncate(update.AvailableVersion, 200);
            var added = new PendingUpdate
            {
                HostId = host.Id,
                Name = name,
                CurrentVersion = update.CurrentVersion is null ? null : Truncate(update.CurrentVersion, 200),
                AvailableVersion = version,
                IsSecurity = update.IsSecurity,
                Classification = update.Classification is null ? null : Truncate(update.Classification, 300),
                FirstSeenAt = firstSeen.GetValueOrDefault((name, version), now),
                SourcePackage = update.SourcePackage is null ? null : Truncate(update.SourcePackage, 200),
            };
            db.PendingUpdates.Add(added);
            if (scored.TryGetValue((added.Name, added.CurrentVersion, added.AvailableVersion), out var previous))
            {
                added.Cves = previous.Cves;
                added.IsKev = previous.IsKev;
                added.MaxEpss = previous.MaxEpss;
                added.CveCheckedAt = previous.CveCheckedAt;
            }

            if (update.IsSecurity && !firstScan && !known.Contains(name))
            {
                newSecurity.Add(name);
            }
        }

        return newSecurity;
    }

    internal static async Task ReplaceAddressesAsync(AppDbContext db, ManagedHost host, IReadOnlyList<NetworkAddress> addresses, DateTime now, CancellationToken ct)
    {
        var subnets = (await db.Subnets.Where(s => s.SiteId == host.SiteId).ToListAsync(ct))
            .Select(s => (s.Id, Network: IPNetwork.TryParse(s.Cidr, out var n) ? n : (IPNetwork?)null))
            .Where(s => s.Network is not null)
            .ToList();

        db.IpAddresses.RemoveRange(await db.IpAddresses.Where(a => a.HostId == host.Id && a.Source == IpSource.Agent).ToListAsync(ct));

        var seen = new HashSet<string>();
        foreach (var reported in addresses.Take(MaxAddressesPerCheckin))
        {
            if (reported is null || !IPAddress.TryParse(reported.Address, out var ip) || !AgentNetworkPolicy.IsReportable(ip))
            {
                continue;
            }

            var normalized = ip.ToString();
            if (!seen.Add(normalized))
            {
                continue;
            }

            db.IpAddresses.Add(new IpAddressEntry
            {
                SiteId = host.SiteId,
                HostId = host.Id,
                Address = normalized,
                PrefixLength = reported.PrefixLength is >= 0 and <= 128 ? reported.PrefixLength : null,
                InterfaceName = Truncate(reported.InterfaceName, 64),
                Source = IpSource.Agent,
                SubnetId = subnets.FirstOrDefault(s => s.Network!.Value.Contains(ip)).Id is var id && id != Guid.Empty ? id : null,
                LastSeenAt = now,
            });
        }
    }

    // Agents sind nicht vertrauenswürdig genug, um Längen und null-Werte ungeprüft zu übernehmen.
    internal static string Truncate(string? value, int max) =>
        value is null ? "" : value.Length <= max ? value : value[..max];
}
