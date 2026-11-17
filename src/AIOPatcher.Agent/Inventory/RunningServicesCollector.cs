using System.Runtime.Versioning;
using AIOPatcher.Contracts;
using Microsoft.Win32;

namespace AIOPatcher.Agent.Inventory;

/// <summary>
/// Laufende Dienste und Container für den Vergleich vor/nach einem Neustart. Nur Dienste, die beim Systemstart von
/// selbst starten: Was nur bei Bedarf läuft (D-Bus-/Trigger-Start, verzögerter Start, Benutzerdienste), wäre nach
/// einem Neustart oft zu Recht noch aus und würde nur Fehlalarme erzeugen.
/// </summary>
public static class RunningServicesCollector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static bool Available =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && (File.Exists("/usr/bin/systemctl") || File.Exists("/bin/systemctl"));

    public static async Task<RunningServices> CollectAsync(CancellationToken ct)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var (services, stopped) = OperatingSystem.IsWindows() ? await WindowsAsync(ct) : await SystemdAsync(ct);
        var (containers, withoutRestart) = await ContainersAsync(ct);
        return new RunningServices(uptime, services, containers, stopped, withoutRestart);
    }

    // --- Linux (systemd) -----------------------------------------------------------------------------

    private static async Task<(List<string> Running, List<string> Failed)> SystemdAsync(CancellationToken ct)
    {
        string[] common = ["--type=service", "--no-legend", "--no-pager", "--plain"];
        var running = await ProcessRunner.RunAsync("systemctl", ["list-units", "--state=running", .. common], Timeout, ct);
        var enabled = await ProcessRunner.RunAsync("systemctl", ["list-unit-files", "--state=enabled,enabled-runtime", "--type=service", "--no-legend", "--no-pager"], Timeout, ct);
        var failed = await ProcessRunner.RunAsync("systemctl", ["list-units", "--state=failed", .. common], Timeout, ct);
        if (!running.Success || !enabled.Success)
        {
            throw new InvalidOperationException("systemctl failed: " + (running.StandardError + enabled.StandardError).Trim());
        }

        var enabledUnits = FirstColumn(enabled.StandardOutput).ToHashSet(StringComparer.Ordinal);
        var autoStart = FirstColumn(running.StandardOutput).Where(u => IsEnabled(u, enabledUnits)).Order(StringComparer.Ordinal).ToList();
        var failedUnits = failed.Success ? FirstColumn(failed.StandardOutput).Where(u => IsEnabled(u, enabledUnits)).Order(StringComparer.Ordinal).ToList() : [];
        return (autoStart, failedUnits);
    }

    /// <summary>Erste Spalte je Zeile ("ssh.service loaded active running …" → "ssh.service"); Markierungen wie "●" fallen weg.</summary>
    public static IEnumerable<string> FirstColumn(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Length > 1 && p[0] is "●" or "*" ? p[1] : p.FirstOrDefault())
            .OfType<string>()
            .Where(u => u.EndsWith(".service", StringComparison.Ordinal));

    /// <summary>Instanzen von Vorlagen zählen mit, wenn die Vorlage aktiviert ist ("getty@tty1.service" über "getty@.service").</summary>
    public static bool IsEnabled(string unit, IReadOnlySet<string> enabled)
    {
        if (enabled.Contains(unit))
        {
            return true;
        }

        var at = unit.IndexOf('@');
        return at > 0 && enabled.Contains(unit[..(at + 1)] + ".service");
    }

    // --- Windows -------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static async Task<(List<string> Running, List<string> Stopped)> WindowsAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var result = await ProcessRunner.RunAsync(sc, ["queryex", "type=", "service", "bufsize=", "262144"], Timeout, ct);
        if (!result.Success)
        {
            throw new InvalidOperationException("sc queryex failed: " + result.StandardOutput.Trim());
        }

        // ohne state= liefert sc nur laufende Dienste ("state= active" lehnen manche Versionen ab)
        var active = ParseScNames(result.StandardOutput).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var autoStart = AutoStartServices();
        return (autoStart.Where(active.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            autoStart.Where(s => !active.Contains(s)).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>"SERVICE_NAME: Dnscache" (sc.exe gibt die Feldnamen immer englisch aus).</summary>
    public static IEnumerable<string> ParseScNames(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["SERVICE_NAME:".Length..].Trim())
            .Where(n => n.Length > 0);

    /// <summary>Start = Automatisch (2), ohne verzögerten Start, ohne Trigger, keine Benutzerdienste (Typ 0x40, Name je Anmeldung anders).</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> AutoStartServices()
    {
        var result = new List<string>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var key = root!.OpenSubKey(name);
                if (key is null || key.GetValue("Start") is not int start || key.GetValue("Type") is not int type)
                {
                    continue;
                }

                var delayed = key.GetValue("DelayedAutostart") is int d && d != 0;
                using var triggers = key.OpenSubKey("TriggerInfo");
                if (IsWatchedWindowsService(start, type, delayed, triggers is not null))
                {
                    result.Add(name);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // einzelne geschützte Schlüssel überspringen
            }
        }

        return result;
    }

    /// <summary>Nur Win32-Dienste (Typ 0x10/0x20), Start automatisch, nicht verzögert, ohne Trigger, keine Benutzerdienste.</summary>
    public static bool IsWatchedWindowsService(int start, int type, bool delayed, bool hasTriggers) =>
        start == 2 && !delayed && !hasTriggers && (type & 0x30) != 0 && (type & 0x40) == 0;

    // --- Docker --------------------------------------------------------------------------------------

    private static async Task<(List<string>? Running, List<string>? WithoutRestart)> ContainersAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/docker") && !File.Exists("/usr/local/bin/docker"))
        {
            return (null, null);
        }

        try
        {
            var ps = await ProcessRunner.RunAsync("docker", ["ps", "-q", "--no-trunc"], Timeout, ct);
            if (!ps.Success)
            {
                return (null, null); // Daemon läuft nicht: nicht als "alle Container fehlen" werten
            }

            var ids = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length == 0)
            {
                return ([], []);
            }

            var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", "{{.Name}}|{{.HostConfig.RestartPolicy.Name}}", .. ids], Timeout, ct);
            return ParseContainers(inspect.StandardOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>Zeilen "/name|unless-stopped"; leere Policy zählt wie "no".</summary>
    public static (List<string> Running, List<string> WithoutRestart) ParseContainers(string output)
    {
        var running = new List<string>();
        var withoutRestart = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var name = parts[0].TrimStart('/');
            if (name.Length == 0)
            {
                continue;
            }

            running.Add(name);
            if (parts.Length < 2 || parts[1] is "" or "no")
            {
                withoutRestart.Add(name);
            }
        }

        running.Sort(StringComparer.Ordinal);
        withoutRestart.Sort(StringComparer.Ordinal);
        return (running, withoutRestart);
    }
}
