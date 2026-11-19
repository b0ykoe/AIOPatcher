using System.Text.RegularExpressions;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using static AIOPatcher.Agent.Platforms.Monitoring.MonitoringSystem;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>Installation des Checkmk-Agents auf diesem Host.</summary>
/// <param name="Agent">check_mk_agent (Skript bzw. .exe).</param>
/// <param name="Controller">cmk-agent-ctl, null = ohne Controller (vor 2.1 bzw. Legacy-Installation).</param>
/// <param name="Xinetd">Linux-Legacy: xinetd-Datei mit only_from.</param>
/// <param name="UserYaml">Windows 2.x: check_mk.user.yml (only_from unter global:).</param>
/// <param name="Ini">Windows 1.x: check_mk.ini (nur anzeigen).</param>
public sealed record CheckmkPaths(string Agent, string? Controller, string? Xinetd, string? UserYaml, string? Ini)
{
    public static CheckmkPaths? Find()
    {
        if (OperatingSystem.IsLinux())
        {
            var agent = Which("/usr/bin/check_mk_agent", "/usr/local/bin/check_mk_agent");
            return agent is null
                ? null
                : new CheckmkPaths(agent, Which("/usr/bin/cmk-agent-ctl", "/usr/local/bin/cmk-agent-ctl"),
                    Which("/etc/xinetd.d/check-mk-agent", "/etc/xinetd.d/check_mk"), null, null);
        }

        if (OperatingSystem.IsWindows())
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var service = Path.Combine(programs, "checkmk", "service");
            if (File.Exists(Path.Combine(service, "check_mk_agent.exe")))
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "checkmk", "agent");
                return new CheckmkPaths(Path.Combine(service, "check_mk_agent.exe"), Which(Path.Combine(service, "cmk-agent-ctl.exe")), null,
                    Path.Combine(data, "check_mk.user.yml"), null);
            }

            var legacy = Path.Combine(programs, "check_mk");
            if (File.Exists(Path.Combine(legacy, "check_mk_agent.exe")))
            {
                return new CheckmkPaths(Path.Combine(legacy, "check_mk_agent.exe"), null, null, null, Path.Combine(legacy, "check_mk.ini"));
            }
        }

        return null;
    }

    public string Variant => Controller is not null ? MonitoringVariants.CheckmkController
        : Ini is not null ? MonitoringVariants.CheckmkIni
        : MonitoringVariants.CheckmkLegacy;
}

/// <summary>Checkmk-Agent erkennen und auf eine andere Site umstellen (Registrierung bzw. only_from).</summary>
public static partial class CheckmkClient
{
    public const string Package = "check-mk-agent";

    /// <summary>Anzeigename im Uninstall-Eintrag: je nach Version "Check MK Agent", "Check_MK Agent", "Checkmk Agent …".</summary>
    public static bool IsCheckmkDisplayName(string name) =>
        name.Contains("Checkmk", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Check", StringComparison.OrdinalIgnoreCase) && name.Contains("MK", StringComparison.Ordinal) && name.Contains("Agent", StringComparison.OrdinalIgnoreCase);

    public static async Task<MonitoringClient?> DetectAsync(CancellationToken ct)
    {
        if (CheckmkPaths.Find() is not { } paths)
        {
            return null;
        }

        var version = MonitoringVersion.Display(await VersionAsync(paths, ct));
        var running = await RunningAsync(paths, ct) ?? false;
        var held = OperatingSystem.IsLinux() && await AptHeldAsync(Package, ct);

        if (paths.Controller is not null)
        {
            var (connections, error) = await ConnectionsAsync(paths, ct);
            var detail = error ?? (connections.Count == 0 ? "not registered (legacy pull mode)" : $"registered with {string.Join(", ", connections.Select(c => c.SiteId))}");
            return new MonitoringClient(MonitoringProducts.Checkmk, paths.Variant, version, running, connections.Select(c => c.SiteId).ToList(),
                error is null ? connections.Count > 0 : null, detail, true, held);
        }

        if (paths.Ini is not null)
        {
            return new MonitoringClient(MonitoringProducts.Checkmk, paths.Variant, version, running, [], null,
                "Checkmk 1.x agent (check_mk.ini): AIOPatcher does not change its configuration. Update the client.", false);
        }

        if (paths.Xinetd is { } xinetd)
        {
            return new MonitoringClient(MonitoringProducts.Checkmk, paths.Variant, version, running, CheckmkConfig.ParseXinetdOnlyFrom(File.ReadAllText(xinetd)),
                null, "legacy pull mode (xinetd)", true, held);
        }

        if (paths.UserYaml is { } yaml)
        {
            var onlyFrom = File.Exists(yaml) ? CheckmkConfig.ParseYamlOnlyFrom(ReadText(yaml).Text) : [];
            return new MonitoringClient(MonitoringProducts.Checkmk, paths.Variant, version, running, onlyFrom, null, "legacy pull mode", true);
        }

        return new MonitoringClient(MonitoringProducts.Checkmk, paths.Variant, version, running, [], null,
            "Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client.", false, held);
    }

    public static async Task<string?> VersionAsync(CheckmkPaths paths, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            if (await DpkgVersionAsync(Package, ct) is { } dpkg)
            {
                return dpkg;
            }

            try
            {
                return ScriptVersionPattern().Match(await File.ReadAllTextAsync(paths.Agent, ct)) is { Success: true } m ? m.Groups[1].Value : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return OperatingSystem.IsWindows()
            ? WindowsUninstallEntry(CheckmkClient.IsCheckmkDisplayName)?.Version
            : null;
    }

    public static async Task<bool?> RunningAsync(CheckmkPaths paths, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsServiceForImage(Path.GetDirectoryName(paths.Agent)!, "check_mk_agent.exe") is { } service
                ? await WindowsServiceRunningAsync(service, ct)
                : null;
        }

        // Je nach Variante: Controller-Daemon, xinetd oder systemd-Socket
        string[] units = paths.Controller is not null ? ["cmk-agent-ctl-daemon"] : paths.Xinetd is not null ? ["xinetd"] : ["check-mk-agent.socket", "check_mk.socket"];
        foreach (var unit in units)
        {
            if (await UnitActiveAsync(unit, ct) is { } active)
            {
                return active;
            }
        }

        return null;
    }

    /// <summary>Registrierte Verbindungen laut "cmk-agent-ctl status --json"; Fehlertext, wenn das nicht ging.</summary>
    public static async Task<(IReadOnlyList<CheckmkConnection> Connections, string? Error)> ConnectionsAsync(CheckmkPaths paths, CancellationToken ct)
    {
        var result = await TryRunAsync(paths.Controller, ["status", "--json"], ct);
        if (result is not { Success: true })
        {
            return ([], $"cmk-agent-ctl status failed: {(result?.StandardError ?? "").Trim()}");
        }

        return (CheckmkConfig.ParseConnections(result.StandardOutput), null);
    }

    public static string? Validate(CheckmkPaths paths, MonitoringSpec spec, string? secret)
    {
        if (paths.Ini is not null)
        {
            return "Checkmk 1.x agent (check_mk.ini): AIOPatcher does not change its configuration. Update the client first.";
        }

        if (paths.Controller is not null)
        {
            if (!ValidHost(spec.Server) || !ValidName(spec.Site) || !ValidName(spec.User) || !ValidName(spec.Hostname))
            {
                return "Server, site, registration user and host name are required for the agent controller.";
            }

            return string.IsNullOrEmpty(secret) || secret.IndexOfAny(['\r', '\n']) >= 0 ? "The registration password is missing." : null;
        }

        if (spec.OnlyFrom is not { Count: > 0 and <= 50 } onlyFrom || !onlyFrom.All(CheckmkConfig.ValidSource))
        {
            return "Legacy agents need the IP addresses of the new site (only_from).";
        }

        return paths.Xinetd is null && paths.UserYaml is null
            ? "Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client."
            : null;
    }

    public static async Task<JobExecutionResult> ConfigureAsync(MonitoringSpec spec, string? secret, string backupRoot, Action<string> log, CancellationToken ct)
    {
        if (CheckmkPaths.Find() is not { } paths)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "No Checkmk agent installed on this host.");
        }

        if (Validate(paths, spec, secret) is { } invalid)
        {
            return new JobExecutionResult(JobOutcome.Rejected, invalid);
        }

        var version = await VersionAsync(paths, ct);
        if (spec.SiteVersion is { } site && MonitoringVersion.CompareMinor(version, site) > 0)
        {
            log($"[agent] Warning: the agent ({MonitoringVersion.Display(version)}) is newer than the site ({site}); some services may show UNKNOWN.");
        }

        return paths.Controller is not null
            ? await RegisterAsync(paths, spec, secret!, log, ct)
            : await SetOnlyFromAsync(paths, spec.OnlyFrom!, backupRoot, log, ct);
    }

    /// <summary>
    /// Bei der neuen Site registrieren, danach die übrigen Verbindungen entfernen. Schlägt die Registrierung fehl, bleibt
    /// alles wie es war (es wurde noch nichts entfernt).
    /// </summary>
    private static async Task<JobExecutionResult> RegisterAsync(CheckmkPaths paths, MonitoringSpec spec, string secret, Action<string> log, CancellationToken ct)
    {
        var (before, _) = await ConnectionsAsync(paths, ct);
        log($"[agent] Registering with {spec.Server}/{spec.Site} as {spec.Hostname} (user {spec.User}).");
        // Passwort steht kurz in der Prozessliste: cmk-agent-ctl liest es sonst nur interaktiv vom Terminal.
        var register = await TryRunAsync(paths.Controller,
            ["register", "--hostname", spec.Hostname!, "--server", spec.Server!, "--site", spec.Site!, "--user", spec.User!, "--password", secret, "--trust-cert"],
            ct, TimeSpan.FromMinutes(3));
        if (register is not { Success: true })
        {
            var message = ((register?.StandardError ?? "") + " " + (register?.StandardOutput ?? "")).Replace(secret, "***").Trim();
            return new JobExecutionResult(JobOutcome.Failed,
                $"Registration with {spec.Server}/{spec.Site} failed (the host must exist in the site): {message}. Nothing was changed.");
        }

        var (after, error) = await ConnectionsAsync(paths, ct);
        if (error is not null || !after.Any(c => c.Matches(spec.Server!, spec.Site!)))
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Registration reported success, but no connection to {spec.Server}/{spec.Site} is listed. {error}".Trim());
        }

        foreach (var old in after.Where(c => !c.Matches(spec.Server!, spec.Site!)))
        {
            var delete = await TryRunAsync(paths.Controller, ["delete", old.Uuid], ct);
            log(delete is { Success: true } ? $"[agent] Old connection {old.SiteId} removed." : $"[agent] Removing {old.SiteId} failed: {(delete?.StandardError ?? "").Trim()}");
        }

        var replaced = before.Where(c => !c.Matches(spec.Server!, spec.Site!)).Select(c => c.SiteId).ToList();
        return new JobExecutionResult(JobOutcome.Succeeded,
            $"Registered with {spec.Server}/{spec.Site} as {spec.Hostname}." + (replaced.Count > 0 ? $" Removed: {string.Join(", ", replaced)}." : ""));
    }

    private static async Task<JobExecutionResult> SetOnlyFromAsync(CheckmkPaths paths, IReadOnlyList<string> onlyFrom, string backupRoot, Action<string> log, CancellationToken ct)
    {
        var file = paths.Xinetd ?? paths.UserYaml!;
        var backup = BackUp(backupRoot, MonitoringProducts.Checkmk, [file]);
        log($"[agent] Backup of {file}: {backup.Folder}");
        try
        {
            var (text, bom) = File.Exists(file) ? ReadText(file) : ("", false);
            var rewritten = paths.Xinetd is not null ? CheckmkConfig.RewriteXinetdOnlyFrom(text, onlyFrom) : CheckmkConfig.RewriteYamlOnlyFrom(text, onlyFrom);
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            }

            WriteText(file, rewritten, bom);
            log($"[agent] only_from = {string.Join(' ', onlyFrom)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            backup.Restore(log);
            return new JobExecutionResult(JobOutcome.Failed, $"Writing {file} failed: {e.Message}");
        }

        if (!await RestartAsync(paths, log, ct))
        {
            backup.Restore(log);
            await RestartAsync(paths, log, ct);
            return new JobExecutionResult(JobOutcome.Failed, "The Checkmk agent could not be restarted. The previous configuration was restored.");
        }

        return new JobExecutionResult(JobOutcome.Succeeded,
            $"only_from set to {string.Join(' ', onlyFrom)}. Whether the new site can query the agent cannot be checked from the host.");
    }

    public static async Task<bool> RestartAsync(CheckmkPaths paths, Action<string> log, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsServiceForImage(Path.GetDirectoryName(paths.Agent)!, "check_mk_agent.exe") is { } service
                   && await RestartWindowsServiceAsync(service, log, ct);
        }

        var unit = paths.Xinetd is not null ? "xinetd" : paths.Controller is not null ? "cmk-agent-ctl-daemon" : "check-mk-agent.socket";
        foreach (var args in new[] { new[] { "reload", unit }, new[] { "restart", unit } })
        {
            if (await TryRunAsync(Systemctl, args, ct) is { Success: true })
            {
                log($"[agent] systemctl {args[0]} {unit}");
                return true;
            }
        }

        // ohne systemd (sehr alte Systeme): SysV-Skript
        var result = await TryRunAsync(Which($"/etc/init.d/{unit}"), ["reload"], ct);
        log(result is { Success: true } ? $"[agent] /etc/init.d/{unit} reload" : $"[agent] Reloading {unit} failed.");
        return result is { Success: true };
    }

    [GeneratedRegex(@"CMK_VERSION\s*=\s*""?([0-9][^""\s]*)")]
    private static partial Regex ScriptVersionPattern();
}
