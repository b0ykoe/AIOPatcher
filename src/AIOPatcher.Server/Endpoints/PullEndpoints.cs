using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Protokoll v2 (Pull-Agent): Der Agent holt Aufträge ab und antwortet nur darauf. Der Server plant, welche Daten
/// er wann braucht (siehe <see cref="ServerModule"/>), und vergibt Jobs aus dem Dashboard als Aufträge.
/// </summary>
public static class PullEndpoints
{
    public static void MapPullEndpoints(this RouteGroupBuilder authenticated)
    {
        authenticated.MapGet("/v2/poll", Poll);
        authenticated.MapPost("/v2/tasks/{taskId:guid}/log", TaskLog);
        authenticated.MapPost("/v2/tasks/{taskId:guid}/result", TaskResult);
    }

    private static async Task<IResult> Poll(
        Guid? running,
        HttpContext http,
        AppDbContext db,
        AIOPatcherOptions options,
        Notifier notifier,
        AgentDownloads downloads,
        FeatureFlags features,
        UpdateSigningKey updateKey,
        IDataProtectionProvider protection,
        PublicAccess publicAccess,
        ScanSchedule schedule,
        CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[AgentEndpoints.HostItemKey]!;
        var now = DateTime.UtcNow;
        AgentEndpoints.MarkSeen(host, notifier, now);
        host.ProtocolVersion = PullProtocol.Version;
        var polledVersion = http.Request.Headers[PullProtocol.VersionHeader].ToString() is { Length: > 0 and <= 32 } v ? v : null;
        NoteInstance(host, http.Request.Headers[PullProtocol.InstanceHeader].ToString(), now);

        AgentTask? task = null;
        if (running is null)
        {
            task = await NextTaskAsync(db, host, notifier, features, downloads, ComplianceEndpoints.SecretProtector(protection), polledVersion, now, ct, schedule);
        }

        // Über den Proxy sieht der Proxy (evtl. Cloudflare) den Inhalt: Geheimnisse nur für den Agent verschlüsselt
        if (task is not null && http.Items[PublicChannelGate.CertificateItem] is System.Security.Cryptography.X509Certificates.X509Certificate2 agentCert)
        {
            task = SealSecrets(task, agentCert);
        }

        await db.SaveChangesAsync(ct);
        var key = features.IsOn(Feature.AgentSelfUpdate) ? updateKey.PublicKeySpkiBase64 : null;
        return Results.Ok(new PollResponse(options.AgentCheckinIntervalSeconds, task, key, publicAccess.AgentUrl));
    }

    /// <summary>Geheimnisse einer Systemänderung (auch in Batch-Schritten) für den Schlüssel des Agents versiegeln.</summary>
    public static AgentTask SealSecrets(AgentTask task, System.Security.Cryptography.X509Certificates.X509Certificate2 agentCert)
    {
        if (task.Action != ModuleActions.Change || PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.SystemChange) is not { } change)
        {
            return task;
        }

        using var key = agentCert.GetECDsaPublicKey();
        if (key is null)
        {
            return task;
        }

        SystemChange Seal(SystemChange c) => c with
        {
            Secret = c.Secret is { Length: > 0 } s && !s.StartsWith(PublicChannel.SealedPrefix, StringComparison.Ordinal) ? PublicChannel.Seal(s, key) : c.Secret,
            Steps = c.Steps?.Select(Seal).ToList(),
        };

        var sealedChange = Seal(change);
        return sealedChange == change ? task : task with { Payload = PullJsonContext.ToElement(sealedChange, PullJsonContext.Default.SystemChange) };
    }

    /// <summary>Neue Prozess-Kennung = Agent oder Host neu gestartet: Systemdaten (Uptime, Neustart nötig) gleich neu anfragen.</summary>
    public static void NoteInstance(ManagedHost host, string? instance, DateTime now)
    {
        if (instance is not { Length: > 0 and <= 64 } || instance == host.AgentInstance)
        {
            return;
        }

        host.AgentRestartedAt = host.AgentInstance is null ? null : now; // erste Kennung (Agent-Update auf diese Version) ist kein Neustart
        host.AgentInstance = instance;
    }

    /// <summary>
    /// Nächster Auftrag: zuerst Jobs (Dashboard, Planer, Abläufe), dann Datenanforderungen der Module nach Priorität.
    /// Offene Aufträge ohne Antwort gelten als verloren (der Agent arbeitet immer nur einen ab).
    /// </summary>
    public static async Task<AgentTask?> NextTaskAsync(AppDbContext db, ManagedHost host, Notifier notifier, FeatureFlags features, AgentDownloads downloads,
        IDataProtector? secrets, string? polledVersion, DateTime now, CancellationToken ct, ScanSchedule? schedule = null)
    {
        var lost = await db.AgentTasks.Where(t => t.HostId == host.Id && t.Status == JobStatus.Running).ToListAsync(ct);
        foreach (var t in lost)
        {
            t.Status = JobStatus.Cancelled; // zählt nicht als "angefordert": wird beim nächsten Mal neu angefordert
            t.FinishedAt = now;
            t.Summary = L.T("Keine Antwort des Agents (Absturz oder Neustart).", "No answer from the agent (crash or restart).");
        }

        if (lost.Count > 0)
        {
            await db.SaveChangesAsync(ct); // die Historie unten liest aus der Datenbank
        }

        // Vor Patch-/Neustart-Jobs den aktuellen Stand der Dienste holen (Vergleich nach dem Neustart)
        if (await SnapshotBeforeJobAsync(db, host, features, now, ct))
        {
            return Create(db, host, AgentModules.Services, ModuleActions.List, null, null);
        }

        // Jobs: der Agent bekommt sie nur, wenn das zuständige Modul nutzbar ist.
        while (await AgentEndpoints.SelectJobAsync(db, host, null, now, ct, features) is { } job)
        {
            var (module, action, payload) = await MapJobAsync(db, host, job, secrets, now, features.IsOn(Feature.AptSourceFixes), ct);
            if (!ModuleJson.Usable(host, module) || host.ReportedModules is not null && !ModuleJson.Supports(host, module, action))
            {
                job.Status = JobStatus.Rejected;
                job.FinishedAt = now;
                job.ProtectedSecret = null;
                job.Summary = L.T($"Modul „{module}“ ist auf diesem Host abgeschaltet oder nicht vorhanden.", $"Module \"{module}\" is disabled or not available on this host.");
                await db.SaveChangesAsync(ct); // sonst findet die nächste Abfrage denselben Job wieder
                continue;
            }

            return Create(db, host, module, action, payload, job.Id);
        }

        var history = (await db.AgentTasks.Where(t => t.HostId == host.Id && t.Status != JobStatus.Cancelled)
                .GroupBy(t => new { t.Module, t.Action })
                .Select(g => new { g.Key.Module, g.Key.Action, Last = g.Max(t => t.CreatedAt) })
                .ToListAsync(ct))
            .ToDictionary(x => (x.Module, x.Action), x => x.Last);
        var context = new ModuleContext(db, notifier, features, downloads, null, now, history, schedule);

        foreach (var module in ServerModule.All)
        {
            // Vor der ersten Beschreibung kennt der Server die Module des Agents nicht: dann nur system.describe.
            if (host.ReportedModules is null && module.Id != AgentModules.System)
            {
                continue;
            }

            if (ModuleJson.Usable(host, module.Id) && module.Due(host, context, polledVersion) is { } planned
                && (host.ReportedModules is null || ModuleJson.Supports(host, module.Id, planned.Action)))
            {
                return Create(db, host, module.Id, planned.Action, planned.Payload, null);
            }
        }

        return null;
    }

    /// <summary>
    /// Wartet ein Patch- oder Neustart-Job und ist der letzte Stand der Dienste älter als ein paar Minuten? Nicht kurz nach
    /// einem Start (Dienste kommen noch hoch, das wäre ein schlechter Vergleichsstand).
    /// </summary>
    private static async Task<bool> SnapshotBeforeJobAsync(AppDbContext db, ManagedHost host, FeatureFlags features, DateTime now, CancellationToken ct)
    {
        if (!features.IsOn(Feature.RebootCheck) || host.ReportedModules is null || !ModuleJson.Usable(host, AgentModules.Services)
            || !ModuleJson.Supports(host, AgentModules.Services, ModuleActions.List)
            || host.BootedAt is not { } booted || now - booted < RebootCheck.Settle)
        {
            return false;
        }

        var jobWaiting = await db.Jobs.AnyAsync(j => j.HostId == host.Id && j.Status == JobStatus.Queued
            && (j.Type == JobType.Reboot || j.Type == JobType.Patch && j.AllowReboot), ct);
        if (!jobWaiting)
        {
            return false;
        }

        var last = await db.AgentTasks
            .Where(t => t.HostId == host.Id && t.Module == AgentModules.Services && t.Action == ModuleActions.List && t.Status != JobStatus.Cancelled)
            .MaxAsync(t => (DateTime?)t.CreatedAt, ct);
        return last is null || now - last >= RebootCheck.FreshBeforeJob;
    }

    private static AgentTask Create(AppDbContext db, ManagedHost host, string module, string action, JsonElement? payload, Guid? jobId)
    {
        var record = new AgentTaskRecord { HostId = host.Id, Module = module, Action = action, JobId = jobId };
        db.AgentTasks.Add(record);
        return new AgentTask(record.Id, module, action, payload);
    }

    /// <summary>Job aus dem Dashboard → Auftrag an ein Modul.</summary>
    private static async Task<(string Module, string Action, JsonElement? Payload)> MapJobAsync(AppDbContext db, ManagedHost host, PatchJob job, IDataProtector? secrets, DateTime now, bool fixSources, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobType.Scan:
                return (AgentModules.Updates, ModuleActions.Scan, null);
            case JobType.Reboot:
                return (AgentModules.Updates, ModuleActions.Reboot, null);
            case JobType.Patch:
                var holds = await PatchRules.HoldsForHostAsync(db, host, now, ct);
                return (AgentModules.Updates, ModuleActions.Install,
                    PullJsonContext.ToElement(new InstallRequest(job.Scope, job.AllowReboot, holds.Count > 0 ? holds : null, fixSources), PullJsonContext.Default.InstallRequest));
            case JobType.ContainerUpdate:
                return (AgentModules.Docker, ModuleActions.AppUpdate,
                    PullJsonContext.ToElement(new AppUpdateRequest([job.Packages ?? ""]), PullJsonContext.Default.AppUpdateRequest));
            case JobType.AppUpdate:
                return (AgentModules.Software, ModuleActions.AppUpdate,
                    PullJsonContext.ToElement(new AppUpdateRequest(job.Packages?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []), PullJsonContext.Default.AppUpdateRequest));
            default:
                var change = ComplianceEndpoints.TakeChange(job, secrets) ?? new SystemChange(SystemChangeKind.UserLock);
                return (AgentModules.ForChange(change.Kind), ModuleActions.Change, PullJsonContext.ToElement(change, PullJsonContext.Default.SystemChange));
        }
    }

    private static async Task<IResult> TaskLog(Guid taskId, JobLogRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[AgentEndpoints.HostItemKey]!;
        var task = await db.AgentTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.HostId == host.Id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (task.Status != JobStatus.Running)
        {
            return Results.Conflict();
        }

        host.LastSeenAt = DateTime.UtcNow;
        // Logs gehören nur zu Jobs (Live-Log im Dashboard). Bei Datenanforderungen werden sie verworfen.
        if (task.JobId is { } jobId && await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, ct) is { Status: JobStatus.Running } job)
        {
            AgentEndpoints.AppendLog(db, job, request);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TaskResult(
        Guid taskId,
        TaskResultRequest request,
        HttpContext http,
        AppDbContext db,
        AuditLog audit,
        Notifier notifier,
        FeatureFlags features,
        AgentDownloads downloads,
        CertificateAuthority ca,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var host = (ManagedHost)http.Items[AgentEndpoints.HostItemKey]!;
        var (status, answer) = await ApplyResultAsync(db, audit, notifier, features, downloads, ca, host, taskId, request,
            loggerFactory.CreateLogger("AIOPatcher.Pull"), ct);
        return status switch
        {
            StatusCodes.Status404NotFound => Results.NotFound(),
            StatusCodes.Status409Conflict => Results.Conflict(),
            _ => Results.Ok(new TaskResultResponse(answer)),
        };
    }

    /// <summary>Antwort auf einen Auftrag übernehmen. Rückgabe: HTTP-Status und Daten für den Agent.</summary>
    public static async Task<(int Status, JsonElement? Answer)> ApplyResultAsync(AppDbContext db, AuditLog audit, Notifier notifier, FeatureFlags features,
        AgentDownloads downloads, CertificateAuthority? ca, ManagedHost host, Guid taskId, TaskResultRequest request, ILogger logger, CancellationToken ct)
    {
        var task = await db.AgentTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.HostId == host.Id, ct);
        if (task is null)
        {
            return (StatusCodes.Status404NotFound, null);
        }

        // Nach einem Neustart kann die Antwort nachkommen, wenn der Auftrag schon als verloren galt: dann gilt sie.
        if (task.Status is not (JobStatus.Running or JobStatus.Failed or JobStatus.Cancelled))
        {
            return (StatusCodes.Status409Conflict, null);
        }

        var now = DateTime.UtcNow;
        task.Status = request.Outcome switch
        {
            JobOutcome.Succeeded => JobStatus.Succeeded,
            JobOutcome.Rejected => JobStatus.Rejected,
            _ => JobStatus.Failed,
        };
        task.FinishedAt = now;
        task.Summary = request.Summary is null ? null : AgentEndpoints.Truncate(request.Summary, 2000);
        host.LastSeenAt = now;

        if (task.JobId is { } jobId && await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.HostId == host.Id, ct) is { Status: JobStatus.Running or JobStatus.Failed } job)
        {
            await AgentEndpoints.CompleteJobCoreAsync(db, audit, notifier, host, job, request.Outcome, request.Summary, request.RebootPerformed, ct);
        }

        JsonElement? answer = null;
        if (request.Outcome == JobOutcome.Succeeded && ServerModule.Find(task.Module, task.Action) is { } module)
        {
            try
            {
                var context = new ModuleContext(db, notifier, features, downloads, ca, now, new Dictionary<(string, string), DateTime>());
                answer = await module.HandleAsync(context, host, task, request, ct);
            }
            catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                logger.LogWarning("Answer to {Module}.{Action} from {Host} rejected: {Message}", task.Module, task.Action, host.Name, ex.Message);
                task.Status = JobStatus.Failed;
                task.Summary = AgentEndpoints.Truncate($"Server: {ex.Message}", 2000);
            }
        }

        await db.SaveChangesAsync(ct);
        return (StatusCodes.Status200OK, answer);
    }
}
