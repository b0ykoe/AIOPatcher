using System.Diagnostics;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Compliance;

/// <summary>Firewall, SSH/RDP und Benutzer lesen bzw. ändern. Eine Implementierung pro Plattform.</summary>
public interface IComplianceProvider
{
    Task<ComplianceReport> CollectAsync(CancellationToken ct);

    /// <param name="serverReachable">Erreicht der Agent den Server noch? Nach Firewall-Änderungen, sonst Rollback.</param>
    Task<JobExecutionResult> ApplyAsync(SystemChange change, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct);
}

/// <summary>
/// Sicherheitsnetz für Änderungen, die den Host aussperren können: Vor der Änderung wird ein Wächter außerhalb des
/// Agents gestartet (systemd-run bzw. abgekoppeltes PowerShell). Hat der Agent die Änderung nach der Wartezeit nicht
/// bestätigt (Marker-Datei noch da, z. B. weil er abgestürzt ist), stellt der Wächter die Sicherung wieder her.
/// </summary>
public static class RevertWatchdog
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(3);

    /// <summary>Linux: führt nach <see cref="Delay"/> das Skript aus, falls die Marker-Datei noch existiert.</summary>
    public static bool ScheduleLinux(string marker, string restoreScript, Action<string> log)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("systemd-run")
            {
                ArgumentList =
                {
                    $"--unit=aiopatcher-change-guard-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    "--collect",
                    $"--on-active={(int)Delay.TotalSeconds}",
                    "--timer-property=AccuracySec=5s",
                    "/bin/sh", "-c", $"test -f {Q(marker)} || exit 0; /bin/sh {Q(restoreScript)}; rm -f {Q(marker)}",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
            if (process is { HasExited: true, ExitCode: 0 })
            {
                log($"[agent] Safety net armed: restores the previous state in {Delay.TotalMinutes:0} minutes unless confirmed.");
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"[agent] systemd-run not available: {ex.Message}");
        }

        return false;
    }

    /// <summary>Windows: abgekoppeltes PowerShell, das nach <see cref="Delay"/> die Wiederherstellung ausführt.</summary>
    public static bool ScheduleWindows(string marker, string restoreCommand, Action<string> log)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"Start-Sleep -Seconds {(int)Delay.TotalSeconds}; if (Test-Path -LiteralPath {Q(marker)}) {{ {restoreCommand}; Remove-Item -LiteralPath {Q(marker)} -Force }}",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            log($"[agent] Safety net armed: restores the previous state in {Delay.TotalMinutes:0} minutes unless confirmed.");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"[agent] Could not start the safety net: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Nach einer Firewall-Änderung: kurz warten, dann bis zu dreimal versuchen, den Server zu erreichen.
    /// </summary>
    public static async Task<bool> VerifyAsync(Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 10), ct);
            if (await serverReachable(ct))
            {
                log("[agent] Server still reachable after the change.");
                return true;
            }

            log($"[agent] Server not reachable (attempt {attempt}/3).");
        }

        return false;
    }
}

/// <summary>Verzeichnisse für Sicherungen im Agent-Verzeichnis.</summary>
public sealed class ComplianceStore(string stateDirectory)
{
    public string Root => Path.Combine(stateDirectory, "compliance");
    public string FirewallDir => Path.Combine(Root, "firewall");
    public string FirewallOriginal => Path.Combine(FirewallDir, "original");
    public string FirewallPending => Path.Combine(FirewallDir, "pending");
    public string FirewallAppliedHash => Path.Combine(FirewallDir, "applied-hash");
    public string RemoteAccessOriginal => Path.Combine(Root, "remote-access-original.json");

    public string NewFirewallBackupDir()
    {
        var dir = Path.Combine(FirewallDir, $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        PruneBackups();
        return dir;
    }

    public string? AppliedHash => File.Exists(FirewallAppliedHash) ? File.ReadAllText(FirewallAppliedHash).Trim() : null;

    public bool HasOriginal => Directory.Exists(FirewallOriginal);

    /// <summary>Nur die letzten 10 Sicherungen behalten (das Original bleibt immer).</summary>
    private void PruneBackups()
    {
        foreach (var old in Directory.GetDirectories(FirewallDir, "backup-*").OrderDescending().Skip(10))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
