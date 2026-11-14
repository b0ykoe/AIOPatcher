using System.Runtime.Versioning;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPatcher : IPatchPlatform
{
    public async Task<PatchOutcome> PatchAsync(PatchScope scope, Action<string> log, CancellationToken ct)
    {
        var result = await WindowsUpdateApi.InstallAsync(scope, log).WaitAsync(ct);
        return new PatchOutcome(result.Success, result.Summary);
    }

    public async Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct)
    {
        log("$ shutdown.exe /r /t 60");
        var exit = await ProcessRunner.RunStreamingAsync(
            Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            ["/r", "/t", "60", "/d", "p:2:17", "/c", "AIOPatcher: Neustart nach Patch-Job"],
            TimeSpan.FromSeconds(30), log, ct);
        return exit == 0;
    }
}
