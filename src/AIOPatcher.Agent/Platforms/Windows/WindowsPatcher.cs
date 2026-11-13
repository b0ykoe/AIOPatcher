using System.Runtime.Versioning;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPatcher : IPatchPlatform
{
    // Installation über die Windows-Update-API folgt in Roadmap-Schritt 6.
    public Task<PatchOutcome> PatchAsync(PatchScope scope, Action<string> log, CancellationToken ct)
    {
        log("[agent] Windows-Updates installieren ist noch nicht implementiert (Roadmap Schritt 6).");
        return Task.FromResult(new PatchOutcome(false, "Windows-Patching folgt in einer späteren Version."));
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
