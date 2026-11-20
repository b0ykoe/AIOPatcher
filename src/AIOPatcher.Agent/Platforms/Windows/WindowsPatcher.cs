using System.Runtime.Versioning;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPatcher : IPatchPlatform
{
    public async Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, bool fixBrokenSources, Action<string> log, CancellationToken ct)
    {
        var result = await WindowsUpdateApi.InstallAsync(scope, holds, log).WaitAsync(ct);
        return new PatchOutcome(result.Success, result.Summary);
    }

    public async Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct)
    {
        // Hart und sofort: /f schließt alle Programme ohne Rückfrage (auch ungespeicherte Dokumente), angemeldete Benutzer
        // werden ignoriert. Ohne Wartezeit kann ihn auch niemand mit "shutdown /a" abbrechen. Ergebnis und Log sind zu
        // diesem Zeitpunkt schon gespeichert bzw. gesendet. Bleibt der Neustart trotzdem aus, erzwingt PollService ihn erneut.
        log("$ shutdown.exe /r /f /t 0");
        var exit = await ProcessRunner.RunStreamingAsync(
            Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            ["/r", "/f", "/t", "0", "/d", "p:2:17", "/c", "AIOPatcher: reboot after patch job"],
            TimeSpan.FromSeconds(30), log, ct);
        return exit == 0;
    }

    /// <summary>
    /// Hart: sofort, ohne Rückfrage. Angemeldete Benutzer und offene Programme (auch ungespeicherte Dokumente) werden
    /// ignoriert und geschlossen. Ein noch wartender Neustart wird vorher abgebrochen, damit der neue sofort greift.
    /// </summary>
    public async Task<bool> ForceRebootNowAsync(Action<string> log, CancellationToken ct)
    {
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        await ProcessRunner.RunStreamingAsync(shutdown, ["/a"], TimeSpan.FromSeconds(15), _ => { }, ct);
        log("$ shutdown.exe /r /f /t 0");
        var exit = await ProcessRunner.RunStreamingAsync(shutdown, ["/r", "/f", "/t", "0", "/d", "p:2:17", "/c", "AIOPatcher: forced reboot after patch job"],
            TimeSpan.FromSeconds(30), log, ct);
        return exit == 0;
    }
}
