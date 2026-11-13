using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Agent-API auf dem Agent-Port (mTLS). Siehe <see cref="PortIsolation"/>.</summary>
public static class AgentEndpoints
{
    private const string HostItemKey = "aiopatcher.agent.host";
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
        authenticated.MapPost("/checkin", Checkin);
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
        var denied = Results.Problem("Enrollment-Token ungültig, abgelaufen oder bereits benutzt.", statusCode: StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem))
        {
            return Results.BadRequest();
        }

        var hash = HashEnrollmentToken(request.EnrollmentToken);
        var token = await db.EnrollmentTokens.Include(t => t.Site).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("Enrollment abgelehnt (Token ungültig) von {Remote}", remote);
            audit.Add("agent.enroll.denied", "Token ungültig/abgelaufen/benutzt", actor: $"agent:{remote}");
            await db.SaveChangesAsync(ct);
            return denied;
        }

        if (!AgentNetworkPolicy.IsAllowed(token.Site!.AllowedAgentCidrs, remote))
        {
            logger.LogWarning("Enrollment abgelehnt: {Remote} nicht in Allowlist der Site {Site}", remote, token.Site.Name);
            audit.Add("agent.enroll.denied", $"IP {remote} nicht in Allowlist", siteId: token.SiteId, actor: $"agent:{remote}");
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
            return Results.Problem($"CSR ungültig: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
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
            audit.Add("agent.enrolled", $"{host.Name} ({request.OsFamily}) von {remote}", siteId: token.SiteId, hostId: hostId, actor: $"agent:{host.Name}");
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Agent {Host} in Site {Site} registriert", host.Name, token.Site.Name);
            return Results.Ok(new EnrollResponse(hostId, token.SiteId, certificate.ExportCertificatePem(), ca.RootCertificatePem));
        }
    }

    private static async ValueTask<object?> AuthenticateAgent(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
        if (certificate is null)
        {
            return Results.Unauthorized();
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var thumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
        var host = await db.Hosts.Include(h => h.Site).SingleOrDefaultAsync(h => h.CertificateThumbprint == thumbprint, http.RequestAborted);

        if (host is null)
        {
            return Results.Unauthorized();
        }

        if (host.IsRevoked || !AgentNetworkPolicy.IsAllowed(host.Site!.AllowedAgentCidrs, http.Connection.RemoteIpAddress))
        {
            return Results.Problem("Agent gesperrt oder IP nicht erlaubt.", statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[HostItemKey] = host;
        return await next(context);
    }

    private static async Task<IResult> Checkin(
        CheckinRequest request,
        HttpContext http,
        AppDbContext db,
        AIOPatcherOptions options,
        CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[HostItemKey]!;
        var now = DateTime.UtcNow;

        host.LastSeenAt = now;
        host.AgentVersion = Truncate(request.AgentVersion, 32);
        host.Hostname = Truncate(request.Hostname, 255);
        host.OsFamily = request.OsFamily;
        host.OsName = Truncate(request.OsName, 200);
        host.OsVersion = Truncate(request.OsVersion, 100);
        host.KernelVersion = request.KernelVersion is null ? null : Truncate(request.KernelVersion, 100);
        host.Uptime = request.Uptime;
        host.RebootRequired = request.RebootRequired;
        host.NativeAutoUpdatesEnabled = request.NativeAutoUpdatesEnabled;

        if (request.Updates is not null)
        {
            await ReplaceUpdatesAsync(db, host, request.Updates, now, ct);
        }

        await ReplaceAddressesAsync(db, host, request.Addresses ?? [], now, ct);
        var job = await DispatchJobAsync(db, host, request.RunningJobId, now, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new CheckinResponse(options.AgentCheckinIntervalSeconds, job));
    }

    /// <summary>
    /// Ein Agent führt immer nur einen Job aus. Meldet er sich ohne laufenden Job, obwohl einer als "Running" gilt,
    /// hat er ihn verloren (Absturz, Neustart ohne Ergebnis). Ergebnisse nach einem Neustart liefert der Agent
    /// immer vor dem Check-in ab, daher ist das eindeutig.
    /// </summary>
    private static async Task<AgentJob?> DispatchJobAsync(AppDbContext db, ManagedHost host, Guid? runningJobId, DateTime now, CancellationToken ct)
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
            job.Summary = "Agent hat den Job nicht abgeschlossen (Abbruch, Absturz oder Neustart ohne Ergebnis).";
        }

        var next = await db.Jobs
            .Where(j => j.HostId == host.Id && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (next is null)
        {
            return null;
        }

        next.Status = JobStatus.Running;
        next.StartedAt = now;
        return new AgentJob(next.Id, next.Type, next.Scope, next.AllowReboot);
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

        host.LastSeenAt = DateTime.UtcNow; // Lange Jobs: Host bleibt "online", obwohl er nicht eincheckt.
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CompleteJob(Guid jobId, JobResultRequest request, HttpContext http, AppDbContext db, AuditLog audit, CancellationToken ct)
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

        job.Status = request.Outcome switch
        {
            JobOutcome.Succeeded => JobStatus.Succeeded,
            JobOutcome.Rejected => JobStatus.Rejected,
            _ => JobStatus.Failed,
        };
        job.Summary = request.Summary is null ? null : Truncate(request.Summary, 2000);
        job.RebootPerformed = request.RebootPerformed;
        job.FinishedAt = DateTime.UtcNow;
        host.LastSeenAt = DateTime.UtcNow;
        audit.Add($"job.{job.Status.ToString().ToLowerInvariant()}", $"{job.Type}: {job.Summary}", siteId: host.SiteId, hostId: host.Id, actor: $"agent:{host.Name}");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task ReplaceUpdatesAsync(AppDbContext db, ManagedHost host, IReadOnlyList<AvailableUpdate> updates, DateTime now, CancellationToken ct)
    {
        host.LastScanAt = now;
        var existing = await db.PendingUpdates.Where(u => u.HostId == host.Id).ToListAsync(ct);
        var firstSeen = existing
            .GroupBy(u => (u.Name, u.AvailableVersion))
            .ToDictionary(g => g.Key, g => g.Min(u => u.FirstSeenAt));

        db.PendingUpdates.RemoveRange(existing);
        foreach (var update in updates.Where(u => u is not null && !string.IsNullOrWhiteSpace(u.Name)).Take(MaxUpdatesPerCheckin))
        {
            var name = Truncate(update.Name, 300);
            var version = update.AvailableVersion is null ? null : Truncate(update.AvailableVersion, 200);
            db.PendingUpdates.Add(new PendingUpdate
            {
                HostId = host.Id,
                Name = name,
                CurrentVersion = update.CurrentVersion is null ? null : Truncate(update.CurrentVersion, 200),
                AvailableVersion = version,
                IsSecurity = update.IsSecurity,
                Classification = update.Classification is null ? null : Truncate(update.Classification, 300),
                FirstSeenAt = firstSeen.GetValueOrDefault((name, version), now),
            });
        }
    }

    private static async Task ReplaceAddressesAsync(AppDbContext db, ManagedHost host, IReadOnlyList<NetworkAddress> addresses, DateTime now, CancellationToken ct)
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
    private static string Truncate(string? value, int max) =>
        value is null ? "" : value.Length <= max ? value : value[..max];
}
