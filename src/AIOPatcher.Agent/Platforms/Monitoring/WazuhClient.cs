using System.Text.RegularExpressions;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using static AIOPatcher.Agent.Platforms.Monitoring.MonitoringSystem;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>Pfade des Wazuh-Agents (Linux /var/ossec, Windows ossec-agent unter Programme bzw. Programme (x86)).</summary>
public sealed record WazuhPaths(string Folder, string Config, string Keys, string[] StateFiles, string AgentAuth, string? Control, string DefaultPassFile)
{
    public static WazuhPaths? Find()
    {
        if (OperatingSystem.IsLinux())
        {
            const string folder = "/var/ossec";
            var config = Path.Combine(folder, "etc", "ossec.conf");
            // Manager: hat remoted. Den fassen wir nie an.
            if (!File.Exists(config) || File.Exists(Path.Combine(folder, "bin", "wazuh-remoted")) || File.Exists(Path.Combine(folder, "bin", "ossec-remoted")))
            {
                return null;
            }

            return new WazuhPaths(folder, config, Path.Combine(folder, "etc", "client.keys"),
                [Path.Combine(folder, "var", "run", "wazuh-agentd.state"), Path.Combine(folder, "var", "run", "ossec-agentd.state")],
                Path.Combine(folder, "bin", "agent-auth"),
                Which(Path.Combine(folder, "bin", "wazuh-control"), Path.Combine(folder, "bin", "ossec-control")),
                Path.Combine(folder, "etc", "authd.pass"));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var folder = Path.Combine(root, "ossec-agent");
                var config = Path.Combine(folder, "ossec.conf");
                if (root.Length > 0 && File.Exists(config))
                {
                    return new WazuhPaths(folder, config, Path.Combine(folder, "client.keys"),
                        [Path.Combine(folder, "wazuh-agent.state"), Path.Combine(folder, "ossec-agent.state")],
                        Path.Combine(folder, "agent-auth.exe"), null, Path.Combine(folder, "authd.pass"));
                }
            }
        }

        return null;
    }

    /// <summary>Pfad aus authorization_pass_path (relativ zum Installationsordner) bzw. Standard.</summary>
    public string PassFile(string? configured) =>
        configured is null ? DefaultPassFile : Path.IsPathRooted(configured) ? configured : Path.Combine(Folder, configured.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Wazuh-Agent erkennen und auf einen anderen Manager umstellen.</summary>
public static partial class WazuhClient
{
    public const string Package = "wazuh-agent";
    public static readonly string[] WindowsServices = ["WazuhSvc", "OssecSvc"];

    /// <summary>Wie lange nach dem Neustart auf "connected" gewartet wird.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static async Task<MonitoringClient?> DetectAsync(CancellationToken ct)
    {
        if (WazuhPaths.Find() is not { } paths)
        {
            return null;
        }

        WazuhConfigInfo info;
        string? detail = null;
        try
        {
            info = WazuhConfig.Parse(ReadText(paths.Config).Text);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            info = new WazuhConfigInfo(true, MonitoringVariants.Wazuh, [], null, null);
            detail = e.Message;
        }

        if (!info.IsAgent)
        {
            return null;
        }

        var version = await VersionAsync(paths, ct);
        var state = ReadState(paths);
        var canConfigure = detail is null;
        return new MonitoringClient(MonitoringProducts.Wazuh, info.Variant, MonitoringVersion.Display(version), await RunningAsync(paths, ct) ?? false,
            info.Addresses, WazuhConfig.ParseState(state), detail ?? (WazuhConfig.StateValue(state) is { } s ? $"status: {s}" : null), canConfigure,
            OperatingSystem.IsLinux() && await AptHeldAsync(Package, ct));
    }

    public static async Task<string?> VersionAsync(WazuhPaths paths, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            if (await DpkgVersionAsync(Package, ct) is { } dpkg)
            {
                return dpkg;
            }

            var init = Path.Combine(paths.Folder, "etc", "ossec-init.conf");
            return File.Exists(init) && InitVersionPattern().Match(File.ReadAllText(init)) is { Success: true } m ? m.Groups[1].Value : null;
        }

        if (OperatingSystem.IsWindows())
        {
            var file = Path.Combine(paths.Folder, "VERSION");
            if (File.Exists(file) && File.ReadLines(file).FirstOrDefault()?.Trim() is { Length: > 0 } line)
            {
                return line;
            }

            return WindowsUninstallEntry(n => n.StartsWith("Wazuh Agent", StringComparison.OrdinalIgnoreCase) || n.StartsWith("OSSEC", StringComparison.OrdinalIgnoreCase))?.Version;
        }

        return null;
    }

    public static async Task<bool?> RunningAsync(WazuhPaths paths, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsService(WindowsServices) is { } name ? await WindowsServiceRunningAsync(name, ct) : null;
        }

        if (await UnitActiveAsync(Package, ct) is { } active)
        {
            return active;
        }

        return await TryRunAsync(paths.Control, ["status"], ct) is { } status
            ? AgentdRunningPattern().IsMatch(status.StandardOutput)
            : null;
    }

    public static async Task<bool> RestartAsync(WazuhPaths paths, Action<string> log, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsService(WindowsServices) is { } name && await RestartWindowsServiceAsync(name, log, ct);
        }

        if (await UnitKnownAsync(Package, ct) && await TryRunAsync(Systemctl, ["restart", Package], ct) is { Success: true })
        {
            log($"[agent] systemctl restart {Package}");
            return true;
        }

        var result = await TryRunAsync(paths.Control, ["restart"], ct);
        log(result is { Success: true } ? $"[agent] {paths.Control} restart" : $"[agent] Restarting the Wazuh agent failed: {(result?.StandardError ?? "no control script").Trim()}");
        return result is { Success: true };
    }

    public static string? ReadState(WazuhPaths paths)
    {
        foreach (var file in paths.StateFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    return File.ReadAllText(file);
                }
            }
            catch (IOException)
            {
                // wird gerade geschrieben
            }
        }

        return null;
    }

    /// <summary>Prüft die Vorgabe, bevor etwas geändert wird. null = in Ordnung.</summary>
    public static string? Validate(MonitoringSpec spec, string? secret)
    {
        if (!ValidHost(spec.Address))
        {
            return "Invalid or missing manager address.";
        }

        if (spec.Port is < 1 or > 65535 || spec.EnrollmentPort is < 1 or > 65535)
        {
            return "Invalid port.";
        }

        if (spec.Protocol is not (null or "tcp" or "udp"))
        {
            return "Protocol must be tcp or udp.";
        }

        if (spec.Enrollment is not (null or "keys" or "new"))
        {
            return "Enrollment must be 'keys' or 'new'.";
        }

        if (!string.IsNullOrEmpty(spec.Groups) && !ValidName(spec.Groups))
        {
            return "Invalid agent groups.";
        }

        return secret is { Length: > 512 } || secret?.IndexOfAny(['\r', '\n']) >= 0 ? "Invalid enrollment password." : null;
    }

    /// <summary>
    /// Auf den Manager der Vorgabe umstellen: sichern, ossec.conf umschreiben, ggf. neu anmelden, neu starten, auf
    /// "connected" warten. Klappt das nicht, wird der alte Stand wiederhergestellt.
    /// </summary>
    public static async Task<JobExecutionResult> ConfigureAsync(MonitoringSpec spec, string? secret, string backupRoot, Action<string> log, CancellationToken ct)
    {
        if (WazuhPaths.Find() is not { } paths)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "No Wazuh agent installed on this host.");
        }

        if (Validate(spec, secret) is { } invalid)
        {
            return new JobExecutionResult(JobOutcome.Rejected, invalid);
        }

        var version = await VersionAsync(paths, ct);
        if (spec.ManagerVersion is { } manager && MonitoringVersion.Compare(version, manager) > 0)
        {
            return new JobExecutionResult(JobOutcome.Rejected,
                $"The Wazuh agent ({MonitoringVersion.Display(version)}) is newer than the manager ({manager}). Wazuh requires the manager to be the same version or newer; install the matching agent version first.");
        }

        var (text, bom) = ReadText(paths.Config);
        var info = WazuhConfig.Parse(text);
        if (!info.IsAgent)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "ossec.conf has no client section (Wazuh manager?).");
        }

        var newEnrollment = spec.Enrollment == "new";
        // <enrollment> gibt es ab 4.0; davor meldet agent-auth an. Unbekannte Version: 4.x annehmen.
        var enrollmentBlock = info.Variant == MonitoringVariants.Wazuh && (version is null || MonitoringVersion.Compare(version, "4.0.0") >= 0);

        string rewritten;
        try
        {
            rewritten = WazuhConfig.Rewrite(text, spec, newEnrollment, enrollmentBlock);
        }
        catch (InvalidOperationException e)
        {
            return new JobExecutionResult(JobOutcome.Rejected, e.Message);
        }

        var passFile = paths.PassFile(info.PassPath);
        var backup = BackUp(backupRoot, MonitoringProducts.Wazuh, [paths.Config, paths.Keys, passFile]);
        log($"[agent] Backup of the Wazuh configuration: {backup.Folder}");

        async Task<JobExecutionResult> RollBackAsync(string reason)
        {
            log($"[agent] {reason}");
            backup.Restore(log);
            await RestartAsync(paths, log, ct);
            return new JobExecutionResult(JobOutcome.Failed, $"{reason} The previous configuration was restored.");
        }

        try
        {
            WriteText(paths.Config, rewritten, bom);
            log($"[agent] ossec.conf: manager {spec.Address}{(spec.Port is { } p ? $":{p}" : "")}{(newEnrollment ? ", new enrollment" : ", keys kept")}");

            if (newEnrollment)
            {
                if (File.Exists(paths.Keys))
                {
                    WriteText(paths.Keys, "", false);
                    log("[agent] client.keys emptied (the agent enrolls with the new manager).");
                }

                if (!string.IsNullOrEmpty(secret))
                {
                    WriteText(passFile, secret + "\n", false);
                    await RestrictAsync(passFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, ["wazuh", "ossec"], ct);
                    log("[agent] Enrollment password written (authd.pass).");
                }

                if (!enrollmentBlock)
                {
                    // Vor 4.0: einmalig mit agent-auth anmelden (liest authd.pass selbst)
                    var args = new List<string> { "-m", spec.Address! };
                    if (spec.EnrollmentPort is { } enrollPort)
                    {
                        args.AddRange(["-p", enrollPort.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                    }

                    if (!string.IsNullOrEmpty(spec.Groups))
                    {
                        args.AddRange(["-G", spec.Groups]);
                    }

                    var auth = await TryRunAsync(File.Exists(paths.AgentAuth) ? paths.AgentAuth : null, args, ct);
                    if (auth is not { Success: true })
                    {
                        return await RollBackAsync($"Enrollment with agent-auth failed: {(auth?.StandardError + auth?.StandardOutput).Trim()}");
                    }

                    log("[agent] Enrolled with agent-auth.");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return await RollBackAsync($"Writing the configuration failed: {e.Message}");
        }

        if (!await RestartAsync(paths, log, ct))
        {
            return await RollBackAsync("The Wazuh agent could not be restarted.");
        }

        return await WaitConnectedAsync(paths, spec.Address!, log, ct) is { } failure
            ? await RollBackAsync(failure)
            : new JobExecutionResult(JobOutcome.Succeeded, $"Wazuh agent connected to {spec.Address}.");
    }

    /// <summary>Wartet auf "connected". null = verbunden (bzw. ohne State-Datei: Dienst läuft), sonst der Grund.</summary>
    public static async Task<string?> WaitConnectedAsync(WazuhPaths paths, string address, Action<string> log, CancellationToken ct)
    {
        // Der alte Status steht noch in der Datei, bis der Dienst sie neu schreibt.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        var until = DateTime.UtcNow + ConnectTimeout;
        string? last = null;
        while (DateTime.UtcNow < until)
        {
            var state = ReadState(paths);
            if (state is null)
            {
                // OSSEC/alte Versionen ohne State-Datei: nur prüfen, ob der Dienst läuft
                if (await RunningAsync(paths, ct) == true)
                {
                    log("[agent] No state file; the agent service is running (connection not verifiable).");
                    return null;
                }

                return "The Wazuh agent service is not running after the restart.";
            }

            if (WazuhConfig.ParseState(state) == true)
            {
                return null;
            }

            var value = WazuhConfig.StateValue(state);
            if (value != last)
            {
                log($"[agent] Agent status: {value}");
                last = value;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        return $"The agent did not connect to {address} within {ConnectTimeout.TotalMinutes:0} minutes (status: {last ?? "unknown"}).";
    }

    [GeneratedRegex(@"VERSION\s*=\s*""?v?([0-9][^""\s]*)")]
    private static partial Regex InitVersionPattern();

    [GeneratedRegex(@"(wazuh|ossec)-agentd is running", RegexOptions.IgnoreCase)]
    private static partial Regex AgentdRunningPattern();
}
