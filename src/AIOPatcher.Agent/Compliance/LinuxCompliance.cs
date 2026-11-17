using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Compliance;

/// <summary>
/// Linux: Firewall (Status von ufw, firewalld, nftables/iptables, Proxmox-Firewall; verwalten nur ufw),
/// OpenSSH (effektive Konfiguration per "sshd -T", Änderungen nur über eine eigene Drop-in-Datei) und lokale Benutzer.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LinuxCompliance(ComplianceStore store) : IComplianceProvider
{
    private const string SshDropInPath = "/etc/ssh/sshd_config.d/00-aiopatcher.conf";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    private static bool IsProxmox => File.Exists("/usr/bin/pveversion");

    private static string? Which(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static string? Ufw => Which("/usr/sbin/ufw", "/sbin/ufw", "/usr/bin/ufw");

    private static string? Sshd => Which("/usr/sbin/sshd", "/sbin/sshd");

    private static async Task<ProcessResult?> TryRunAsync(string? file, string[] args, CancellationToken ct, string? stdin = null)
    {
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        try
        {
            return await ProcessRunner.RunAsync(file, args, Short, ct, stdin);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    public async Task<IReadOnlyList<RemoteAccessSetting>> RemoteAccessAsync(CancellationToken ct) => await SshAsync(ct);

    async Task<IReadOnlyList<LocalUser>> IComplianceProvider.UsersAsync(CancellationToken ct) => await UsersAsync(ct);

    // --- Lesen ----------------------------------------------------------------------------------------

    public async Task<FirewallStatus> FirewallAsync(CancellationToken ct)
    {
        var proxmox = IsProxmox;
        var ufwPath = Ufw;
        ComplianceParsers.UfwStatus? ufw = null;
        if (await TryRunAsync(ufwPath, ["status", "verbose"], ct) is { Success: true } status)
        {
            ufw = ComplianceParsers.ParseUfwStatus(status.StandardOutput);
        }

        if (ufw is { Active: true })
        {
            return new FirewallStatus("ufw", true, !proxmox, ufw.Incoming, ufw.Outgoing, ufw.Rules.Take(200).ToList(),
                ufw.Rules.Count(r => !r.Managed), store.AppliedHash, store.HasOriginal);
        }

        if (proxmox && await TryRunAsync(Which("/usr/sbin/pve-firewall"), ["status"], ct) is { Success: true } pve)
        {
            var enabled = pve.StandardOutput.Contains("enabled", StringComparison.OrdinalIgnoreCase);
            return new FirewallStatus("pve-firewall", enabled, false, null, null, [], 0, null, false, pve.StandardOutput.Trim());
        }

        if (await TryRunAsync(Which("/usr/bin/firewall-cmd", "/bin/firewall-cmd"), ["--state"], ct) is { Success: true } fwd && fwd.StandardOutput.Contains("running"))
        {
            var all = await TryRunAsync(Which("/usr/bin/firewall-cmd", "/bin/firewall-cmd"), ["--list-all"], ct);
            var lines = (all?.StandardOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new FirewallStatus("firewalld", true, false, null, null, lines.Take(50).Select(l => new FirewallRuleInfo(l, false)).ToList(), lines.Length, null, false);
        }

        if (await TryRunAsync(Which("/usr/sbin/nft", "/sbin/nft"), ["list", "ruleset"], ct) is { Success: true } nft
            && nft.StandardOutput.Contains("hook input", StringComparison.Ordinal)
            && (nft.StandardOutput.Contains("policy drop", StringComparison.Ordinal) || nft.StandardOutput.Contains(" drop", StringComparison.Ordinal) || nft.StandardOutput.Contains(" reject", StringComparison.Ordinal)))
        {
            var rules = nft.StandardOutput.Split('\n').Count(l => l.Contains(" accept") || l.Contains(" drop") || l.Contains(" reject"));
            return new FirewallStatus("nftables", true, false, nft.StandardOutput.Contains("policy drop") ? "drop" : null, null, [], rules, null, false);
        }

        if (await TryRunAsync(Which("/usr/sbin/iptables", "/sbin/iptables"), ["-S", "INPUT"], ct) is { Success: true } ipt)
        {
            var lines = ipt.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var policy = lines.FirstOrDefault(l => l.StartsWith("-P INPUT ", StringComparison.Ordinal))?[9..].Trim().ToLowerInvariant();
            var rules = lines.Count(l => l.StartsWith("-A INPUT", StringComparison.Ordinal));
            if (rules > 0 || policy is "drop" or "reject")
            {
                return new FirewallStatus("iptables", true, false, policy, null, lines.Where(l => l.StartsWith("-A")).Take(50).Select(l => new FirewallRuleInfo(l, false)).ToList(), rules, null, false);
            }
        }

        return ufwPath is not null
            ? new FirewallStatus("ufw", false, !proxmox, ufw?.Incoming, ufw?.Outgoing, [], 0, store.AppliedHash, store.HasOriginal)
            : new FirewallStatus("none", false, false, null, null, [], 0, null, false, proxmox ? null : "ufw not installed (apt install ufw)");
    }

    private static async Task<List<RemoteAccessSetting>> SshAsync(CancellationToken ct)
    {
        if (await TryRunAsync(Sshd, ["-T"], ct) is not { Success: true } effective)
        {
            return [];
        }

        var config = File.Exists("/etc/ssh/sshd_config") ? await File.ReadAllTextAsync("/etc/ssh/sshd_config", ct) : "";
        var includes = ComplianceParsers.SshConfigIncludesDropIns(config);
        var managed = File.Exists(SshDropInPath) ? ComplianceParsers.SshDropInIds(await File.ReadAllTextAsync(SshDropInPath, ct)) : [];
        return ComplianceParsers.EvaluateSsh(ComplianceParsers.ParseSshdEffective(effective.StandardOutput), includes, includes ? null : "no-include")
            .Select(s => managed.Contains(s.Id) ? s with { Note = "managed" } : s)
            .ToList();
    }

    private static async Task<List<LocalUser>> UsersAsync(CancellationToken ct)
    {
        var passwd = await ReadAsync("/etc/passwd", ct);
        var keys = new Dictionary<string, IReadOnlyList<SshKeyInfo>>();
        foreach (var entry in ComplianceParsers.ParsePasswd(passwd).Where(p => p.Uid == 0 || p.Uid >= 1000))
        {
            var file = Path.Combine(entry.Home, ".ssh", "authorized_keys");
            if (File.Exists(file))
            {
                try
                {
                    keys[entry.Name] = ComplianceParsers.ParseAuthorizedKeys(await File.ReadAllTextAsync(file, ct)).Take(100).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var lastlog = await TryRunAsync(Which("/usr/bin/lastlog", "/bin/lastlog"), [], ct);
        return ComplianceParsers.BuildLinuxUsers(passwd, await ReadAsync("/etc/shadow", ct), await ReadAsync("/etc/group", ct), keys,
            lastlog is { Success: true } ? ComplianceParsers.ParseLastlog(lastlog.StandardOutput) : new Dictionary<string, DateTime?>(), DateTime.UtcNow);
    }

    private static async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    // --- Ändern ---------------------------------------------------------------------------------------

    public Task<JobExecutionResult> ApplyAsync(SystemChange change, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct) =>
        change.Kind switch
        {
            SystemChangeKind.FirewallApply when change.Firewall is { } config => ApplyFirewallAsync(config, serverReachable, log, ct),
            SystemChangeKind.FirewallRestoreOriginal => RestoreOriginalAsync(log, ct),
            SystemChangeKind.RemoteAccessFix => SshFixAsync(change.Settings ?? [], log, ct),
            SystemChangeKind.RemoteAccessSet => SshSetAsync(change.Values ?? new Dictionary<string, string>(), log, ct),
            SystemChangeKind.RemoteAccessRevert => SshRevertAsync(log, ct),
            SystemChangeKind.UserCreate => UserCreateAsync(change, log, ct),
            >= SystemChangeKind.UserLock and <= SystemChangeKind.UserRemoveSshKey => UserChangeAsync(change, log, ct),
            _ => Task.FromResult(Rejected("Change is incomplete.")),
        };

    private static JobExecutionResult Rejected(string message) => new(JobOutcome.Rejected, message);

    private async Task<JobExecutionResult> ApplyFirewallAsync(FirewallConfig config, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct)
    {
        if (FirewallRules.Validate(config) is { Count: > 0 } errors)
        {
            return Rejected("Invalid firewall configuration: " + string.Join(" ", errors));
        }

        if (IsProxmox)
        {
            return Rejected("Proxmox VE: use the Proxmox firewall (Datacenter / Node / VM). ufw would conflict with pve-firewall.");
        }

        if (Ufw is not { } ufw)
        {
            return Rejected("ufw is not installed (apt install ufw).");
        }

        if (await TryRunAsync(Which("/usr/bin/firewall-cmd", "/bin/firewall-cmd"), ["--state"], ct) is { Success: true } fwd && fwd.StandardOutput.Contains("running"))
        {
            return Rejected("firewalld is active. Two firewalls at once would conflict: disable firewalld first.");
        }

        var backup = store.NewFirewallBackupDir();
        var restore = BackupUfw(backup);
        if (!store.HasOriginal)
        {
            BackupUfw(store.FirewallOriginal);
            log("[agent] Saved the original firewall state (restorable from the dashboard).");
        }

        await File.WriteAllTextAsync(store.FirewallPending, backup, ct);
        if (!RevertWatchdog.ScheduleLinux(store.FirewallPending, restore, log))
        {
            log("[agent] WARNING: no external safety net. The agent still checks the connection itself.");
        }

        foreach (var args in ComplianceParsers.UfwCommands(config))
        {
            log("[agent] ufw " + string.Join(' ', args));
            var result = await ProcessRunner.RunAsync(ufw, args, Short, ct);
            if (!result.Success)
            {
                log($"[agent] ERROR: {result.StandardError.Trim()} {result.StandardOutput.Trim()}");
                await RestoreAsync(restore, log, ct);
                File.Delete(store.FirewallPending);
                return new JobExecutionResult(JobOutcome.Failed, $"ufw {string.Join(' ', args)} failed: {result.StandardError.Trim()}. Previous rules restored.");
            }
        }

        if (!await RevertWatchdog.VerifyAsync(serverReachable, log, ct))
        {
            await RestoreAsync(restore, log, ct);
            File.Delete(store.FirewallPending);
            return new JobExecutionResult(JobOutcome.Failed, "Server was not reachable after applying the rules. Previous rules restored.");
        }

        File.Delete(store.FirewallPending);
        await File.WriteAllTextAsync(store.FirewallAppliedHash, FirewallRules.Hash(config), ct);
        return new JobExecutionResult(JobOutcome.Succeeded,
            $"Firewall applied: {config.Rules.Count} rule(s), incoming {config.IncomingDefault}, ufw {(config.Enabled ? "active" : "inactive")}.");
    }

    /// <summary>Sichert die ufw-Dateien und schreibt daneben ein restore.sh. Rückgabe: Pfad des Skripts.</summary>
    private static string BackupUfw(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "user.rules", "user6.rules", "ufw.conf" })
        {
            var source = Path.Combine("/etc/ufw", name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(dir, name), overwrite: true);
            }
        }

        if (File.Exists("/etc/default/ufw"))
        {
            File.Copy("/etc/default/ufw", Path.Combine(dir, "default-ufw"), overwrite: true);
        }

        var quoted = "'" + dir.Replace("'", "'\\''") + "'";
        var script = Path.Combine(dir, "restore.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "# Restores the ufw state saved by AIOPatcher.\n" +
            $"D={quoted}\n" +
            "for f in user.rules user6.rules ufw.conf; do [ -f \"$D/$f\" ] && cp -f \"$D/$f\" \"/etc/ufw/$f\"; done\n" +
            "[ -f \"$D/default-ufw\" ] && cp -f \"$D/default-ufw\" /etc/default/ufw\n" +
            "if grep -q '^ENABLED=yes' /etc/ufw/ufw.conf; then ufw disable >/dev/null 2>&1; ufw --force enable; else ufw disable; fi\n");
        return script;
    }

    private static async Task RestoreAsync(string script, Action<string> log, CancellationToken ct)
    {
        log("[agent] Restoring previous firewall state ...");
        var result = await ProcessRunner.RunAsync("/bin/sh", [script], Short, ct);
        log(result.Success ? "[agent] Previous firewall state restored." : $"[agent] ERROR while restoring: {result.StandardError.Trim()}");
    }

    private async Task<JobExecutionResult> RestoreOriginalAsync(Action<string> log, CancellationToken ct)
    {
        if (!store.HasOriginal)
        {
            return Rejected("No original state saved: AIOPatcher has not changed this firewall yet.");
        }

        await RestoreAsync(Path.Combine(store.FirewallOriginal, "restore.sh"), log, ct);
        File.Delete(store.FirewallAppliedHash);
        return new JobExecutionResult(JobOutcome.Succeeded, "Firewall restored to the state before AIOPatcher's first change.");
    }

    private Task<JobExecutionResult> SshFixAsync(IReadOnlyList<string> settings, Action<string> log, CancellationToken ct)
    {
        var chosen = ComplianceParsers.SshChecks.Where(c => settings.Contains(c.Id)).ToList();
        return chosen.Count == 0
            ? Task.FromResult(Rejected("No SSH recommendation selected (RDP settings only exist on Windows)."))
            : SshWriteAsync(chosen.ToDictionary(c => c.Key, c => c.Recommended), chosen.Select(c => c.Id).ToList(), log, ct);
    }

    /// <summary>Sollwerte aus einer Compliance-Vorgabe (Id → Wert); nur bekannte Schlüssel mit gültigem Wert.</summary>
    private Task<JobExecutionResult> SshSetAsync(IReadOnlyDictionary<string, string> values, Action<string> log, CancellationToken ct)
    {
        var ssh = values.Where(v => v.Key.StartsWith("ssh.", StringComparison.Ordinal)).ToList();
        var invalid = ssh.Where(v => !ComplianceParsers.SshSettable.TryGetValue(v.Key, out var s) || !s.Valid(v.Value)).Select(v => $"{v.Key}={v.Value}").ToList();
        if (invalid.Count > 0)
        {
            return Task.FromResult(Rejected($"Invalid SSH value(s): {string.Join(", ", invalid)}."));
        }

        return ssh.Count == 0
            ? Task.FromResult(Rejected("No SSH value given (RDP settings only exist on Windows)."))
            : SshWriteAsync(ssh.ToDictionary(v => ComplianceParsers.SshSettable[v.Key].Key, v => v.Value), ssh.Select(v => $"{v.Key}={v.Value}").ToList(), log, ct);
    }

    /// <summary>Werte in die Drop-in-Datei schreiben (bestehende behalten), mit "sshd -t" prüfen, sonst zurück.</summary>
    private async Task<JobExecutionResult> SshWriteAsync(Dictionary<string, string> keyValues, List<string> ids, Action<string> log, CancellationToken ct)
    {
        if (Sshd is not { } sshd)
        {
            return Rejected("OpenSSH server is not installed.");
        }

        if (!ComplianceParsers.SshConfigIncludesDropIns(await ReadAsync("/etc/ssh/sshd_config", ct)))
        {
            return Rejected("/etc/ssh/sshd_config has no 'Include /etc/ssh/sshd_config.d/*.conf': adjust it by hand.");
        }

        var passwordOff = keyValues.Any(kv => kv.Key is "PasswordAuthentication" or "KbdInteractiveAuthentication" && kv.Value == "no");
        if (passwordOff)
        {
            var users = await UsersAsync(ct);
            if (!users.Any(u => u.IsAdmin && u.Enabled && u.SshKeys > 0))
            {
                return Rejected("No administrator has an SSH key: turning off password login would lock everyone out. Add a key first.");
            }
        }

        var previous = File.Exists(SshDropInPath) ? await File.ReadAllTextAsync(SshDropInPath, ct) : null;
        var all = previous is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ComplianceParsers.SshDropInValues(previous);
        foreach (var (key, value) in keyValues)
        {
            all[key] = value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SshDropInPath)!);
        await File.WriteAllTextAsync(SshDropInPath, ComplianceParsers.SshDropIn(all), ct);
        log($"[agent] Wrote {SshDropInPath}: {string.Join(", ", all.Select(kv => $"{kv.Key} {kv.Value}"))}");

        var test = await ProcessRunner.RunAsync(sshd, ["-t"], Short, ct);
        if (!test.Success)
        {
            if (previous is null)
            {
                File.Delete(SshDropInPath);
            }
            else
            {
                await File.WriteAllTextAsync(SshDropInPath, previous, ct);
            }

            return new JobExecutionResult(JobOutcome.Failed, $"sshd -t rejected the configuration, nothing changed: {test.StandardError.Trim()}");
        }

        return new JobExecutionResult(JobOutcome.Succeeded, $"SSH: {string.Join(", ", ids)} applied. {await ReloadSshAsync(log, ct)}");
    }

    private static async Task<JobExecutionResult> SshRevertAsync(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(SshDropInPath))
        {
            return new JobExecutionResult(JobOutcome.Succeeded, "No SSH settings from AIOPatcher present.");
        }

        File.Delete(SshDropInPath);
        log($"[agent] Removed {SshDropInPath}.");
        return new JobExecutionResult(JobOutcome.Succeeded, $"SSH settings from AIOPatcher removed. {await ReloadSshAsync(log, ct)}");
    }

    private static async Task<string> ReloadSshAsync(Action<string> log, CancellationToken ct)
    {
        foreach (var unit in new[] { "ssh", "sshd" })
        {
            if (await TryRunAsync(Which("/usr/bin/systemctl", "/bin/systemctl"), ["reload", unit], ct) is { Success: true })
            {
                log($"[agent] systemctl reload {unit}");
                return "sshd reloaded, open sessions stay connected.";
            }
        }

        return "sshd could not be reloaded (not running?); the settings apply from its next start.";
    }

    private async Task<JobExecutionResult> UserChangeAsync(SystemChange change, Action<string> log, CancellationToken ct)
    {
        var name = change.UserName;
        if (!ComplianceParsers.IsValidUserName(name, windows: false))
        {
            return Rejected("Invalid user name.");
        }

        var users = await UsersAsync(ct);
        var user = users.FirstOrDefault(u => u.Name == name);
        if (user is null)
        {
            return Rejected($"User {name} not found (only root and users with UID >= 1000 can be managed).");
        }

        var groups = ComplianceParsers.ParseGroups(await ReadAsync("/etc/group", ct));
        switch (change.Kind)
        {
            case SystemChangeKind.UserLock:
                if (user.IsAdmin && !ComplianceParsers.HasOtherUsableAdmin(users, user.Name))
                {
                    return Rejected($"{name} is the last administrator who can log in. Locking would lock you out.");
                }

                // root nicht ablaufen lassen (Cronjobs, su), nur das Passwort sperren.
                return user.IsBuiltIn
                    ? await RunAsync(log, "passwd", ["-l", user.Name], $"Password login for {name} locked (SSH keys still work).")
                    : await RunAsync(log, "usermod", ["-L", "-e", "1", user.Name], $"{name} locked (password and SSH key).");

            case SystemChangeKind.UserUnlock:
                if (user.IsBuiltIn)
                {
                    return await RunAsync(log, "passwd", ["-u", user.Name], $"Password login for {name} unlocked.");
                }

                var unexpire = await RunAsync(log, "usermod", ["-e", "", user.Name], $"{name} unlocked.");
                if (unexpire.Outcome == JobOutcome.Succeeded && user.PasswordState == "locked")
                {
                    await RunAsync(log, "usermod", ["-U", user.Name], "");
                }

                return unexpire;

            case SystemChangeKind.UserGrantAdmin:
                var group = ComplianceParsers.AdminGroups.FirstOrDefault(groups.ContainsKey);
                return group is null
                    ? Rejected("Neither group sudo nor wheel exists (sudo installed?).")
                    : await RunAsync(log, "usermod", ["-aG", group, user.Name], $"{name} added to group {group}.");

            case SystemChangeKind.UserRevokeAdmin:
                if (user.IsBuiltIn)
                {
                    return Rejected("root is always administrator. Lock its password instead.");
                }

                if (!ComplianceParsers.HasOtherUsableAdmin(users, user.Name))
                {
                    return Rejected($"{name} is the last administrator who can log in.");
                }

                var memberOf = ComplianceParsers.AdminGroups.Where(g => groups.TryGetValue(g, out var e) && e.Members.Contains(user.Name)).ToList();
                foreach (var g in memberOf)
                {
                    var r = await RunAsync(log, "gpasswd", ["-d", user.Name, g], "");
                    if (r.Outcome != JobOutcome.Succeeded)
                    {
                        return r;
                    }
                }

                return new JobExecutionResult(JobOutcome.Succeeded, memberOf.Count > 0
                    ? $"{name} removed from {string.Join(", ", memberOf)}."
                    : $"{name} has administrator rights through its primary group; change that by hand.");

            case SystemChangeKind.UserSetPassword:
                if (!ComplianceParsers.IsValidPassword(change.Secret))
                {
                    return Rejected("Password must be 12 to 128 characters without line breaks.");
                }

                log($"[agent] Setting password for {name} (value is not logged).");
                var chpasswd = await ProcessRunner.RunAsync(Which("/usr/sbin/chpasswd", "/sbin/chpasswd") ?? "chpasswd", [], Short, ct, $"{user.Name}:{change.Secret}\n");
                return chpasswd.Success
                    ? new JobExecutionResult(JobOutcome.Succeeded, $"Password for {name} changed.")
                    : new JobExecutionResult(JobOutcome.Failed, $"chpasswd failed: {chpasswd.StandardError.Trim()}");

            case SystemChangeKind.UserAddSshKey:
            case SystemChangeKind.UserRemoveSshKey:
                return await SshKeyAsync(change, user.Name, log, ct);

            default:
                return Rejected($"Unknown change {change.Kind}.");
        }
    }

    /// <summary>Benutzer anlegen (Home, bash, kein Passwort: Anmeldung per Schlüssel), optional in sudo/wheel.</summary>
    private async Task<JobExecutionResult> UserCreateAsync(SystemChange change, Action<string> log, CancellationToken ct)
    {
        var name = change.UserName;
        if (!ComplianceParsers.IsValidUserName(name, windows: false))
        {
            return Rejected("Invalid user name.");
        }

        if (ComplianceParsers.ParsePasswd(await ReadAsync("/etc/passwd", ct)).Any(p => p.Name == name))
        {
            return new JobExecutionResult(JobOutcome.Succeeded, $"User {name} already exists.");
        }

        var shell = Which("/bin/bash", "/usr/bin/bash") ?? "/bin/sh";
        var created = await RunAsync(log, "useradd", ["-m", "-s", shell, name!], $"User {name} created (no password; log in with an SSH key).");
        if (created.Outcome != JobOutcome.Succeeded || !change.Admin)
        {
            return created;
        }

        var group = ComplianceParsers.AdminGroups.FirstOrDefault(ComplianceParsers.ParseGroups(await ReadAsync("/etc/group", ct)).ContainsKey);
        return group is null
            ? new JobExecutionResult(JobOutcome.Failed, $"User {name} created, but neither group sudo nor wheel exists (sudo installed?).")
            : await RunAsync(log, "usermod", ["-aG", group, name!], $"User {name} created and added to group {group}.");
    }

    private static async Task<JobExecutionResult> SshKeyAsync(SystemChange change, string name, Action<string> log, CancellationToken ct)
    {
        // Entfernen geht auch per Fingerabdruck (Compliance-Vorgabe kennt fremde Schlüssel nur so)
        var byFingerprint = change.Kind == SystemChangeKind.UserRemoveSshKey && ComplianceParsers.IsFingerprint(change.SshKey);
        if (!byFingerprint && !ComplianceParsers.IsValidPublicKey(change.SshKey))
        {
            return Rejected("Not a valid public SSH key (one line, e.g. 'ssh-ed25519 AAAA... comment', no options).");
        }

        var entry = ComplianceParsers.ParsePasswd(await ReadAsync("/etc/passwd", ct)).First(p => p.Name == name);
        var sshDir = Path.Combine(entry.Home, ".ssh");
        var file = Path.Combine(sshDir, "authorized_keys");

        // Kein Schreiben durch Symlinks: sonst könnte ein Benutzer root dazu bringen, beliebige Dateien zu ändern.
        if (new DirectoryInfo(entry.Home).LinkTarget is not null || new DirectoryInfo(sshDir).LinkTarget is not null || new FileInfo(file).LinkTarget is not null)
        {
            return Rejected($"{sshDir} or authorized_keys is a symbolic link; refusing to write through it.");
        }

        if (!Directory.Exists(entry.Home))
        {
            return Rejected($"Home directory {entry.Home} does not exist.");
        }

        var key = change.SshKey!.Trim();
        var lines = File.Exists(file) ? (await File.ReadAllTextAsync(file, ct)).Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToList() : [];
        var fingerprint = byFingerprint ? key : ComplianceParsers.KeyOf(key)?.Fingerprint;
        bool Same(string line) => ComplianceParsers.KeyOf(line)?.Fingerprint is { } f && f == fingerprint;
        if (change.Kind == SystemChangeKind.UserAddSshKey)
        {
            if (lines.Any(Same))
            {
                return new JobExecutionResult(JobOutcome.Succeeded, $"Key already present for {name}.");
            }

            lines.Add(key);
        }
        else
        {
            var before = lines.Count;
            lines.RemoveAll(Same);
            if (lines.Count == before)
            {
                return new JobExecutionResult(JobOutcome.Succeeded, $"Key was not present for {name}.");
            }
        }

        Directory.CreateDirectory(sshDir);
        await File.WriteAllTextAsync(file, string.Join('\n', lines) + "\n", ct);
        File.SetUnixFileMode(sshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await RunAsync(log, "chown", ["-h", $"{entry.Uid}:{entry.Gid}", sshDir, file], "");
        var comment = byFingerprint ? key : key.Split(' ').Length > 2 ? key.Split(' ', 3)[2] : "";
        return new JobExecutionResult(JobOutcome.Succeeded,
            change.Kind == SystemChangeKind.UserAddSshKey ? $"SSH key {comment} added for {name}." : $"SSH key {comment} removed for {name}.");
    }

    private static async Task<JobExecutionResult> RunAsync(Action<string> log, string tool, string[] args, string success)
    {
        var path = Which($"/usr/sbin/{tool}", $"/sbin/{tool}", $"/usr/bin/{tool}", $"/bin/{tool}") ?? tool;
        log($"[agent] {tool} {string.Join(' ', args)}");
        var result = await ProcessRunner.RunAsync(path, args, Short, CancellationToken.None);
        return result.Success
            ? new JobExecutionResult(JobOutcome.Succeeded, success)
            : new JobExecutionResult(JobOutcome.Failed, $"{tool} failed: {result.StandardError.Trim()}");
    }
}
