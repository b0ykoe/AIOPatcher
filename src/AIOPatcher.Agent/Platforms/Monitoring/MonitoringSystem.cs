using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using Microsoft.Win32;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>Gemeinsame Systemaufrufe für Monitoring-Clients: Dienste, Pakete, Sicherungen.</summary>
public static partial class MonitoringSystem
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public static string? Which(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    public static string? Systemctl => Which("/usr/bin/systemctl", "/bin/systemctl");

    public static async Task<ProcessResult?> TryRunAsync(string? file, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            return await ProcessRunner.RunAsync(file, args, timeout ?? CommandTimeout, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new ProcessResult(-1, "", e.Message);
        }
    }

    // --- Linux-Pakete ---------------------------------------------------------------------------------

    /// <summary>Installierte Version eines Debian-Pakets (dpkg-query), null = nicht installiert.</summary>
    public static async Task<string?> DpkgVersionAsync(string package, CancellationToken ct)
    {
        var result = await TryRunAsync(Which("/usr/bin/dpkg-query", "/bin/dpkg-query"), ["-W", "-f=${Status}|${Version}", package], ct);
        if (result is not { Success: true })
        {
            return null;
        }

        var parts = result.StandardOutput.Trim().Split('|');
        return parts.Length == 2 && parts[0].EndsWith("installed", StringComparison.Ordinal) && !parts[0].Contains("not-installed") ? parts[1].Trim() : null;
    }

    public static async Task<bool> AptHeldAsync(string package, CancellationToken ct) =>
        await TryRunAsync(Which("/usr/bin/apt-mark", "/bin/apt-mark"), ["showhold"], ct) is { Success: true } held
        && held.StandardOutput.Split('\n', StringSplitOptions.TrimEntries).Contains(package);

    // --- Dienste --------------------------------------------------------------------------------------

    /// <summary>systemd-Unit aktiv? null = kein systemd bzw. Unit unbekannt.</summary>
    public static async Task<bool?> UnitActiveAsync(string unit, CancellationToken ct)
    {
        if (await TryRunAsync(Systemctl, ["is-active", unit], ct) is not { } result)
        {
            return null;
        }

        var state = result.StandardOutput.Trim();
        return state switch
        {
            "active" or "reloading" or "activating" => true,
            "inactive" or "failed" or "deactivating" => await UnitKnownAsync(unit, ct) ? false : null,
            _ => null,
        };
    }

    public static async Task<bool> UnitKnownAsync(string unit, CancellationToken ct) =>
        await TryRunAsync(Systemctl, ["list-unit-files", "--no-legend", "--no-pager", unit.Contains('.') ? unit : unit + ".service"], ct) is { Success: true } r
        && r.StandardOutput.Trim().Length > 0;

    [SupportedOSPlatform("windows")]
    public static async Task<bool?> WindowsServiceRunningAsync(string name, CancellationToken ct)
    {
        var result = await TryRunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["query", name], ct);
        if (result is null || !result.Success)
        {
            return null; // Dienst gibt es nicht
        }

        return result.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ersten vorhandenen Windows-Dienst aus der Liste finden (Registry).</summary>
    [SupportedOSPlatform("windows")]
    public static string? WindowsService(params string[] names)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        return names.FirstOrDefault(n => root?.OpenSubKey(n) is { } key && Dispose(key));
    }

    /// <summary>Windows-Dienst, dessen Programm unter dem Ordner liegt (Name unterscheidet sich je nach Version).</summary>
    [SupportedOSPlatform("windows")]
    public static string? WindowsServiceForImage(string folder, string fileName)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var key = root!.OpenSubKey(name);
            if (key?.GetValue("ImagePath") is string image
                && image.Contains(fileName, StringComparison.OrdinalIgnoreCase)
                && image.Replace("\"", "").Contains(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    public static async Task<bool> RestartWindowsServiceAsync(string name, Action<string> log, CancellationToken ct)
    {
        if (!ServiceNamePattern().IsMatch(name))
        {
            return false;
        }

        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var result = await TryRunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", $"Restart-Service -Name '{name}' -Force -ErrorAction Stop"], ct);
        log(result is { Success: true } ? $"[agent] Service {name} restarted." : $"[agent] Restarting service {name} failed: {(result?.StandardError ?? "").Trim()}");
        return result is { Success: true };
    }

    /// <summary>Uninstall-Eintrag (64- und 32-Bit-Ansicht) mit passendem Anzeigenamen: Version und ProductCode.</summary>
    [SupportedOSPlatform("windows")]
    public static (string? Version, string? ProductCode)? WindowsUninstallEntry(Func<string, bool> displayName)
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is string display && displayName(display))
                {
                    var code = ProductCodePattern().IsMatch(name) ? name : null;
                    return (key.GetValue("DisplayVersion") as string, code);
                }
            }
        }

        return null;
    }

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }

    // --- Dateien --------------------------------------------------------------------------------------

    /// <summary>Text lesen und die Kodierung merken (mit BOM bleibt mit BOM).</summary>
    public static (string Text, bool Bom) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Encoding.UTF8.GetString(bom ? bytes[3..] : bytes), bom);
    }

    /// <summary>In die bestehende Datei schreiben (Besitzer und Rechte bleiben erhalten).</summary>
    public static void WriteText(string path, string text, bool bom)
    {
        using var stream = new FileStream(path, File.Exists(path) ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write);
        var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        stream.Write(bytes);
    }

    /// <summary>Dateien in einen neuen Sicherungsordner kopieren; Rückgabe: Ordner und welche Dateien es vorher gab.</summary>
    public static Backup BackUp(string root, string product, IEnumerable<string> files)
    {
        var folder = Path.Combine(root, "monitoring-backup", product, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var entries = new List<(string Original, string? Copy)>();
        var index = 0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(file))
            {
                var copy = Path.Combine(folder, $"{index++:00}-{Path.GetFileName(file)}");
                File.Copy(file, copy);
                entries.Add((file, copy));
            }
            else
            {
                entries.Add((file, null));
            }
        }

        File.WriteAllLines(Path.Combine(folder, "files.txt"), entries.Select(e => $"{e.Copy ?? "-"}\t{e.Original}"));
        return new Backup(folder, entries);
    }

    public sealed record Backup(string Folder, IReadOnlyList<(string Original, string? Copy)> Files)
    {
        /// <summary>Alten Stand herstellen: gesicherte Dateien zurück, vorher nicht vorhandene löschen.</summary>
        public void Restore(Action<string> log)
        {
            foreach (var (original, copy) in Files)
            {
                try
                {
                    if (copy is null)
                    {
                        if (File.Exists(original))
                        {
                            File.Delete(original);
                        }
                    }
                    else
                    {
                        File.Copy(copy, original, overwrite: true);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log($"[agent] Could not restore {original}: {e.Message}");
                }
            }

            log($"[agent] Previous configuration restored (backup: {Folder}).");
        }
    }

    /// <summary>Linux: Rechte setzen (z. B. 640) und Gruppe des Clients, falls es sie gibt.</summary>
    public static async Task RestrictAsync(string path, UnixFileMode mode, string[] groups, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
        foreach (var group in groups)
        {
            if (await TryRunAsync(Which("/usr/bin/getent", "/bin/getent"), ["group", group], ct) is { Success: true })
            {
                await TryRunAsync(Which("/bin/chown", "/usr/bin/chown"), [$"root:{group}", path], ct);
                return;
            }
        }
    }

    // --- Prüfungen ------------------------------------------------------------------------------------

    /// <summary>Hostname oder IP (v4/v6), keine Leerzeichen oder Steuerzeichen.</summary>
    public static bool ValidHost(string? value) => value is { Length: > 0 and <= 253 } && HostPattern().IsMatch(value);

    /// <summary>Freier Bezeichner (Gruppen, Site, Benutzer, Hostname in Checkmk).</summary>
    public static bool ValidName(string? value) => value is { Length: > 0 and <= 200 } && NamePattern().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9._\-:\[\]]+$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-,@]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex(@"^\{[0-9A-Fa-f\-]{36}\}$")]
    private static partial Regex ProductCodePattern();
}
