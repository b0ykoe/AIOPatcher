using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Patcht über apt. Proxmox VE/PBS verlangen dist-upgrade (nie nur "upgrade"), deshalb ist "alles" immer dist-upgrade.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class AptPatcher : IPatchPlatform
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromHours(2);

    // Konfigurationsdateien nie interaktiv: bestehende Konfiguration behalten, neue Defaults nur wo unverändert.
    private static readonly string[] NonInteractive =
    [
        "-y",
        "-o", "DPkg::Lock::Timeout=600",
        "-o", "Dpkg::Options::=--force-confdef",
        "-o", "Dpkg::Options::=--force-confold",
    ];

    [GeneratedRegex(@"^\d+ upgraded, \d+ newly installed, \d+ to remove")]
    private static partial Regex SummaryLine();

    public async Task<PatchOutcome> PatchAsync(PatchScope scope, Action<string> log, CancellationToken ct)
    {
        log("$ apt-get update");
        var updateExit = await RunPrivilegedAsync("apt-get", ["update"], UpdateTimeout, log, ct);
        if (updateExit != 0)
        {
            return new PatchOutcome(false, $"apt-get update fehlgeschlagen (Exit {updateExit}).");
        }

        string[] args;
        if (scope == PatchScope.Security)
        {
            var simulation = await ProcessRunner.RunAsync("apt-get", ["-s", "-o", "Debug::NoLocking=1", "dist-upgrade"], UpdateTimeout, ct);
            var packages = AptOutputParser.Parse(simulation.StandardOutput)
                .Where(u => u.IsSecurity && u.CurrentVersion is not null) // nur Upgrades installierter Pakete
                .Select(u => u.Name)
                .Distinct()
                .ToArray();

            if (packages.Length == 0)
            {
                log("[agent] Keine Security-Updates offen.");
                return new PatchOutcome(true, "Keine Security-Updates offen.");
            }

            log($"[agent] {packages.Length} Security-Updates: {string.Join(", ", packages)}");
            args = [.. NonInteractive, "install", "--only-upgrade", .. packages];
        }
        else
        {
            args = [.. NonInteractive, "dist-upgrade"];
        }

        log($"$ apt-get {string.Join(' ', args)}");
        string? summary = null;
        var exit = await RunPrivilegedAsync("apt-get", args, UpgradeTimeout, line =>
        {
            log(line);
            if (SummaryLine().IsMatch(line))
            {
                summary = line.TrimEnd('.');
            }
        }, ct);

        return exit == 0
            ? new PatchOutcome(true, summary ?? "Updates installiert.")
            : new PatchOutcome(false, $"apt-get beendet mit Exit {exit}. Details im Log.");
    }

    public async Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct)
    {
        // +1 Minute: Zeit, um Log und Ergebnis-Datei sauber abzuschließen. Angemeldete Nutzer bekommen eine Meldung.
        log("$ shutdown -r +1");
        var exit = await RunPrivilegedAsync("shutdown", ["-r", "+1", "AIOPatcher: Neustart nach Patch-Job"], TimeSpan.FromSeconds(30), log, ct);
        return exit == 0;
    }

    private static Task<int> RunPrivilegedAsync(string command, string[] args, TimeSpan timeout, Action<string> onLine, CancellationToken ct) =>
        Environment.IsPrivilegedProcess
            ? ProcessRunner.RunStreamingAsync(command, args, timeout, onLine, ct)
            : ProcessRunner.RunStreamingAsync("sudo", ["-n", command, .. args], timeout, onLine, ct);
}
