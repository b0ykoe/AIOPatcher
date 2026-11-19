using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using static AIOPatcher.Agent.Platforms.Monitoring.MonitoringSystem;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>
/// Monitoring-Clients installieren, aktualisieren und deinstallieren. Pakete kommen nur vom AIOPatcher-Server (der sie
/// beim Wazuh-Paketserver bzw. bei der Checkmk-Site geholt hat) und werden per SHA-256 geprüft; danach stellt die
/// normale Konfiguration den Client auf das Ziel ein.
/// </summary>
public static partial class MonitoringInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    public static async Task<JobExecutionResult> InstallAsync(AgentState state, MonitoringSpec spec, string? secret, Action<string> log, CancellationToken ct)
    {
        if (spec.Package is not { } package || !PackageNamePattern().IsMatch(package.FileName) || !HexPattern().IsMatch(package.Sha256))
        {
            return new JobExecutionResult(JobOutcome.Rejected, "No valid package given.");
        }

        var msi = package.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        if (msi != OperatingSystem.IsWindows() || !msi && !File.Exists("/usr/bin/apt-get"))
        {
            return new JobExecutionResult(JobOutcome.Rejected, $"Package {package.FileName} does not fit this system.");
        }

        if (spec.Product == MonitoringProducts.Wazuh)
        {
            if (WazuhClient.Validate(spec, secret) is { } invalid)
            {
                return new JobExecutionResult(JobOutcome.Rejected, invalid);
            }

            if (MonitoringVersion.Parse(package.Version) is [>= 5, ..] && !SupportsWazuh5())
            {
                return new JobExecutionResult(JobOutcome.Rejected,
                    "Wazuh 5 agents do not support this operating system (needs Windows 10 / Server 2012 R2, Debian 10 or Ubuntu 20.04 or newer). Keep a 4.x manager for these hosts.");
            }
        }
        else if (spec.Product != MonitoringProducts.Checkmk)
        {
            return new JobExecutionResult(JobOutcome.Rejected, $"Unknown monitoring product {spec.Product}.");
        }

        var folder = Path.Combine(state.Directory, "monitoring-downloads");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, package.FileName);
        try
        {
            log($"[agent] Downloading {package.FileName} ({package.Size / 1024 / 1024.0:0.0} MB) …");
            using (var client = ServerClient.FromState(state))
            {
                await client.DownloadAsync(package.Path, file, ct);
            }

            await using (var stream = File.OpenRead(file))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new JobExecutionResult(JobOutcome.Failed, "SHA-256 of the downloaded package does not match. Nothing was installed.");
                }
            }

            if (msi && spec.Product == MonitoringProducts.Wazuh && await SignerAsync(file, ct) is var signer && signer?.Contains("Wazuh", StringComparison.OrdinalIgnoreCase) != true)
            {
                return new JobExecutionResult(JobOutcome.Failed, $"The package is not signed by Wazuh (signer: {signer ?? "none"}). Nothing was installed.");
            }

            var fresh = spec.Product == MonitoringProducts.Wazuh ? WazuhPaths.Find() is null : CheckmkPaths.Find() is null;
            var installed = msi ? await InstallMsiAsync(file, log, ct) : await InstallDebAsync(file, spec.Product, log, ct);
            if (installed is not null)
            {
                return new JobExecutionResult(JobOutcome.Failed, installed);
            }

            log($"[agent] {(fresh ? "Installed" : "Updated")} {package.FileName}.");
            // Neu installiert: Wazuh hat noch keinen Schlüssel, also beim Manager anmelden
            var target = spec.Product == MonitoringProducts.Wazuh && fresh ? spec with { Enrollment = "new" } : spec;
            var configured = spec.Product == MonitoringProducts.Wazuh
                ? await WazuhClient.ConfigureAsync(target, secret, state.Directory, log, ct)
                : await CheckmkClient.ConfigureAsync(target, secret, state.Directory, log, ct);

            return configured.Outcome == JobOutcome.Succeeded
                ? configured with { Summary = $"{(fresh ? "Installed" : "Updated to")} {spec.Product} {package.Version}. {configured.Summary}" }
                : configured with
                {
                    Outcome = JobOutcome.Failed,
                    Summary = $"{spec.Product} {package.Version} is installed, but configuring it failed: {configured.Summary}",
                };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Installing {package.FileName} failed: {e.Message}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // bleibt liegen, wird beim nächsten Mal überschrieben
            }
        }
    }

    public static async Task<JobExecutionResult> UninstallAsync(string product, string backupRoot, Action<string> log, CancellationToken ct)
    {
        string[] keep;
        if (product == MonitoringProducts.Wazuh)
        {
            var paths = WazuhPaths.Find();
            if (paths is null && !(OperatingSystem.IsLinux() && await DpkgVersionAsync(WazuhClient.Package, ct) is not null))
            {
                return new JobExecutionResult(JobOutcome.Rejected, "No Wazuh agent installed on this host.");
            }

            keep = paths is null ? [] : [paths.Config, paths.Keys, paths.DefaultPassFile];
        }
        else if (product == MonitoringProducts.Checkmk)
        {
            var paths = CheckmkPaths.Find();
            if (paths is null)
            {
                return new JobExecutionResult(JobOutcome.Rejected, "No Checkmk agent installed on this host.");
            }

            keep = new[] { paths.Xinetd, paths.UserYaml, paths.Ini, OperatingSystem.IsLinux() ? "/var/lib/cmk-agent/registered_connections.json" : null }.OfType<string>().ToArray();
            if (paths.Controller is not null && await TryRunAsync(paths.Controller, ["delete-all"], ct) is { Success: true })
            {
                log("[agent] Registrations removed (cmk-agent-ctl delete-all).");
            }
        }
        else
        {
            return new JobExecutionResult(JobOutcome.Rejected, $"Unknown monitoring product {product}.");
        }

        var backup = BackUp(backupRoot, product, keep);
        log($"[agent] Configuration saved to {backup.Folder} (for a later reinstall with the same identity).");

        string? error;
        if (OperatingSystem.IsWindows())
        {
            var entry = WindowsUninstallEntry(product == MonitoringProducts.Wazuh
                ? n => n.StartsWith("Wazuh Agent", StringComparison.OrdinalIgnoreCase)
                : CheckmkClient.IsCheckmkDisplayName);
            error = entry?.ProductCode is { } code
                ? await MsiExecAsync(["/x", code, "/qn", "/norestart"], log, ct)
                : "No Windows Installer entry found; uninstall the client manually.";
        }
        else
        {
            var package = product == MonitoringProducts.Wazuh ? WazuhClient.Package : CheckmkClient.Package;
            await TryRunAsync(Which("/usr/bin/apt-mark"), ["unhold", package], ct);
            var result = await ProcessRunnerAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=120", "purge", "-y", package], log, ct);
            error = result == 0 ? null : $"apt-get purge {package} failed (exit code {result}).";
            if (error is null && product == MonitoringProducts.Checkmk)
            {
                foreach (var xinetd in new[] { "/etc/xinetd.d/check-mk-agent", "/etc/xinetd.d/check_mk" }.Where(File.Exists))
                {
                    File.Delete(xinetd);
                    await TryRunAsync(Systemctl, ["reload", "xinetd"], ct);
                }
            }
        }

        if (error is not null)
        {
            return new JobExecutionResult(JobOutcome.Failed, error);
        }

        var left = product == MonitoringProducts.Wazuh
            ? WazuhPaths.Find() is not null || OperatingSystem.IsLinux() && await DpkgVersionAsync(WazuhClient.Package, ct) is not null
            : CheckmkPaths.Find() is not null;
        return left
            ? new JobExecutionResult(JobOutcome.Failed, $"The {product} client was uninstalled, but files or the package are still present. Check the host.")
            : new JobExecutionResult(JobOutcome.Succeeded, $"{product} client uninstalled. The host stays listed in the {(product == MonitoringProducts.Wazuh ? "Wazuh manager" : "Checkmk site")} until removed there.");
    }

    // --- Installer ------------------------------------------------------------------------------------

    private static async Task<string?> InstallDebAsync(string file, string product, Action<string> log, CancellationToken ct)
    {
        var package = product == MonitoringProducts.Wazuh ? WazuhClient.Package : CheckmkClient.Package;
        await TryRunAsync(Which("/usr/bin/apt-mark"), ["unhold", package], ct);
        var code = await ProcessRunnerAsync("/usr/bin/apt-get",
            ["-o", "DPkg::Lock::Timeout=120", "install", "-y", "--allow-downgrades", "--allow-change-held-packages", file], log, ct);
        if (code != 0)
        {
            return $"apt-get install failed (exit code {code}).";
        }

        if (product == MonitoringProducts.Wazuh)
        {
            // Normales Patchen soll den Client nicht über die Version des Managers heben.
            await TryRunAsync(Which("/usr/bin/apt-mark"), ["hold", package], ct);
            log($"[agent] apt-mark hold {package} (updates only via AIOPatcher, matching the manager version).");
            await TryRunAsync(Systemctl, ["daemon-reload"], ct);
            await TryRunAsync(Systemctl, ["enable", package], ct);
        }

        return null;
    }

    private static Task<string?> InstallMsiAsync(string file, Action<string> log, CancellationToken ct) =>
        MsiExecAsync(["/i", file, "/qn", "/norestart"], log, ct);

    private static async Task<string?> MsiExecAsync(IEnumerable<string> args, Action<string> log, CancellationToken ct)
    {
        var code = await ProcessRunnerAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), args, log, ct);
        // 3010/1641: erfolgreich, Neustart nötig bzw. eingeleitet (der Agent startet nie selbst neu)
        return code is 0 or 3010 or 1641 ? null : $"msiexec failed (exit code {code}).";
    }

    private static async Task<int> ProcessRunnerAsync(string file, IEnumerable<string> args, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await Inventory.ProcessRunner.RunStreamingAsync(file, args, InstallTimeout, line => log(line), ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            log($"[agent] {e.Message}");
            return -1;
        }
    }

    /// <summary>Authenticode-Signierer einer MSI (Subject), null = nicht oder ungültig signiert.</summary>
    private static async Task<string?> SignerAsync(string file, CancellationToken ct)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var script = "$s = Get-AuthenticodeSignature -LiteralPath $env:AIOP_FILE; if ($s.Status -eq 'Valid') { $s.SignerCertificate.Subject }";
        var result = await TryRunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script.Replace("$env:AIOP_FILE", $"'{file.Replace("'", "''")}'")], ct);
        return result is { Success: true } && result.StandardOutput.Trim() is { Length: > 0 } subject ? subject : null;
    }

    /// <summary>Wazuh 5 laut Doku: Windows 10 / Server 2012 R2 und neuer, Debian 10+, Ubuntu 20.04+.</summary>
    private static bool SupportsWazuh5()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.OSVersion.Version >= new Version(6, 3);
        }

        if (!File.Exists("/etc/os-release"))
        {
            return true;
        }

        var lines = File.ReadAllLines("/etc/os-release");
        string? Value(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim('"');
        var version = MonitoringVersion.Parse(Value("VERSION_ID"));
        return (Value("ID"), version) switch
        {
            ("ubuntu", [var major, ..]) => major >= 20,
            ("debian", [var major, ..]) => major >= 10,
            _ => true,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9._+~\-]{1,120}\.(deb|msi)$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageNamePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex HexPattern();
}
