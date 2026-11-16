using AIOPatcher.Agent;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

public class JobExecutorTests
{
    private sealed class FakeInventory(bool rebootRequired, IReadOnlyList<AvailableUpdate>? updates) : IPlatformInventory
    {
        public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct) =>
            Task.FromResult(new SystemInfo("Debian", "13", "6.12", rebootRequired, false));

        public Task<IReadOnlyList<AvailableUpdate>?> ScanUpdatesAsync(CancellationToken ct) => Task.FromResult(updates);
    }

    private sealed class FakePatcher(bool success = true) : IPatchPlatform
    {
        public int PatchCalls { get; private set; }
        public PatchScope? LastScope { get; private set; }
        public IReadOnlyList<string>? LastHolds { get; private set; }

        public Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, Action<string> log, CancellationToken ct)
        {
            PatchCalls++;
            LastScope = scope;
            LastHolds = holds;
            return Task.FromResult(new PatchOutcome(success, success ? "3 upgraded, 0 newly installed, 0 to remove" : "apt-get exited with code 100."));
        }

        public Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct) => Task.FromResult(true);
    }

    private static readonly AvailableUpdate[] Remaining = [new("libfoo", "1", "2", true, "Debian-Security")];

    private static Task<JobExecutionResult> Run(AgentJob job, AgentPolicy policy, FakePatcher patcher, bool rebootRequired = false, IReadOnlyList<AvailableUpdate>? updates = null) =>
        new JobExecutor(new FakeInventory(rebootRequired, updates ?? []), patcher, NullLogger<JobExecutor>.Instance)
            .ExecuteAsync(job, policy, _ => { }, CancellationToken.None);

    [Fact]
    public async Task Patch_RebootRequiredAndAllowed_RequestsReboot()
    {
        var patcher = new FakePatcher();
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.Security, AllowReboot: true), new AgentPolicy(), patcher, rebootRequired: true, updates: Remaining);

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.True(result.RebootRequested);
        Assert.Equal(PatchScope.Security, patcher.LastScope);
        Assert.Contains("Still pending afterwards: 1 (1 security)", result.Summary);
        Assert.Same(Remaining, result.ScannedUpdates);
    }

    [Fact]
    public async Task Patch_RebootRequiredButPolicyForbids_DoesNotReboot()
    {
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.All, AllowReboot: true), new AgentPolicy(AllowReboot: false), new FakePatcher(), rebootRequired: true);

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.False(result.RebootRequested);
        Assert.Contains("local policy forbids reboots", result.Summary);
    }

    [Fact]
    public async Task Patch_RebootRequiredButNotRequested_DoesNotReboot()
    {
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.All, AllowReboot: false), new AgentPolicy(), new FakePatcher(), rebootRequired: true);

        Assert.False(result.RebootRequested);
        Assert.Contains("not requested", result.Summary);
    }

    [Fact]
    public async Task Patch_AllUpdatesWithSecurityOnlyPolicy_IsRejectedWithoutTouchingSystem()
    {
        var patcher = new FakePatcher();
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.All, false), new AgentPolicy(PatchPermission.Security), patcher);

        Assert.Equal(JobOutcome.Rejected, result.Outcome);
        Assert.Equal(0, patcher.PatchCalls);
    }

    [Fact]
    public async Task Patch_PolicyNone_IsRejected()
    {
        var patcher = new FakePatcher();
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.Security, false), new AgentPolicy(PatchPermission.None), patcher);

        Assert.Equal(JobOutcome.Rejected, result.Outcome);
        Assert.Equal(0, patcher.PatchCalls);
    }

    [Fact]
    public async Task Patch_Failure_IsReportedAsFailed_WithoutReboot()
    {
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Patch, PatchScope.All, true), new AgentPolicy(), new FakePatcher(success: false), rebootRequired: true);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.False(result.RebootRequested);
    }

    [Fact]
    public async Task Reboot_RespectsPolicy()
    {
        var allowed = await Run(new AgentJob(Guid.NewGuid(), JobType.Reboot, PatchScope.Security, true), new AgentPolicy(), new FakePatcher());
        var forbidden = await Run(new AgentJob(Guid.NewGuid(), JobType.Reboot, PatchScope.Security, true), new AgentPolicy(AllowReboot: false), new FakePatcher());

        Assert.True(allowed.RebootRequested);
        Assert.Equal(JobOutcome.Rejected, forbidden.Outcome);
        Assert.False(forbidden.RebootRequested);
    }

    [Fact]
    public async Task Scan_ReturnsUpdates()
    {
        var result = await Run(new AgentJob(Guid.NewGuid(), JobType.Scan, PatchScope.Security, false), new AgentPolicy(), new FakePatcher(), updates: Remaining);

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Equal("1 updates available, 1 of them security.", result.Summary);
    }

    [Fact]
    public void PendingResult_DetectsWhetherRebootHappened()
    {
        var requested = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var pending = new PendingResult(Guid.NewGuid(), JobOutcome.Succeeded, "ok", requested);

        Assert.True(pending.HasRebooted(requested.AddMinutes(2)));   // gebootet nach dem Auslösen
        Assert.False(pending.HasRebooted(requested.AddDays(-3)));    // Bootzeit liegt davor: kein Neustart
    }

    [Fact]
    public void AgentPolicy_MissingFile_UsesDefaults_AndFileOverrides()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(new AgentPolicy(PatchPermission.All, true), AgentPolicy.Load(dir));

            File.WriteAllText(Path.Combine(dir, AgentPolicy.FileName), """
                {
                  // nur Security, keine Neustarts
                  "allowPatch": "Security",
                  "allowReboot": false
                }
                """);
            Assert.Equal(new AgentPolicy(PatchPermission.Security, false), AgentPolicy.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
