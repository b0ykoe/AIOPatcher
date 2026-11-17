using System.Net;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Maintenance;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent;

/// <summary>
/// Hauptschleife des Pull-Agents:
/// 1. Antwort auf einen Auftrag, die über einen Neustart hinweg offen war, abliefern.
/// 2. Auftrag abholen (GET). Ohne Auftrag: warten, nichts senden.
/// 3. Auftrag vom zuständigen Modul ausführen lassen, Log-Zeilen und Ergebnis als Antwort darauf senden.
/// Ungefragt schickt der Agent nie Daten an den Server.
/// </summary>
public sealed class PollService(
    AgentState state,
    ModuleHost modules,
    IPatchPlatform patcher,
    SelfMaintenance maintenance,
    ILogger<PollService> logger) : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AIOPatcher agent {Version} (pull) started. Server {Server}, host {HostId}",
            AgentState.AgentVersion, state.Config.ServerUrl, state.Config.HostId);

        maintenance.Startup();
        using var client = ServerClient.FromState(state);
        var interval = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = interval;
            try
            {
                var waitingForReboot = await DeliverPendingResultAsync(client, stoppingToken);
                var response = await client.PollAsync(waitingForReboot, stoppingToken);
                maintenance.PollSucceeded(response.UpdateKey);
                LearnPublicUrl(client, response.PublicUrl);
                interval = TimeSpan.FromSeconds(Math.Max(response.PollIntervalSeconds, (int)MinInterval.TotalSeconds));
                wait = interval;

                if (response.Task is { } task && waitingForReboot is null)
                {
                    await RunTaskAsync(client, task, stoppingToken);
                    wait = TimeSpan.FromSeconds(1); // gleich den nächsten Auftrag abholen
                }
            }
            catch (ServerRejectedException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("Server rejects this agent ({Status}). Revoked or deleted? Re-enroll with 'aiopatcher-agent enroll --force'.", ex.Status);
                wait = TimeSpan.FromMinutes(15);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Netzwerk oder Server weg: weiterlaufen, der Dienst darf nicht sterben.
                logger.LogWarning(ex is HttpRequestException or ServerRejectedException or TaskCanceledException ? null : ex, "Poll failed: {Message}", ex.Message);
                wait = RetryInterval < interval ? RetryInterval : interval;
            }

            await Task.Delay(wait, stoppingToken);
        }
    }

    /// <summary>Öffentliche Adresse vom Server übernehmen und merken (gilt auch nach einem Neustart des Agents).</summary>
    private void LearnPublicUrl(ServerClient client, string? url)
    {
        var current = state.Config.PublicUrl;
        if (string.Equals(current ?? "", url ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            state.SaveConfig(state.Config with { PublicUrl = url });
            client.UsePublicUrl(url is null ? null : new Uri(url));
            logger.LogInformation("Public address {Url}", url ?? "(none)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not save the public address: {Message}", ex.Message);
        }
    }

    /// <summary>Über den öffentlichen Weg kommen Geheimnisse nur für diesen Agent verschlüsselt: hier öffnen.</summary>
    private AgentTask OpenSecrets(AgentTask task)
    {
        if (task.Action != ModuleActions.Change || task.Payload is not { } payload || !payload.GetRawText().Contains(PublicChannel.SealedPrefix, StringComparison.Ordinal)
            || PullJsonContext.FromElement(payload, PullJsonContext.Default.SystemChange) is not { } change)
        {
            return task;
        }

        using var key = state.ClientCertificate.GetECDsaPrivateKey() ?? throw new System.Security.Cryptography.CryptographicException("No ECDSA key.");
        SystemChange Open(SystemChange c) => c with { Secret = PublicChannel.Open(c.Secret, key), Steps = c.Steps?.Select(Open).ToList() };
        return task with { Payload = PullJsonContext.ToElement(Open(change), PullJsonContext.Default.SystemChange) };
    }

    private async Task RunTaskAsync(ServerClient client, AgentTask task, CancellationToken ct)
    {
        logger.LogInformation("Task {TaskId}: {Module}.{Action}{Via}", task.TaskId, task.Module, task.Action, client.LastUsedPublic ? " (via public address)" : "");
        task = OpenSecrets(task);

        // Policy bei jedem Auftrag neu lesen: Änderungen gelten ohne Agent-Neustart.
        var policy = AgentPolicy.Load(state.Directory);
        TaskOutcome outcome;
        await using (var log = new JobLog(client, task.TaskId, logger))
        {
            try
            {
                outcome = await modules.ExecuteAsync(task, new TaskContext(policy, log.Write), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Task {TaskId} failed", task.TaskId);
                outcome = new TaskOutcome(JobOutcome.Failed, $"Error in agent: {ex.Message}");
            }

            if (outcome.RebootRequested && await TryScheduleRebootAsync(task, outcome, log, ct))
            {
                logger.LogInformation("Reboot scheduled, the answer to task {TaskId} follows after the reboot", task.TaskId);
                return;
            }

            if (outcome.RebootRequested)
            {
                outcome = outcome with { Outcome = JobOutcome.Failed, Summary = $"{outcome.Summary} Reboot could not be triggered." };
            }
        }

        TaskResultResponse response;
        try
        {
            response = await client.SendTaskResultAsync(task.TaskId, new TaskResultRequest(outcome.Outcome, outcome.Summary, outcome.Payload), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException { Status: >= HttpStatusCode.InternalServerError }
                                   && !ct.IsCancellationRequested)
        {
            // Server gerade weg: Ergebnis (ohne Daten) aufheben und vor der nächsten Abfrage nachliefern.
            new PendingResult(task.TaskId, outcome.Outcome, outcome.Summary, DateTime.UtcNow, AwaitReboot: false).Save(state.Directory);
            throw;
        }

        logger.LogInformation("Task {TaskId}: {Outcome}. {Summary}", task.TaskId, outcome.Outcome, outcome.Summary);
        if (outcome.AfterAnswer is { } after)
        {
            await after(response);
        }
    }

    /// <summary>Ergebnis zuerst auf die Platte, dann Log senden, dann Neustart auslösen.</summary>
    private async Task<bool> TryScheduleRebootAsync(AgentTask task, TaskOutcome outcome, JobLog log, CancellationToken ct)
    {
        new PendingResult(task.TaskId, outcome.Outcome, outcome.Summary, DateTime.UtcNow).Save(state.Directory);
        log.Write("[agent] Triggering reboot. The result will be reported after the reboot.");
        await log.FlushAsync(ct);

        if (await patcher.RequestRebootAsync(log.Write, ct))
        {
            return true;
        }

        PendingResult.Delete(state.Directory);
        return false;
    }

    /// <summary>
    /// Liefert eine offene Antwort ab (nach Neustart oder Verbindungsabbruch). Rückgabe: Auftrags-ID, solange wir noch
    /// auf den Neustart warten (dann vergibt der Server nichts Neues und hält den Auftrag offen).
    /// </summary>
    private async Task<Guid?> DeliverPendingResultAsync(ServerClient client, CancellationToken ct)
    {
        var pending = PendingResult.Load(state.Directory);
        if (pending is null)
        {
            return null;
        }

        var bootTime = PendingResult.CurrentBootTimeUtc();
        TaskResultRequest request;
        if (!pending.AwaitReboot)
        {
            request = new TaskResultRequest(pending.Outcome, pending.Summary);
        }
        else if (pending.HasRebooted(bootTime))
        {
            request = new TaskResultRequest(pending.Outcome, $"{pending.Summary} Rebooted, back online since {bootTime.ToLocalTime():yyyy-MM-dd HH:mm}.", RebootPerformed: true);
        }
        else if (DateTime.UtcNow - pending.RebootRequestedAtUtc < PendingResult.RebootGracePeriod)
        {
            return pending.JobId; // Neustart steht noch bevor
        }
        else
        {
            request = new TaskResultRequest(JobOutcome.Failed, $"{pending.Summary} Reboot was triggered but did not happen.");
        }

        try
        {
            await client.SendTaskResultAsync(pending.JobId, request, ct);
            logger.LogInformation("Answer to task {TaskId} delivered: {Outcome}", pending.JobId, request.Outcome);
        }
        catch (ServerRejectedException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            logger.LogWarning("Server no longer knows task {TaskId} ({Status}), answer discarded", pending.JobId, ex.Status);
        }

        PendingResult.Delete(state.Directory);
        return null;
    }
}
