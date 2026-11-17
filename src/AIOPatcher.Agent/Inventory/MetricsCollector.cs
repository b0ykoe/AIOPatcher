using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Inventory;

/// <summary>
/// Auslastung für metrics.sample: CPU (Mittel seit der letzten Messung), RAM, Datenträger, Docker-Container.
/// Nur lesen, keine zusätzlichen Pakete nötig (Linux: /proc, Windows: kernel32).
/// </summary>
public static partial class MetricsCollector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Lock Gate = new();
    private static (ulong Busy, ulong Total, DateTime At)? _lastCpu;

    /// <summary>Dateisysteme, die echte Daten tragen (proc, tmpfs, overlay, squashfs usw. fallen weg).</summary>
    private static readonly HashSet<string> DiskFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "vfat", "exfat", "ntfs", "ntfs3", "f2fs", "jfs", "reiserfs", "fuseblk", "nfs", "nfs4", "cifs",
    };

    public static async Task<HostMetrics> CollectAsync(CancellationToken ct)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var cpu = await CpuPercentAsync(ct);
        var (memTotal, memUsed, swapTotal, swapUsed) = Memory();
        var load = OperatingSystem.IsLinux() ? ReadLoad() : null;
        var disks = OperatingSystem.IsWindows() ? WindowsDisks() : LinuxDisks(File.Exists("/proc/mounts") ? File.ReadAllText("/proc/mounts") : "");
        var containers = await ContainersAsync(ct);
        return new HostMetrics(uptime, Math.Round(cpu, 1), Environment.ProcessorCount, memTotal, memUsed, swapTotal, swapUsed, load, disks, containers);
    }

    // --- CPU -----------------------------------------------------------------------------------------

    private static async Task<double> CpuPercentAsync(CancellationToken ct)
    {
        var now = ReadCpu();
        if (now is null)
        {
            return 0;
        }

        (ulong Busy, ulong Total, DateTime At)? last;
        lock (Gate)
        {
            last = _lastCpu;
            _lastCpu = (now.Value.Busy, now.Value.Total, DateTime.UtcNow);
        }

        // Erste Messung (oder lange her): kurz selbst messen
        if (last is null || DateTime.UtcNow - last.Value.At > TimeSpan.FromMinutes(30))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var after = ReadCpu();
            lock (Gate)
            {
                _lastCpu = after is { } a ? (a.Busy, a.Total, DateTime.UtcNow) : _lastCpu;
            }

            return after is { } next ? CpuPercent(now.Value, next) : 0;
        }

        return CpuPercent((last.Value.Busy, last.Value.Total), now.Value);
    }

    public static double CpuPercent((ulong Busy, ulong Total) before, (ulong Busy, ulong Total) after)
    {
        var total = after.Total - before.Total;
        return after.Total <= before.Total || after.Busy < before.Busy ? 0 : Math.Clamp(100.0 * (after.Busy - before.Busy) / total, 0, 100);
    }

    private static (ulong Busy, ulong Total)? ReadCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCpu();
        }

        return File.Exists("/proc/stat") ? ParseProcStat(File.ReadAllText("/proc/stat")) : null;
    }

    /// <summary>Erste Zeile "cpu  user nice system idle iowait irq softirq steal …" (Jiffies). Leerlauf = idle + iowait.</summary>
    public static (ulong Busy, ulong Total)? ParseProcStat(string stat)
    {
        var line = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(v => ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        if (values.Length < 4)
        {
            return null;
        }

        var total = values.Aggregate(0UL, (a, b) => a + b);
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total - idle, total);
    }

    [SupportedOSPlatform("windows")]
    private static (ulong Busy, ulong Total)? WindowsCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel-Zeit enthält die Leerlaufzeit
        var total = kernel + user;
        return (total - idle, total);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    // --- RAM -----------------------------------------------------------------------------------------

    private static (long Total, long Used, long? SwapTotal, long? SwapUsed) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? ((long)status.TotalPhys, (long)(status.TotalPhys - status.AvailPhys), null, null)
                : (0, 0, null, null);
        }

        return File.Exists("/proc/meminfo") ? ParseMeminfo(File.ReadAllText("/proc/meminfo")) : (0, 0, null, null);
    }

    /// <summary>Belegt = MemTotal − MemAvailable (Cache zählt als frei, wie bei free/htop).</summary>
    public static (long Total, long Used, long? SwapTotal, long? SwapUsed) ParseMeminfo(string meminfo)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in meminfo.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && long.TryParse(line[(colon + 1)..].Replace("kB", "", StringComparison.Ordinal).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
            {
                values[line[..colon]] = kb * 1024;
            }
        }

        var total = values.GetValueOrDefault("MemTotal");
        var available = values.TryGetValue("MemAvailable", out var a) ? a : values.GetValueOrDefault("MemFree");
        long? swapTotal = values.TryGetValue("SwapTotal", out var st) ? st : null;
        long? swapUsed = swapTotal is { } t && values.TryGetValue("SwapFree", out var sf) ? t - sf : null;
        return (total, Math.Max(0, total - available), swapTotal, swapUsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static List<double>? ReadLoad()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');
            return parts.Take(3).Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
        }
        catch (IOException)
        {
            return null;
        }
    }

    // --- Datenträger ---------------------------------------------------------------------------------

    /// <summary>Echte Dateisysteme aus /proc/mounts, je Gerät einmal (Bind-Mounts), ohne Proxmox-Gast-Volumes und Docker-Interna.</summary>
    public static List<DiskUsage> LinuxDisks(string mounts, Func<string, (long Total, long Available)?>? stat = null)
    {
        stat ??= StatFs;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiskUsage>();
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3 || !DiskFileSystems.Contains(parts[2]))
            {
                continue;
            }

            var device = parts[0];
            var mount = parts[1].Replace("\\040", " ", StringComparison.Ordinal);
            if (mount.Contains("/subvol-", StringComparison.Ordinal) || mount.Contains("/vm-", StringComparison.Ordinal)
                || mount.StartsWith("/var/lib/docker/", StringComparison.Ordinal) || mount.StartsWith("/snap/", StringComparison.Ordinal)
                || !seen.Add(device))
            {
                continue;
            }

            if (stat(mount) is { Total: > 0 } size)
            {
                result.Add(new DiskUsage(mount, parts[2], size.Total, Math.Max(0, size.Total - size.Available)));
            }
        }

        return result.OrderBy(d => d.Mount, StringComparer.Ordinal).Take(16).ToList();
    }

    private static (long Total, long Available)? StatFs(string mount)
    {
        try
        {
            var drive = new DriveInfo(mount);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // z. B. nicht erreichbares Netzlaufwerk
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<DiskUsage> WindowsDisks()
    {
        var result = new List<DiskUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.TotalSize > 0)
                {
                    result.Add(new DiskUsage(drive.Name.TrimEnd('\\'), drive.DriveFormat, drive.TotalSize, drive.TotalSize - drive.AvailableFreeSpace));
                }
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    // --- Docker --------------------------------------------------------------------------------------

    private static async Task<List<ContainerUsage>?> ContainersAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/docker") && !File.Exists("/usr/local/bin/docker"))
        {
            return null;
        }

        try
        {
            var stats = await ProcessRunner.RunAsync("docker", ["stats", "--no-stream", "--format", "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}"], Timeout, ct);
            return stats.Success ? ParseDockerStats(stats.StandardOutput) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Zeilen "web|1.23%|12.5MiB / 1.94GiB".</summary>
    public static List<ContainerUsage> ParseDockerStats(string output)
    {
        var result = new List<ContainerUsage>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3 || parts[0].Length == 0)
            {
                continue;
            }

            var cpu = double.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;
            var memory = parts[2].Split('/', StringSplitOptions.TrimEntries);
            result.Add(new ContainerUsage(parts[0], Math.Round(cpu, 1), ParseSize(memory[0]) ?? 0, memory.Length > 1 ? ParseSize(memory[1]) : null));
        }

        return result.OrderBy(c => c.Name, StringComparer.Ordinal).Take(200).ToList();
    }

    /// <summary>"12.5MiB", "1.94GiB", "512kB", "0B" → Bytes.</summary>
    public static long? ParseSize(string value)
    {
        var i = 0;
        while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.'))
        {
            i++;
        }

        if (i == 0 || !double.TryParse(value[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var factor = value[i..].Trim() switch
        {
            "B" or "" => 1d,
            "kB" or "KB" => 1e3,
            "KiB" => 1024d,
            "MB" => 1e6,
            "MiB" => 1024d * 1024,
            "GB" => 1e9,
            "GiB" => 1024d * 1024 * 1024,
            "TB" => 1e12,
            "TiB" => 1024d * 1024 * 1024 * 1024,
            _ => double.NaN,
        };
        return double.IsNaN(factor) ? null : (long)(number * factor);
    }
}
