using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using Microsoft.Win32;

namespace AIOPatcher.Agent.Compliance;

/// <summary>
/// Windows: Windows-Firewall (Profile, Regelgruppe "AIOPatcher", Sicherung per "netsh advfirewall export"),
/// Remotedesktop (Registry, Gruppenrichtlinien haben Vorrang), OpenSSH-Server (nur lesen) und lokale Benutzer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCompliance(ComplianceStore store) : IComplianceProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private const string RdpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string AdminsSid = "S-1-5-32-544";

    /// <summary>Feste RDP-Empfehlungen: Id → (Registry-Wert, empfohlener Wert, ok ab).</summary>
    private static readonly (string Id, string Value, int Recommended)[] RdpChecks =
    [
        ("rdp.nla", "UserAuthentication", 1),
        ("rdp.securityLayer", "SecurityLayer", 2),
        ("rdp.encryption", "MinEncryptionLevel", 3),
    ];

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Skript über -EncodedCommand: keine Anführungszeichen-Probleme, Eingaben stehen nie auf der Kommandozeile.</summary>
    private static Task<ProcessResult> RunScriptAsync(string script, CancellationToken ct, string? stdin = null) =>
        ProcessRunner.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
             Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + script))],
            Timeout, ct, stdin);

    public async Task<IReadOnlyList<RemoteAccessSetting>> RemoteAccessAsync(CancellationToken ct)
    {
        var remote = RdpSettings();
        remote.AddRange(await OpenSshAsync(ct));
        return remote;
    }

    async Task<IReadOnlyList<LocalUser>> IComplianceProvider.UsersAsync(CancellationToken ct) => await UsersAsync(ct);

    // --- Lesen ----------------------------------------------------------------------------------------

    public async Task<FirewallStatus> FirewallAsync(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $profiles = @(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Name = [string]$_.Name; Enabled = ([string]$_.Enabled -eq 'True'); In = [string]$_.DefaultInboundAction; Out = [string]$_.DefaultOutboundAction } })
            $managed = @(Get-NetFirewallRule -Group 'AIOPatcher' -ErrorAction SilentlyContinue | ForEach-Object {
              $pf = $_ | Get-NetFirewallPortFilter; $af = $_ | Get-NetFirewallAddressFilter
              '{0} {1} {2} from {3} ({4}){5}' -f $_.Action, $pf.Protocol, (@($pf.LocalPort) -join ','), (@($af.RemoteAddress) -join ','), $_.DisplayName, $(if ([string]$_.Enabled -eq 'True') { '' } else { ' [disabled]' })
            })
            $other = @(Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { $_.Group -ne 'AIOPatcher' }).Count
            ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{ Profiles = $profiles; Rules = $managed; Other = $other })
            """;
        var result = await RunScriptAsync(script, ct);
        if (!result.Success)
        {
            return new FirewallStatus("windows", false, false, null, null, [], 0, store.AppliedHash, store.HasOriginal, result.StandardError.Trim());
        }

        using var json = JsonDocument.Parse(result.StandardOutput);
        var profiles = json.RootElement.GetProperty("Profiles").EnumerateArray()
            .Select(p => (Name: p.GetProperty("Name").GetString()!, Enabled: p.GetProperty("Enabled").GetBoolean(), In: Action(p.GetProperty("In").GetString()), Out: Action(p.GetProperty("Out").GetString(), outbound: true)))
            .ToList();
        var rules = json.RootElement.GetProperty("Rules").ValueKind == JsonValueKind.Array
            ? json.RootElement.GetProperty("Rules").EnumerateArray().Select(r => new FirewallRuleInfo(r.GetString() ?? "", true)).ToList()
            : [];
        var detail = string.Join(", ", profiles.Select(p => $"{p.Name}: {(p.Enabled ? "on" : "off")}"));
        return new FirewallStatus("windows", profiles.Count > 0 && profiles.All(p => p.Enabled), true,
            Distinct(profiles.Select(p => p.In)), Distinct(profiles.Select(p => p.Out)), rules,
            json.RootElement.GetProperty("Other").GetInt32(), store.AppliedHash, store.HasOriginal, detail);

        static string Action(string? value, bool outbound = false) => value switch
        {
            "Allow" => "allow",
            "Block" => "deny",
            _ => outbound ? "allow" : "deny", // NotConfigured = Windows-Standard (ein: blockieren, aus: erlauben)
        };

        static string Distinct(IEnumerable<string> values) => values.Distinct().ToList() is { Count: 1 } one ? one[0] : "mixed";
    }

    private List<RemoteAccessSetting> RdpSettings()
    {
        using var ts = Registry.LocalMachine.OpenSubKey(TsKey);
        using var rdp = Registry.LocalMachine.OpenSubKey(RdpKey);
        using var policy = Registry.LocalMachine.OpenSubKey(RdpPolicyKey);
        if (rdp is null)
        {
            return [];
        }

        var enabled = ts?.GetValue("fDenyTSConnections") is int deny && deny == 0;
        var settings = new List<RemoteAccessSetting>
        {
            new("rdp.enabled", enabled ? "on" : "off", true, false),
            new("rdp.port", rdp.GetValue("PortNumber")?.ToString(), true, false),
        };

        var managed = LoadOriginals().Keys.ToHashSet();
        foreach (var (id, value, recommended) in RdpChecks)
        {
            var byPolicy = policy?.GetValue(value);
            var current = byPolicy ?? rdp.GetValue(value);
            var ok = current is int n && (value == "MinEncryptionLevel" ? n >= recommended : n == recommended);
            settings.Add(new RemoteAccessSetting(id, current?.ToString(), ok, byPolicy is null,
                byPolicy is not null ? "group-policy" : managed.Contains(value) ? "managed" : null));
        }

        return settings;
    }

    private static async Task<List<RemoteAccessSetting>> OpenSshAsync(CancellationToken ct)
    {
        var sshd = Path.Combine(Environment.SystemDirectory, "OpenSSH", "sshd.exe");
        if (!File.Exists(sshd))
        {
            return [];
        }

        try
        {
            var result = await ProcessRunner.RunAsync(sshd, ["-T"], TimeSpan.FromSeconds(20), ct);
            return result.Success
                ? ComplianceParsers.EvaluateSsh(ComplianceParsers.ParseSshdEffective(result.StandardOutput), fixable: false, "windows")
                : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            return [];
        }
    }

    private static async Task<List<LocalUser>> UsersAsync(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $sid = 'S-1-5-32-544'
            try { $admins = @(Get-LocalGroupMember -SID $sid | ForEach-Object { $_.SID.Value }) }
            catch {
              # Get-LocalGroupMember scheitert an verwaisten SIDs: dann über ADSI.
              $group = (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
              $admins = @(([ADSI]"WinNT://./$group,group").psbase.Invoke('Members') | ForEach-Object {
                (New-Object Security.Principal.SecurityIdentifier ([byte[]]$_.GetType().InvokeMember('objectSid', 'GetProperty', $null, $_, $null)), 0).Value })
            }
            $users = @(Get-LocalUser | ForEach-Object { [pscustomobject]@{
              Name = $_.Name; Enabled = [bool]$_.Enabled; Sid = $_.SID.Value; FullName = $_.FullName
              LastLogon = $(if ($_.LastLogon) { $_.LastLogon.ToUniversalTime().ToString('o') } else { $null })
              PasswordSet = [bool]$_.PasswordLastSet; Admin = ($admins -contains $_.SID.Value) } })
            ConvertTo-Json -Compress -InputObject $users
            """;
        ProcessResult result;
        try
        {
            result = await RunScriptAsync(script, ct);
        }
        catch (TimeoutException)
        {
            return [];
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return []; // z. B. Domänencontroller: keine lokalen Benutzer
        }

        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.EnumerateArray().Select(u => new LocalUser(
                u.GetProperty("Name").GetString()!,
                u.GetProperty("Enabled").GetBoolean(),
                u.GetProperty("Admin").GetBoolean(),
                u.GetProperty("Sid").GetString()!.EndsWith("-500", StringComparison.Ordinal),
                u.GetProperty("PasswordSet").GetBoolean() ? "set" : "unknown", // Windows verrät nicht, ob ein Passwort gesetzt ist
                u.GetProperty("LastLogon").ValueKind == JsonValueKind.String ? DateTime.Parse(u.GetProperty("LastLogon").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                0,
                u.GetProperty("FullName").GetString() is { Length: > 0 } full ? full : null))
            .OrderBy(u => u.IsBuiltIn ? 0 : 1).ThenBy(u => u.Name)
            .ToList();
    }

    // --- Ändern ---------------------------------------------------------------------------------------

    public Task<JobExecutionResult> ApplyAsync(SystemChange change, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct) =>
        change.Kind switch
        {
            SystemChangeKind.FirewallApply when change.Firewall is { } config => ApplyFirewallAsync(config, serverReachable, log, ct),
            SystemChangeKind.FirewallRestoreOriginal => RestoreOriginalAsync(log, ct),
            SystemChangeKind.RemoteAccessFix => Task.FromResult(RdpFix(change.Settings ?? [], log)),
            SystemChangeKind.RemoteAccessRevert => Task.FromResult(RdpRevert(log)),
            SystemChangeKind.UserAddSshKey or SystemChangeKind.UserRemoveSshKey =>
                Task.FromResult(Rejected("SSH keys are managed on Linux hosts only.")),
            >= SystemChangeKind.UserLock and <= SystemChangeKind.UserRemoveSshKey => UserChangeAsync(change, log, ct),
            _ => Task.FromResult(Rejected("Change is incomplete.")),
        };

    private static JobExecutionResult Rejected(string message) => new(JobOutcome.Rejected, message);

    private string DisabledRulesFile => Path.Combine(store.FirewallDir, "disabled-rules.txt");

    private async Task<JobExecutionResult> ApplyFirewallAsync(FirewallConfig config, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct)
    {
        if (FirewallRules.Validate(config) is { Count: > 0 } errors)
        {
            return Rejected("Invalid firewall configuration: " + string.Join(" ", errors));
        }

        var backup = store.NewFirewallBackupDir();
        var backupFile = Path.Combine(backup, "backup.wfw");
        if (!await ExportAsync(backupFile, log, ct))
        {
            return new JobExecutionResult(JobOutcome.Failed, "Could not back up the firewall (netsh advfirewall export); nothing changed.");
        }

        if (!store.HasOriginal)
        {
            Directory.CreateDirectory(store.FirewallOriginal);
            File.Copy(backupFile, Path.Combine(store.FirewallOriginal, "backup.wfw"));
            log("[agent] Saved the original firewall state (restorable from the dashboard).");
        }

        await File.WriteAllTextAsync(store.FirewallPending, backup, ct);
        RevertWatchdog.ScheduleWindows(store.FirewallPending, $"& {Q(Netsh)} advfirewall import {Q(backupFile)}", log);

        log($"[agent] Applying {config.Rules.Count} rule(s) in group '{ComplianceParsers.WindowsRuleGroup}'{(config.Exclusive ? ", other inbound rules disabled" : "")} ...");
        var result = await RunScriptAsync(ComplianceParsers.WindowsFirewallScript(config, DisabledRulesFile), ct);
        if (!result.Success)
        {
            log($"[agent] ERROR: {result.StandardError.Trim()}");
            await ImportAsync(backupFile, log, ct);
            File.Delete(store.FirewallPending);
            return new JobExecutionResult(JobOutcome.Failed, $"Applying the rules failed: {FirstLine(result.StandardError)}. Previous state restored.");
        }

        if (!await RevertWatchdog.VerifyAsync(serverReachable, log, ct))
        {
            await ImportAsync(backupFile, log, ct);
            File.Delete(store.FirewallPending);
            return new JobExecutionResult(JobOutcome.Failed, "Server was not reachable after applying the rules. Previous state restored.");
        }

        File.Delete(store.FirewallPending);
        await File.WriteAllTextAsync(store.FirewallAppliedHash, FirewallRules.Hash(config), ct);
        return new JobExecutionResult(JobOutcome.Succeeded,
            $"Firewall applied: {config.Rules.Count} rule(s), incoming {config.IncomingDefault}{(config.Exclusive ? ", exclusive" : "")}, firewall {(config.Enabled ? "on" : "off")}.");
    }

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    private static async Task<bool> ExportAsync(string file, Action<string> log, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(Netsh, ["advfirewall", "export", file], Timeout, ct);
        log(result.Success ? $"[agent] Firewall backed up to {file}." : $"[agent] ERROR: {result.StandardOutput.Trim()}");
        return result.Success && File.Exists(file);
    }

    private static async Task ImportAsync(string file, Action<string> log, CancellationToken ct)
    {
        log("[agent] Restoring previous firewall state ...");
        var result = await ProcessRunner.RunAsync(Netsh, ["advfirewall", "import", file], Timeout, ct);
        log(result.Success ? "[agent] Previous firewall state restored." : $"[agent] ERROR while restoring: {result.StandardOutput.Trim()}");
    }

    private async Task<JobExecutionResult> RestoreOriginalAsync(Action<string> log, CancellationToken ct)
    {
        var original = Path.Combine(store.FirewallOriginal, "backup.wfw");
        if (!File.Exists(original))
        {
            return Rejected("No original state saved: AIOPatcher has not changed this firewall yet.");
        }

        await ImportAsync(original, log, ct);
        File.Delete(store.FirewallAppliedHash);
        File.Delete(DisabledRulesFile);
        return new JobExecutionResult(JobOutcome.Succeeded, "Firewall restored to the state before AIOPatcher's first change.");
    }

    // RDP: ursprüngliche Werte einmalig merken, damit "Zurücksetzen" den Stand vor AIOPatcher herstellt.
    private Dictionary<string, int?> LoadOriginals() =>
        File.Exists(store.RemoteAccessOriginal)
            ? JsonSerializer.Deserialize(File.ReadAllText(store.RemoteAccessOriginal), Maintenance.UpdateStateJsonContext.Default.DictionaryStringNullableInt32) ?? []
            : [];

    private JobExecutionResult RdpFix(IReadOnlyList<string> settings, Action<string> log)
    {
        var chosen = RdpChecks.Where(c => settings.Contains(c.Id)).ToList();
        if (chosen.Count == 0)
        {
            return Rejected("No RDP recommendation selected (SSH on Windows is read-only).");
        }

        using var policy = Registry.LocalMachine.OpenSubKey(RdpPolicyKey);
        var byPolicy = chosen.Where(c => policy?.GetValue(c.Value) is not null).Select(c => c.Id).ToList();
        if (byPolicy.Count > 0)
        {
            return Rejected($"{string.Join(", ", byPolicy)} set by group policy; change it there.");
        }

        using var rdp = Registry.LocalMachine.OpenSubKey(RdpKey, writable: true);
        if (rdp is null)
        {
            return Rejected("Remote Desktop is not installed.");
        }

        var originals = LoadOriginals();
        foreach (var (id, value, recommended) in chosen)
        {
            if (!originals.ContainsKey(value))
            {
                originals[value] = rdp.GetValue(value) as int?;
            }

            rdp.SetValue(value, recommended, RegistryValueKind.DWord);
            log($"[agent] RDP {value} = {recommended} ({id})");
        }

        Directory.CreateDirectory(store.Root);
        File.WriteAllText(store.RemoteAccessOriginal, JsonSerializer.Serialize(originals, Maintenance.UpdateStateJsonContext.Default.DictionaryStringNullableInt32));
        return new JobExecutionResult(JobOutcome.Succeeded, $"RDP: {string.Join(", ", chosen.Select(c => c.Id))} applied. New sessions use them, open sessions stay connected.");
    }

    private JobExecutionResult RdpRevert(Action<string> log)
    {
        var originals = LoadOriginals();
        if (originals.Count == 0)
        {
            return new JobExecutionResult(JobOutcome.Succeeded, "No RDP settings from AIOPatcher present.");
        }

        using var rdp = Registry.LocalMachine.OpenSubKey(RdpKey, writable: true);
        foreach (var (value, original) in originals)
        {
            if (original is { } v)
            {
                rdp?.SetValue(value, v, RegistryValueKind.DWord);
            }
            else
            {
                rdp?.DeleteValue(value, throwOnMissingValue: false);
            }

            log($"[agent] RDP {value} restored to {original?.ToString() ?? "(default)"}");
        }

        File.Delete(store.RemoteAccessOriginal);
        return new JobExecutionResult(JobOutcome.Succeeded, "RDP settings restored to the state before AIOPatcher.");
    }

    private async Task<JobExecutionResult> UserChangeAsync(SystemChange change, Action<string> log, CancellationToken ct)
    {
        var name = change.UserName;
        if (!ComplianceParsers.IsValidUserName(name, windows: true))
        {
            return Rejected("Invalid user name.");
        }

        var users = await UsersAsync(ct);
        var user = users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
        if (user is null)
        {
            return Rejected($"Local user {name} not found.");
        }

        string script;
        string success;
        string? stdin = null;
        switch (change.Kind)
        {
            case SystemChangeKind.UserLock:
                if (user.IsAdmin && !ComplianceParsers.HasOtherUsableAdmin(users, user.Name))
                {
                    return Rejected($"{user.Name} is the last enabled administrator with a password. Disabling it would lock you out.");
                }

                script = $"Disable-LocalUser -Name {Q(user.Name)}";
                success = $"{user.Name} disabled.";
                break;
            case SystemChangeKind.UserUnlock:
                script = $"Enable-LocalUser -Name {Q(user.Name)}";
                success = $"{user.Name} enabled.";
                break;
            case SystemChangeKind.UserGrantAdmin:
                script = $"Add-LocalGroupMember -SID '{AdminsSid}' -Member {Q(user.Name)}";
                success = $"{user.Name} added to Administrators.";
                break;
            case SystemChangeKind.UserRevokeAdmin:
                if (!ComplianceParsers.HasOtherUsableAdmin(users, user.Name))
                {
                    return Rejected($"{user.Name} is the last enabled administrator with a password.");
                }

                script = $"Remove-LocalGroupMember -SID '{AdminsSid}' -Member {Q(user.Name)}";
                success = $"{user.Name} removed from Administrators.";
                break;
            case SystemChangeKind.UserSetPassword:
                if (!ComplianceParsers.IsValidPassword(change.Secret))
                {
                    return Rejected("Password must be 12 to 128 characters without line breaks.");
                }

                // Passwort als Base64 über stdin: steht nie auf der Kommandozeile und übersteht jede Konsolen-Codepage.
                stdin = Convert.ToBase64String(Encoding.UTF8.GetBytes(change.Secret!)) + "\n";
                script = "$plain = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadLine()))\n" +
                         "$secure = ConvertTo-SecureString -String $plain -AsPlainText -Force; $plain = $null\n" +
                         $"Set-LocalUser -Name {Q(user.Name)} -Password $secure";
                success = $"Password for {user.Name} changed.";
                log($"[agent] Setting password for {user.Name} (value is not logged).");
                break;
            default:
                return Rejected($"Unknown change {change.Kind}.");
        }

        if (change.Kind != SystemChangeKind.UserSetPassword)
        {
            log($"[agent] {script}");
        }

        var result = await RunScriptAsync("$ErrorActionPreference = 'Stop'\n" + script, ct, stdin);
        return result.Success
            ? new JobExecutionResult(JobOutcome.Succeeded, success)
            : new JobExecutionResult(JobOutcome.Failed, $"Failed: {FirstLine(result.StandardError)}");
    }
}
