using System.Net;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent;

/// <summary>
/// Hauptschleife des Agents:
/// 1. Ergebnis eines Jobs nach Neustart abliefern (falls vorhanden).
/// 2. Check-in mit Systemdaten, IPs und (seltener) offenen Updates.
/// 3. Bekommt er einen Job, führt er ihn sofort aus und meldet das Ergebnis.
/// </summary>
public sealed class CheckinService(
    AgentState state,
    IPlatformInventory inventory,
    IPatchPlatform patcher,
    JobExecutor executor,
    ILogger<CheckinService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private DateTime _nextScan = DateTime.MinValue;
    private IReadOnlyList<AvailableUpdate>? _freshUpdates;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AIOPatcher-Agent {Version} gestartet. Server {Server}, Host {HostId}",
            AgentState.AgentVersion, state.Config.ServerUrl, state.Config.HostId);

        using var client = ServerClient.FromState(state);
        var interval = TimeSpan.FromMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = interval;
            try
            {
                var waitingForRebootJob = await DeliverPendingResultAsync(client, stoppingToken);
                var response = await CheckinOnceAsync(client, waitingForRebootJob, stoppingToken);
                interval = TimeSpan.FromSeconds(Math.Max(response.CheckinIntervalSeconds, (int)MinInterval.TotalSeconds));
                wait = interval;

                if (response.Job is not null && waitingForRebootJob is null)
                {
                    await RunJobAsync(client, response.Job, stoppingToken);
                    wait = TimeSpan.FromSeconds(1); // sofort neuen Stand melden
                }
            }
            catch (ServerRejectedException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("Server lehnt diesen Agent ab ({Status}). Gesperrt oder gelöscht? Neu registrieren mit 'aiopatcher-agent enroll --force'.", ex.Status);
                wait = TimeSpan.FromMinutes(15);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Netzwerk, Server oder ein fehlgeschlagener Scan: weiterlaufen, der Dienst darf nicht sterben.
                logger.LogWarning(ex is HttpRequestException or ServerRejectedException or TaskCanceledException ? null : ex,
                    "Check-in fehlgeschlagen: {Message}", ex.Message);
                wait = RetryInterval < interval ? RetryInterval : interval;
            }

            await Task.Delay(wait, stoppingToken);
        }
    }

    private async Task<CheckinResponse> CheckinOnceAsync(ServerClient client, Guid? runningJobId, CancellationToken ct)
    {
        var updates = _freshUpdates;
        if (updates is null && DateTime.UtcNow >= _nextScan)
        {
            logger.LogInformation("Suche nach Updates …");
            updates = await inventory.ScanUpdatesAsync(ct);
            if (updates is not null)
            {
                logger.LogInformation("{Count} Updates verfügbar, davon {Security} Security", updates.Count, updates.Count(u => u.IsSecurity));
            }

            _nextScan = DateTime.UtcNow + ScanInterval;
        }

        var request = await BuildRequestAsync(inventory, updates, ct) with { RunningJobId = runningJobId };
        var response = await client.CheckinAsync(request, ct);
        _freshUpdates = null;
        logger.LogDebug("Check-in ok, nächster in {Seconds}s", response.CheckinIntervalSeconds);
        return response;
    }

    private async Task RunJobAsync(ServerClient client, AgentJob job, CancellationToken ct)
    {
        logger.LogInformation("Job {JobId} erhalten: {Type} ({Scope}, Neustart erlaubt: {AllowReboot})", job.JobId, job.Type, job.Scope, job.AllowReboot);

        // Policy bei jedem Job neu lesen: Änderungen gelten ohne Agent-Neustart.
        var policy = AgentPolicy.Load(state.Directory);
        JobExecutionResult result;
        await using (var log = new JobLog(client, job.JobId, logger))
        {
            result = await executor.ExecuteAsync(job, policy, log.Write, ct);
            if (result.ScannedUpdates is not null)
            {
                _freshUpdates = result.ScannedUpdates;
                _nextScan = DateTime.UtcNow + ScanInterval;
            }

            if (result.RebootRequested && await TryScheduleRebootAsync(job, result, log, ct))
            {
                logger.LogInformation("Neustart eingeplant, Ergebnis von Job {JobId} wird nach dem Neustart gemeldet", job.JobId);
                return;
            }

            if (result.RebootRequested)
            {
                result = result with { Outcome = JobOutcome.Failed, Summary = $"{result.Summary} Neustart konnte nicht ausgelöst werden." };
            }

            log.Write($"[agent] Ergebnis: {result.Outcome}. {result.Summary}");
        }

        try
        {
            await client.SendJobResultAsync(job.JobId, new JobResultRequest(result.Outcome, result.Summary, RebootPerformed: false), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException { Status: >= HttpStatusCode.InternalServerError }
                                   && !ct.IsCancellationRequested)
        {
            // Server gerade nicht erreichbar: Ergebnis aufheben und vor dem nächsten Check-in nachliefern.
            new PendingResult(job.JobId, result.Outcome, result.Summary, DateTime.UtcNow, AwaitReboot: false).Save(state.Directory);
            throw;
        }

        logger.LogInformation("Job {JobId}: {Outcome}. {Summary}", job.JobId, result.Outcome, result.Summary);
    }

    /// <summary>Ergebnis zuerst auf die Platte, dann Log senden, dann Neustart auslösen.</summary>
    private async Task<bool> TryScheduleRebootAsync(AgentJob job, JobExecutionResult result, JobLog log, CancellationToken ct)
    {
        var pending = new PendingResult(job.JobId, result.Outcome, result.Summary, DateTime.UtcNow);
        pending.Save(state.Directory);

        log.Write("[agent] Löse Neustart aus. Das Ergebnis wird nach dem Neustart gemeldet.");
        await log.FlushAsync(ct);

        if (await patcher.RequestRebootAsync(log.Write, ct))
        {
            return true;
        }

        PendingResult.Delete(state.Directory);
        return false;
    }

    /// <summary>
    /// Liefert ein nach Neustart offenes Ergebnis ab. Rückgabe: Job-ID, wenn wir noch auf den Neustart warten
    /// (dann darf der Server keinen neuen Job vergeben und den alten nicht als verloren markieren).
    /// </summary>
    private async Task<Guid?> DeliverPendingResultAsync(ServerClient client, CancellationToken ct)
    {
        var pending = PendingResult.Load(state.Directory);
        if (pending is null)
        {
            return null;
        }

        var bootTime = PendingResult.CurrentBootTimeUtc();
        JobResultRequest request;
        if (!pending.AwaitReboot)
        {
            request = new JobResultRequest(pending.Outcome, pending.Summary, RebootPerformed: false);
        }
        else if (pending.HasRebooted(bootTime))
        {
            request = new JobResultRequest(pending.Outcome, $"{pending.Summary} Neustart durchgeführt, wieder online seit {bootTime.ToLocalTime():dd.MM.yyyy HH:mm}.", RebootPerformed: true);
        }
        else if (DateTime.UtcNow - pending.RebootRequestedAtUtc < PendingResult.RebootGracePeriod)
        {
            return pending.JobId; // Neustart steht noch bevor
        }
        else
        {
            request = new JobResultRequest(JobOutcome.Failed, $"{pending.Summary} Neustart wurde ausgelöst, hat aber nicht stattgefunden.", RebootPerformed: false);
        }

        try
        {
            await client.SendJobResultAsync(pending.JobId, request, ct);
            logger.LogInformation("Ergebnis von Job {JobId} nach Neustart gemeldet: {Outcome}", pending.JobId, request.Outcome);
        }
        catch (ServerRejectedException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            logger.LogWarning("Server kennt Job {JobId} nicht mehr ({Status}), Ergebnis verworfen", pending.JobId, ex.Status);
        }

        PendingResult.Delete(state.Directory);
        return null;
    }

    public static async Task<CheckinRequest> BuildRequestAsync(IPlatformInventory inventory, IReadOnlyList<AvailableUpdate>? updates, CancellationToken ct)
    {
        var info = await inventory.GetSystemInfoAsync(ct);
        return new CheckinRequest(
            ProtocolVersion: AgentProtocol.Version,
            AgentVersion: AgentState.AgentVersion,
            Hostname: Dns.GetHostName(),
            OsFamily: PlatformInfo.OsFamily,
            OsName: info.OsName,
            OsVersion: info.OsVersion,
            KernelVersion: info.KernelVersion,
            Uptime: TimeSpan.FromMilliseconds(Environment.TickCount64),
            RebootRequired: info.RebootRequired,
            NativeAutoUpdatesEnabled: info.NativeAutoUpdatesEnabled,
            Addresses: NetworkInventory.Collect(),
            Updates: updates,
            RunningJobId: null);
    }
}
