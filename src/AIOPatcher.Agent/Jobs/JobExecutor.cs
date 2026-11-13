using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Jobs;

public sealed record JobExecutionResult(
    JobOutcome Outcome,
    string Summary,
    bool RebootRequested = false,
    IReadOnlyList<AvailableUpdate>? ScannedUpdates = null);

/// <summary>
/// Führt einen Job aus. Der Agent kennt nur diese drei Aktionen, keine Shell und keine freien Parameter.
/// Die lokale Policy hat immer das letzte Wort.
/// </summary>
public sealed class JobExecutor(IPlatformInventory inventory, IPatchPlatform patcher, ILogger<JobExecutor> logger)
{
    public async Task<JobExecutionResult> ExecuteAsync(AgentJob job, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        log($"[agent] Job {job.Type} gestartet (Umfang {job.Scope}, Neustart angefordert: {(job.AllowReboot ? "ja" : "nein")}). " +
            $"Lokale Policy: Patchen {policy.AllowPatch}, Neustart {(policy.AllowReboot ? "erlaubt" : "verboten")}.");
        try
        {
            return job.Type switch
            {
                JobType.Scan => await ScanAsync(log, ct),
                JobType.Patch => await PatchAsync(job, policy, log, ct),
                JobType.Reboot => Reboot(policy, log),
                _ => new JobExecutionResult(JobOutcome.Rejected, $"Unbekannter Job-Typ {job.Type}."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} fehlgeschlagen", job.JobId);
            log($"[agent] Fehler: {ex.Message}");
            return new JobExecutionResult(JobOutcome.Failed, $"Fehler im Agent: {ex.Message}");
        }
    }

    public static JobExecutionResult? CheckPolicy(AgentJob job, AgentPolicy policy) => job.Type switch
    {
        JobType.Patch when policy.AllowPatch == PatchPermission.None =>
            new JobExecutionResult(JobOutcome.Rejected, "Lokale Policy verbietet das Patchen."),
        JobType.Patch when policy.AllowPatch == PatchPermission.Security && job.Scope == PatchScope.All =>
            new JobExecutionResult(JobOutcome.Rejected, "Lokale Policy erlaubt nur Security-Updates."),
        JobType.Reboot when !policy.AllowReboot =>
            new JobExecutionResult(JobOutcome.Rejected, "Lokale Policy verbietet Neustarts."),
        _ => null,
    };

    private async Task<JobExecutionResult> ScanAsync(Action<string> log, CancellationToken ct)
    {
        var updates = await inventory.ScanUpdatesAsync(ct);
        if (updates is null)
        {
            return new JobExecutionResult(JobOutcome.Failed, "Update-Scan ist auf dieser Plattform noch nicht verfügbar.");
        }

        var summary = $"{updates.Count} Updates verfügbar, davon {updates.Count(u => u.IsSecurity)} Security.";
        log($"[agent] {summary}");
        return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates);
    }

    private async Task<JobExecutionResult> PatchAsync(AgentJob job, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        if (CheckPolicy(job, policy) is { } rejected)
        {
            log($"[agent] Abgelehnt: {rejected.Summary}");
            return rejected;
        }

        var outcome = await patcher.PatchAsync(job.Scope, log, ct);
        if (!outcome.Success)
        {
            return new JobExecutionResult(JobOutcome.Failed, outcome.Summary);
        }

        // Frischer Stand fürs Dashboard: was ist noch offen, braucht es einen Neustart?
        var updates = await inventory.ScanUpdatesAsync(ct);
        var info = await inventory.GetSystemInfoAsync(ct);
        var summary = outcome.Summary.TrimEnd('.') + ".";
        if (updates is not null)
        {
            summary += $" Danach noch offen: {updates.Count} ({updates.Count(u => u.IsSecurity)} Security).";
        }

        if (!info.RebootRequired)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates);
        }

        if (job.AllowReboot && policy.AllowReboot)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, summary, RebootRequested: true, ScannedUpdates: updates);
        }

        var reason = job.AllowReboot ? "lokale Policy verbietet Neustarts" : "nicht angefordert";
        log($"[agent] Neustart erforderlich, wird nicht ausgeführt ({reason}).");
        return new JobExecutionResult(JobOutcome.Succeeded, $"{summary} Neustart erforderlich ({reason}).", ScannedUpdates: updates);
    }

    private static JobExecutionResult Reboot(AgentPolicy policy, Action<string> log)
    {
        if (CheckPolicy(new AgentJob(Guid.Empty, JobType.Reboot, PatchScope.Security, true), policy) is { } rejected)
        {
            log($"[agent] Abgelehnt: {rejected.Summary}");
            return rejected;
        }

        return new JobExecutionResult(JobOutcome.Succeeded, "Neustart angefordert.", RebootRequested: true);
    }
}
