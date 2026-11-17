using System.Runtime.Versioning;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>Debian, Ubuntu, Proxmox VE und Proxmox Backup Server (alles apt-basiert).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class LinuxInventory(ILogger<LinuxInventory> logger) : IPlatformInventory
{
    public IReadOnlyList<BrokenAptSource>? BrokenSources { get; private set; }

    private static readonly TimeSpan AptUpdateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct)
    {
        var osRelease = ReadOsRelease();
        var distro = osRelease.GetValueOrDefault("PRETTY_NAME") ?? osRelease.GetValueOrDefault("NAME") ?? "Linux";
        var kernel = ReadFirstLine("/proc/sys/kernel/osrelease");

        string osName = distro, osVersion = osRelease.GetValueOrDefault("VERSION_ID") ?? "";
        if (await GetPackageVersionAsync("pve-manager", ct) is { } pve)
        {
            (osName, osVersion) = ("Proxmox VE", $"{pve} ({distro})");
        }
        else if (await GetPackageVersionAsync("proxmox-backup-server", ct) is { } pbs)
        {
            (osName, osVersion) = ("Proxmox Backup Server", $"{pbs} ({distro})");
        }

        return new SystemInfo(osName, osVersion, kernel, RebootDetector.IsRebootRequired(kernel), await IsUnattendedUpgradesEnabledAsync(ct));
    }

    public async Task<IReadOnlyList<AvailableUpdate>?> ScanUpdatesAsync(CancellationToken ct)
    {
        // Paketlisten aktualisieren braucht Root. Schlägt das fehl, scannen wir mit den vorhandenen Listen.
        var update = await RunPrivilegedAsync("apt-get", ["update"], AptUpdateTimeout, ct);
        if (!update.Success)
        {
            logger.LogWarning("apt-get update failed (exit {Code}): {Error}", update.ExitCode, Trim(update.StandardError));
        }

        // Vorabprüfung: kaputte Paketquellen melden, bevor ein Patch-Job daran hängen bleibt
        var lines = (update.StandardOutput + "\n" + update.StandardError).Split('\n');
        BrokenSources = AptSources.Classify(AptUpdateCheck.Analyze(update.ExitCode, lines).Broken);

        // Simulation braucht keine Rechte. Proxmox verlangt dist-upgrade, daher immer dist-upgrade simulieren.
        var simulation = await ProcessRunner.RunAsync("apt-get", ["-s", "-o", "Debug::NoLocking=1", "dist-upgrade"], AptUpdateTimeout, ct);
        if (!simulation.Success)
        {
            logger.LogError("apt-get -s dist-upgrade failed (exit {Code}): {Error}", simulation.ExitCode, Trim(simulation.StandardError));
            return null;
        }

        var updates = AptOutputParser.Parse(simulation.StandardOutput);
        return await AddSourcePackagesAsync(updates, ct);
    }

    /// <summary>Alle installierten Pakete (dpkg) und Snaps. Grundlage für das Programm-Inventar und CVE-Abgleich.</summary>
    public async Task<IReadOnlyList<InstalledSoftware>?> ScanSoftwareAsync(CancellationToken ct)
    {
        var dpkg = await ProcessRunner.RunAsync("dpkg-query", ["-W", "-f=${db:Status-Abbrev}\t${Package}\t${Version}\t${source:Package}\t${Maintainer}\t${source:Version}\n"], ShortTimeout, ct);
        var software = AptOutputParser.ParseInstalled(dpkg.StandardOutput);

        try
        {
            var snap = await ProcessRunner.RunAsync("snap", ["list"], ShortTimeout, ct);
            if (snap.Success)
            {
                software.AddRange(AptOutputParser.ParseSnapList(snap.StandardOutput));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // kein snapd: egal
        }

        return software;
    }

    /// <summary>Quellpaket je Update (für die CVE-Zuordnung auf dem Server). Fehler sind egal: dann ohne.</summary>
    private static async Task<IReadOnlyList<AvailableUpdate>> AddSourcePackagesAsync(IReadOnlyList<AvailableUpdate> updates, CancellationToken ct)
    {
        var installed = updates.Where(u => u.CurrentVersion is not null).Select(u => u.Name).Distinct().ToArray();
        if (installed.Length == 0)
        {
            return updates;
        }

        try
        {
            // dpkg-query liefert Exit 1, wenn ein Paket fehlt, gibt die übrigen aber trotzdem aus.
            var result = await ProcessRunner.RunAsync("dpkg-query", ["-W", "-f=${Package}\t${source:Package}\n", .. installed], ShortTimeout, ct);
            var map = AptOutputParser.ParseSourceMap(result.StandardOutput);
            return updates.Select(u => map.TryGetValue(u.Name, out var source) ? u with { SourcePackage = source } : u).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return updates;
        }
    }

    private static async Task<ProcessResult> RunPrivilegedAsync(string command, string[] args, TimeSpan timeout, CancellationToken ct) =>
        Environment.IsPrivilegedProcess
            ? await ProcessRunner.RunAsync(command, args, timeout, ct)
            : await ProcessRunner.RunAsync("sudo", ["-n", command, .. args], timeout, ct);

    private static async Task<string?> GetPackageVersionAsync(string package, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("dpkg-query", ["-W", "-f=${Version}", package], ShortTimeout, ct);
            var version = result.StandardOutput.Trim();
            return result.Success && version.Length > 0 ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // kein dpkg
        }
    }

    public static async Task<bool> IsUnattendedUpgradesEnabledAsync(CancellationToken ct)
    {
        if (!File.Exists("/usr/bin/unattended-upgrade"))
        {
            return false;
        }

        var dump = await ProcessRunner.RunAsync("apt-config", ["dump"], ShortTimeout, ct);
        return dump.StandardOutput.Split('\n')
            .Any(l => l.StartsWith("APT::Periodic::Unattended-Upgrade ", StringComparison.Ordinal) && !l.Contains("\"0\"", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ReadOsRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release";
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                result[line[..idx]] = line[(idx + 1)..].Trim().Trim('"');
            }
        }

        return result;
    }

    private static string? ReadFirstLine(string path) =>
        File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s.Trim();
}

/// <summary>Reine Dateilogik, daher plattformneutral (und unter Windows testbar).</summary>
public static class RebootDetector
{
    /// <summary>
    /// Ubuntu legt /var/run/reboot-required an. Debian/Proxmox nicht immer: dort zusätzlich prüfen,
    /// ob in /boot ein neuerer Kernel liegt als der laufende. In LXC-Containern gibt es /boot meist nicht.
    /// </summary>
    public static bool IsRebootRequired(string? runningKernel, string bootDirectory = "/boot", string flagFile = "/var/run/reboot-required")
    {
        if (File.Exists(flagFile))
        {
            return true;
        }

        if (string.IsNullOrEmpty(runningKernel) || !Directory.Exists(bootDirectory))
        {
            return false;
        }

        var installed = Directory.GetFiles(bootDirectory, "vmlinuz-*")
            .Select(f => Path.GetFileName(f)["vmlinuz-".Length..])
            .ToList();
        if (installed.Count == 0)
        {
            return false;
        }

        var newest = installed.OrderBy(v => v, KernelVersionComparer.Instance).Last();
        return KernelVersionComparer.Instance.Compare(newest, runningKernel) > 0;
    }
}

/// <summary>Vergleicht Kernel-Versionen numerisch ("6.8.12-10-pve" &gt; "6.8.12-9-pve").</summary>
public sealed class KernelVersionComparer : IComparer<string>
{
    public static readonly KernelVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var pa = i < a.Length ? a[i] : -1;
            var pb = i < b.Length ? b[i] : -1;
            if (pa != pb)
            {
                return pa.CompareTo(pb);
            }
        }

        return 0;
    }

    private static long[] Parts(string? version) =>
        (version ?? "").Split(['.', '-', '+', '~', '_'], StringSplitOptions.RemoveEmptyEntries)
        .Select(p => long.TryParse(p, out var n) ? n : -1)
        .Where(n => n >= 0)
        .ToArray();
}
