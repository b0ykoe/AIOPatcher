using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Platforms;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Jobs;

public sealed record JobExecutionResult(
    JobOutcome Outcome,
    string Summary,
    bool RebootRequested = false,
    IReadOnlyList<AvailableUpdate>? ScannedUpdates = null,
    IReadOnlyList<BrokenAptSource>? BrokenSources = null);

/// <summary>
/// Führt einen Job aus. Der Agent kennt nur diese drei Aktionen, keine Shell und keine freien Parameter.
/// Die lokale Policy hat immer das letzte Wort.
/// </summary>
public sealed class JobExecutor(
    IPlatformInventory inventory,
    IPatchPlatform patcher,
    ILogger<JobExecutor> logger,
    Compliance.IComplianceProvider? compliance = null,
    AgentState? state = null)
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
                JobType.AppUpdate => await AppUpdateAsync(job, policy, log, ct),
                JobType.SystemChange => await SystemChangeAsync(job, policy, log, ct),
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
        JobType.AppUpdate when !policy.AllowAppUpdates =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy forbids program updates (allowAppUpdates: false)."),
        JobType.AppUpdate when policy.AllowPatch != PatchPermission.All =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy does not allow program updates (needs allowPatch: All)."),
        JobType.Reboot when !policy.AllowReboot =>
            new JobExecutionResult(JobOutcome.Rejected, "Local policy forbids reboots."),
        JobType.SystemChange when NativeAutoUpdates.IsChange(job.Change) && !policy.AllowAutoUpdateChanges =>
            new JobExecutionResult(JobOutcome.Rejected,
                "Local policy forbids changing the system's automatic updates (allowAutoUpdateChanges: false)."),
        JobType.SystemChange when !NativeAutoUpdates.IsChange(job.Change) && !policy.AllowSystemChanges =>
            new JobExecutionResult(JobOutcome.Rejected,
                "Local policy forbids system changes. To allow firewall, SSH/RDP and user changes on this host: aiopatcher-agent policy set allowSystemChanges true"),
        _ => null,
    };

    /// <summary>Firewall, SSH/RDP, Benutzer: nur feste Aktionen, Werte prüft der jeweilige Provider.</summary>
    private async Task<JobExecutionResult> SystemChangeAsync(AgentJob job, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        if (CheckPolicy(job, policy) is { } rejected)
        {
            log($"[agent] Rejected: {rejected.Summary}");
            return rejected;
        }

        if (NativeAutoUpdates.IsChange(job.Change))
        {
            log($"[agent] Change: {job.Change!.Kind}");
            return await NativeAutoUpdates.SetAsync(job.Change.Kind == SystemChangeKind.AutoUpdatesOn, log, ct);
        }

        if (job.Change is not { } change || compliance is null || state is null)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "System changes are not available in this agent.");
        }

        if (change.Kind != SystemChangeKind.Batch)
        {
            log($"[agent] Change: {change.Kind}{(change.UserName is null ? "" : $" ({change.UserName})")}");
            return await compliance.ApplyAsync(change, ServerReachableAsync, log, ct);
        }

        return await BatchAsync(change.Steps ?? [], log, ct);
    }

    /// <summary>Compliance-Vorgabe angleichen: Schritte nacheinander, beim ersten Fehler Schluss (Rest bleibt offen).</summary>
    private async Task<JobExecutionResult> BatchAsync(IReadOnlyList<SystemChange> steps, Action<string> log, CancellationToken ct)
    {
        if (steps.Count is 0 or > 50 || steps.Any(s => s.Kind is SystemChangeKind.Batch || NativeAutoUpdates.IsChange(s)))
        {
            return new JobExecutionResult(JobOutcome.Rejected, "Batch must contain 1 to 50 plain changes.");
        }

        var done = new List<string>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            log($"[agent] Step {i + 1}/{steps.Count}: {step.Kind}{(step.UserName is null ? "" : $" ({step.UserName})")}");
            var result = await compliance!.ApplyAsync(step, ServerReachableAsync, log, ct);
            log($"[agent] -> {result.Outcome}: {result.Summary}");
            if (result.Outcome != JobOutcome.Succeeded)
            {
                return result with
                {
                    Summary = $"Step {i + 1}/{steps.Count} ({step.Kind}) {result.Outcome.ToString().ToLowerInvariant()}: {result.Summary}"
                              + (done.Count > 0 ? $" Done before: {string.Join(" ", done)}" : ""),
                };
            }

            done.Add(result.Summary);
        }

        return new JobExecutionResult(JobOutcome.Succeeded, string.Join(" ", done));
    }

    /// <summary>Meldet sich der Server mit unserem Zertifikat? (Nach Firewall-Änderungen, sonst Rollback.)</summary>
    private async Task<bool> ServerReachableAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = ServerClient.FromState(state!);
            await client.HelloAsync(timeout.Token);
            return true;
        }
        catch (ServerRejectedException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return true; // älterer Server ohne /agent/hello, aber erreichbar
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ServerRejectedException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<JobExecutionResult> ScanAsync(Action<string> log, CancellationToken ct)
    {
        var updates = await inventory.ScanUpdatesAsync(ct);
        if (updates is null)
        {
            return new JobExecutionResult(JobOutcome.Failed, "Update scan is not available on this platform yet.");
        }

        var summary = $"{updates.Count} updates available, {updates.Count(u => u.IsSecurity)} of them security.";
        var broken = inventory.BrokenSources;
        if (broken is { Count: > 0 })
        {
            summary += $" Package source(s) not reachable: {string.Join("; ", broken.Select(b => b.Source))}.";
        }

        log($"[agent] {summary}");
        return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates, BrokenSources: broken);
    }

    /// <summary>Programme per winget aktualisieren, eins nach dem anderen; ein Fehler bricht die übrigen nicht ab.</summary>
    private static async Task<JobExecutionResult> AppUpdateAsync(AgentJob job, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        if (CheckPolicy(job, policy) is { } rejected)
        {
            log($"[agent] Rejected: {rejected.Summary}");
            return rejected;
        }

        if (!OperatingSystem.IsWindows() || Platforms.Windows.WingetClient.FindExecutable() is not { } winget)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "winget is not available on this host.");
        }

        var packages = (job.Packages ?? []).Where(p => p.Length is > 0 and <= 200 && !p.Any(char.IsWhiteSpace)).Distinct().ToList();
        if (packages.Count == 0)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, "Nothing to update.");
        }

        var failed = new List<string>();
        foreach (var id in packages)
        {
            var exit = await Platforms.Windows.WingetClient.UpgradeAsync(winget, id, log, ct);
            if (exit != 0)
            {
                failed.Add(id);
                log($"[agent] ERROR {id}: winget exited with code {exit}.");
            }
            else
            {
                log($"[agent] OK: {id} updated.");
            }
        }

        return failed.Count == 0
            ? new JobExecutionResult(JobOutcome.Succeeded, $"{packages.Count} program(s) updated.")
            : new JobExecutionResult(JobOutcome.Failed, $"{packages.Count - failed.Count} of {packages.Count} updated; failed: {string.Join(", ", failed)}.");
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

        var fix = job.FixBrokenSources && policy.AllowSourceFixes;
        var outcome = await patcher.PatchAsync(job.Scope, holds, fix, log, ct);
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
            return new JobExecutionResult(JobOutcome.Succeeded, summary, ScannedUpdates: updates, BrokenSources: inventory.BrokenSources);
        }

        if (job.AllowReboot && policy.AllowReboot)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, summary, RebootRequested: true, ScannedUpdates: updates, BrokenSources: inventory.BrokenSources);
        }

        var reason = job.AllowReboot ? "local policy forbids reboots" : "not requested";
        log($"[agent] Reboot required, not performed ({reason}).");
        return new JobExecutionResult(JobOutcome.Succeeded, $"{summary} Reboot required ({reason}).", ScannedUpdates: updates, BrokenSources: inventory.BrokenSources);
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
