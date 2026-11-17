using System.Runtime.Versioning;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Agent.Platforms.Windows;
using AIOPatcher.Contracts;
using Microsoft.Win32;

namespace AIOPatcher.Agent.Platforms;

/// <summary>
/// Die eigene Update-Automatik des Systems an- oder abschalten, damit nur AIOPatcher patcht (oder wieder zurück).
/// Linux: eigene apt-Konfigurationsdatei, die unattended-upgrades übersteuert (löschen = Einstellung der Distribution).
/// Windows: Richtlinie "Herunterladen, Installation manuell" (AUOptions 3); einschalten entfernt die Werte wieder.
/// </summary>
public static class NativeAutoUpdates
{
    public const string AptOverrideFile = "/etc/apt/apt.conf.d/99aiopatcher-auto-upgrades";
    private const string WindowsPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    public static bool IsChange(SystemChange? change) => change?.Kind is SystemChangeKind.AutoUpdatesOff or SystemChangeKind.AutoUpdatesOn;

    public static async Task<JobExecutionResult> SetAsync(bool enabled, Action<string> log, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return SetWindows(enabled, log);
        }

        if (!OperatingSystem.IsLinux())
        {
            return new JobExecutionResult(JobOutcome.Rejected, "Not supported on this platform.");
        }

        if (!File.Exists("/usr/bin/unattended-upgrade"))
        {
            return enabled
                ? new JobExecutionResult(JobOutcome.Rejected, "unattended-upgrades is not installed (apt install unattended-upgrades).")
                : new JobExecutionResult(JobOutcome.Succeeded, "unattended-upgrades is not installed: nothing to turn off.");
        }

        var content = AptOverride(enabled);
        var temp = AptOverrideFile + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temp, AptOverrideFile, overwrite: true);
        log($"[agent] Wrote {AptOverrideFile}: Unattended-Upgrade \"{(enabled ? 1 : 0)}\"");

        var now = await LinuxInventory.IsUnattendedUpgradesEnabledAsync(ct);
        return now == enabled
            ? new JobExecutionResult(JobOutcome.Succeeded, enabled ? "unattended-upgrades turned on." : "unattended-upgrades turned off: only AIOPatcher installs updates now.")
            : new JobExecutionResult(JobOutcome.Failed, $"Written, but apt-config still reports Unattended-Upgrade {(now ? "on" : "off")}: another file in /etc/apt/apt.conf.d overrides it.");
    }

    /// <summary>Inhalt der Override-Datei (lädt nach 20auto-upgrades und 50unattended-upgrades, gewinnt also).</summary>
    public static string AptOverride(bool enabled) =>
        "// Set by AIOPatcher (dashboard: host -> Auto-Patch -> system updates). Delete this file to use the distribution setting.\n"
        + (enabled ? "APT::Periodic::Update-Package-Lists \"1\";\n" : "")
        + $"APT::Periodic::Unattended-Upgrade \"{(enabled ? 1 : 0)}\";\n";

    [SupportedOSPlatform("windows")]
    private static JobExecutionResult SetWindows(bool enabled, Action<string> log)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(WindowsPolicyKey))
        {
            if (enabled)
            {
                key.DeleteValue("AUOptions", throwOnMissingValue: false);
                key.DeleteValue("NoAutoUpdate", throwOnMissingValue: false);
                log($"[agent] Removed AUOptions/NoAutoUpdate from HKLM\\{WindowsPolicyKey} (Windows default)");
            }
            else
            {
                key.SetValue("NoAutoUpdate", 0, RegistryValueKind.DWord);
                key.SetValue("AUOptions", 3, RegistryValueKind.DWord); // herunterladen, Installation manuell
                log($"[agent] Set HKLM\\{WindowsPolicyKey}: AUOptions=3 (download, install manually), NoAutoUpdate=0");
            }
        }

        log("[agent] Note: a domain group policy overrides this at the next policy refresh.");
        var now = WindowsInventory.AreNativeAutoUpdatesEnabled();
        return now == enabled
            ? new JobExecutionResult(JobOutcome.Succeeded, enabled ? "Windows automatic updates turned on (Windows default)." : "Windows only downloads updates now; AIOPatcher installs them.")
            : new JobExecutionResult(JobOutcome.Failed, "Policy written, but Windows still reports a different setting.");
    }
}
