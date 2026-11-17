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

    public async Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, bool fixBrokenSources, Action<string> log, CancellationToken ct)
    {
        var (updateExit, check) = await UpdateListsAsync(log, ct);
        var note = "";

        // Dauerhaft kaputte Fremdquellen (z. B. eingestelltes Repo) deaktivieren und die Listen neu laden
        var fixable = AptSources.Classify(check.Broken).Where(b => b.Fixable && b.File is not null).ToList();
        if (fixable.Count > 0 && !fixBrokenSources)
        {
            log("[agent] Broken package sources are not disabled automatically (switch in the dashboard or policy allowSourceFixes).");
        }
        else if (fixable.Count > 0)
        {
            var disabled = new List<string>();
            foreach (var file in fixable.Select(b => b.File!).Distinct())
            {
                if (AptSources.Disable(file) is { } moved)
                {
                    log($"[agent] Disabled broken package source {file} (moved to {moved}; to restore: mv {moved} {file}).");
                    disabled.Add(Path.GetFileName(file));
                }
            }

            if (disabled.Count > 0)
            {
                note = $" Disabled broken package source(s): {string.Join(", ", disabled)} (backup in {AptSources.BackupDirectory}).";
                (updateExit, check) = await UpdateListsAsync(log, ct);
            }
        }

        if (!check.CanContinue)
        {
            return new PatchOutcome(false, $"apt-get update failed (exit {updateExit}).{note}");
        }

        // Übrige kaputte Quellen: trotzdem patchen (die anderen Quellen sind aktuell), aber deutlich melden
        var warning = note + (check.Broken.Count == 0 ? "" : $" Warning: package source not reachable: {string.Join("; ", check.BrokenSources)}.");
        if (check.Broken.Count > 0)
        {
            log($"[agent] Warning: package source not reachable: {string.Join("; ", check.BrokenSources)}. Continuing with the other sources.");
        }

        string[] args;
        if (scope == PatchScope.Security || holds.Count > 0)
        {
            // Mit zurückgehaltenen Paketen gibt es kein "dist-upgrade außer X": stattdessen die Paketliste explizit
            // aktualisieren. Neue Abhängigkeiten (z. B. ein neuer Kernel) zieht apt dabei selbst mit.
            var simulation = await ProcessRunner.RunAsync("apt-get", ["-s", "-o", "Debug::NoLocking=1", "dist-upgrade"], UpdateTimeout, ct);
            var candidates = AptOutputParser.Parse(simulation.StandardOutput)
                .Where(u => (scope == PatchScope.All || u.IsSecurity) && u.CurrentVersion is not null) // nur Upgrades installierter Pakete
                .Select(u => u.Name)
                .Distinct()
                .ToList();

            var held = candidates.Where(p => HoldPatterns.IsHeld(holds, p)).ToList();
            if (held.Count > 0)
            {
                log($"[agent] Held back: {string.Join(", ", held)}");
            }

            var packages = candidates.Except(held).ToArray();
            if (packages.Length == 0)
            {
                var none = scope == PatchScope.Security ? "No pending security updates." : "No pending updates.";
                log($"[agent] {none}");
                return new PatchOutcome(true, (held.Count > 0 ? $"{none} {held.Count} held back." : none) + warning);
            }

            log($"[agent] {packages.Length} {(scope == PatchScope.Security ? "security " : "")}updates: {string.Join(", ", packages)}");
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
            ? new PatchOutcome(true, (summary ?? "Updates installed.") + warning)
            : new PatchOutcome(false, $"apt-get exited with code {exit}. See the log for details.");
    }

    private static async Task<(int Exit, AptUpdateCheck.Result Check)> UpdateListsAsync(Action<string> log, CancellationToken ct)
    {
        log("$ apt-get update");
        var lines = new List<string>();
        var exit = await RunPrivilegedAsync("apt-get", ["update"], UpdateTimeout, line =>
        {
            log(line);
            lines.Add(line);
        }, ct);
        return (exit, AptUpdateCheck.Analyze(exit, lines));
    }

    public async Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct)
    {
        // +1 Minute: Zeit, um Log und Ergebnis-Datei sauber abzuschließen. Angemeldete Nutzer bekommen eine Meldung.
        log("$ shutdown -r +1");
        var exit = await RunPrivilegedAsync("shutdown", ["-r", "+1", "AIOPatcher: reboot after patch job"], TimeSpan.FromSeconds(30), log, ct);
        return exit == 0;
    }

    private static Task<int> RunPrivilegedAsync(string command, string[] args, TimeSpan timeout, Action<string> onLine, CancellationToken ct) =>
        Environment.IsPrivilegedProcess
            ? ProcessRunner.RunStreamingAsync(command, args, timeout, onLine, ct)
            : ProcessRunner.RunStreamingAsync("sudo", ["-n", command, .. args], timeout, onLine, ct);
}
