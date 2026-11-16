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
        log($"[agent] Job {job.Type} started (scope {job.Scope}, reboot requested: {(job.AllowReboot ? "yes" : "no")}). " +
            $"Local policy: patch {policy.AllowPatch}, reboot {(policy.AllowReboot ? "allowed" : "forbidden")}.");
        try
        {
            return job.Type switch
            {
                JobType.Scan => await ScanAsync(log, ct),
                JobType.Patch => await PatchAsync(job, policy, log, ct),
                JobType.Reboot => Reboot(policy, log),
                _ => new JobExecutionResult(JobOutcome.Rejected, $"Unknown job type {job.Type}."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} failed", job.JobId);
            log($"[agent] Error: {ex.Message}");
            return new JobExecutionResult(JobOutcome.Failed, $"Error in agent: {ex.Message}");
        }
    }

    public static JobExecutionResult? CheckPolicy(AgentJob job, AgentPolicy policy) => job.Type switch
    {
        JobType.Patch when policy.AllowPatch == PatchPermission.None =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy forbids patching."),
        JobType.Patch when policy.AllowPatch == PatchPermission.Security && job.Scope == PatchScope.All =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy only allows security updates."),
        JobType.Reboot when !policy.AllowReboot =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy forbids reboots."),
        _ => null,
    };

    private async Task<JobExecutionResult> ScanAsync(Action<string> log, CancellationToken ct)
    {
        var updates = await inventory.ScanUpdatesAsync(ct);
        if (updates is null)
        {
            return new JobExecutionResult(JobOutcome.Failed, "Update scan is not available on this platform yet.");
        }

        var summary = $"{updates.Count} updates available, {updates.Count(u => u.IsSecurity)} of them security.";
        log($"[agent] {summary}");
        return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates);
    }

    private async Task<JobExecutionResult> PatchAsync(AgentJob job, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        if (CheckPolicy(job, policy) is { } rejected)
        {
            log($"[agent] Rejected: {rejected.Summary}");
            return rejected;
        }

        var holds = job.Holds ?? [];
        if (holds.Count > 0)
        {
            log($"[agent] Held back by server rules: {string.Join(", ", holds)}");
        }

        var outcome = await patcher.PatchAsync(job.Scope, holds, log, ct);
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
            summary += $" Still pending afterwards: {updates.Count} ({updates.Count(u => u.IsSecurity)} security).";
        }

        if (!info.RebootRequired)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates);
        }

        if (job.AllowReboot && policy.AllowReboot)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, summary, RebootRequested: true, ScannedUpdates: updates);
        }

        var reason = job.AllowReboot ? "local policy forbids reboots" : "not requested";
        log($"[agent] Reboot required, not performed ({reason}).");
        return new JobExecutionResult(JobOutcome.Succeeded, $"{summary} Reboot required ({reason}).", ScannedUpdates: updates);
    }

    private static JobExecutionResult Reboot(AgentPolicy policy, Action<string> log)
    {
        if (CheckPolicy(new AgentJob(Guid.Empty, JobType.Reboot, PatchScope.Security, true), policy) is { } rejected)
        {
            log($"[agent] Rejected: {rejected.Summary}");
            return rejected;
        }

        return new JobExecutionResult(JobOutcome.Succeeded, "Reboot requested.", RebootRequested: true);
    }
}
