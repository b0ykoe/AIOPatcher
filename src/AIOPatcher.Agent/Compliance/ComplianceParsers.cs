using System.Globalization;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Compliance;

/// <summary>Parser und Prüfungen ohne Systemzugriff (testbar auf jeder Plattform).</summary>
public static partial class ComplianceParsers
{
    public const string ManagedTag = "aiopatcher";

    // --- ufw ------------------------------------------------------------------------------------------

    public sealed record UfwStatus(bool Active, string? Incoming, string? Outgoing, List<FirewallRuleInfo> Rules);

    /// <summary>Ausgabe von "ufw status verbose".</summary>
    public static UfwStatus ParseUfwStatus(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var active = lines.Any(l => l.Trim().Equals("Status: active", StringComparison.OrdinalIgnoreCase));
        string? incoming = null, outgoing = null;
        var defaults = lines.FirstOrDefault(l => l.StartsWith("Default:", StringComparison.Ordinal));
        if (defaults is not null)
        {
            incoming = DefaultMatch().Match(defaults) is { Success: true } m1 ? m1.Groups[1].Value : null;
            outgoing = DefaultOutMatch().Match(defaults) is { Success: true } m2 ? m2.Groups[1].Value : null;
        }

        var rules = new List<FirewallRuleInfo>();
        var inTable = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.Length == 0)
            {
                continue;
            }

            var managed = line.Contains("# " + ManagedTag, StringComparison.OrdinalIgnoreCase);
            rules.Add(new FirewallRuleInfo(Whitespace().Replace(line, " ").Trim(), managed));
        }

        return new UfwStatus(active, incoming, outgoing, rules);
    }

    [GeneratedRegex(@"(\w+) \(incoming\)")]
    private static partial Regex DefaultMatch();

    [GeneratedRegex(@"(\w+) \(outgoing\)")]
    private static partial Regex DefaultOutMatch();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>ufw-Aufrufe für eine (vorher geprüfte!) Konfiguration, ohne "ufw" selbst. Reihenfolge = Regelreihenfolge.</summary>
    public static List<string[]> UfwCommands(FirewallConfig config)
    {
        var commands = new List<string[]>
        {
            new[] { "--force", "reset" },
            new[] { "default", config.IncomingDefault == FirewallAction.Allow ? "allow" : "deny", "incoming" },
            new[] { "default", config.OutgoingDefault == FirewallAction.Allow ? "allow" : "deny", "outgoing" },
        };

        foreach (var rule in config.Rules)
        {
            var args = new List<string> { rule.Action == FirewallAction.Allow ? "allow" : "deny" };
            if (rule.Protocol != FirewallProtocol.Any)
            {
                args.AddRange(["proto", rule.Protocol == FirewallProtocol.Tcp ? "tcp" : "udp"]);
            }

            args.AddRange(["from", rule.Source is { Length: > 0 } s ? FirewallRules.NormalizeSource(s)! : "any", "to", "any"]);
            if (rule.Ports is { Length: > 0 } ports)
            {
                args.AddRange(["port", ports.Replace('-', ':')]);
            }

            args.AddRange(["comment", string.IsNullOrWhiteSpace(rule.Comment) ? ManagedTag : $"{ManagedTag}: {rule.Comment}"]);
            commands.Add(args.ToArray());
        }

        commands.Add(config.Enabled ? ["--force", "enable"] : ["disable"]);
        return commands;
    }

    // --- Windows-Firewall ----------------------------------------------------------------------------

    public const string WindowsRuleGroup = "AIOPatcher";

    /// <summary>PowerShell für eine (vorher geprüfte!) Konfiguration. disabledFile: Liste der von uns abgeschalteten Regeln.</summary>
    public static string WindowsFirewallScript(FirewallConfig config, string disabledFile)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            // Früher von uns abgeschaltete Regeln wieder an (ein nicht-exklusiver Stand soll sie nicht verschlucken).
            $"if (Test-Path -LiteralPath {Q(disabledFile)}) {{ Get-Content -LiteralPath {Q(disabledFile)} | Where-Object {{ $_ }} | ForEach-Object {{ Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue | Enable-NetFirewallRule }}; Remove-Item -LiteralPath {Q(disabledFile)} }}",
            $"Get-NetFirewallRule -Group {Q(WindowsRuleGroup)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
        };

        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var name = $"AIOPatcher {i + 1:000}" + (string.IsNullOrWhiteSpace(rule.Comment) ? "" : ": " + rule.Comment);
            var cmd = $"New-NetFirewallRule -Group {Q(WindowsRuleGroup)} -DisplayName {Q(name)} -Direction Inbound -Profile Any -Action {(rule.Action == FirewallAction.Allow ? "Allow" : "Block")}";
            if (rule.Protocol != FirewallProtocol.Any)
            {
                cmd += $" -Protocol {(rule.Protocol == FirewallProtocol.Tcp ? "TCP" : "UDP")}";
            }

            if (rule.Ports is { Length: > 0 } ports)
            {
                cmd += " -LocalPort " + string.Join(",", ports.Split(',').Select(Q));
            }

            if (rule.Source is { Length: > 0 } source)
            {
                cmd += " -RemoteAddress " + Q(FirewallRules.NormalizeSource(source)!);
            }

            lines.Add(cmd + " | Out-Null");
        }

        if (config.Exclusive)
        {
            lines.Add($"$others = @(Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object {{ $_.Group -ne {Q(WindowsRuleGroup)} }})");
            lines.Add($"$others | ForEach-Object {{ $_.Name }} | Set-Content -LiteralPath {Q(disabledFile)} -Encoding UTF8");
            lines.Add("$others | Disable-NetFirewallRule");
        }

        lines.Add($"Set-NetFirewallProfile -All -Enabled {(config.Enabled ? "True" : "False")} " +
                  $"-DefaultInboundAction {(config.IncomingDefault == FirewallAction.Allow ? "Allow" : "Block")} " +
                  $"-DefaultOutboundAction {(config.OutgoingDefault == FirewallAction.Allow ? "Allow" : "Block")}");
        return string.Join("\n", lines);
    }

    // --- SSH ------------------------------------------------------------------------------------------

    /// <summary>Feste Empfehlungen: Id → (sshd-Schlüssel, empfohlener Wert, gilt als ok).</summary>
    public static readonly IReadOnlyList<(string Id, string Key, string Recommended, Func<string?, bool> Ok)> SshChecks =
    [
        ("ssh.permitRootLogin", "PermitRootLogin", "prohibit-password", v => v is not null and not "yes"),
        ("ssh.passwordAuthentication", "PasswordAuthentication", "no", v => v == "no"),
        ("ssh.kbdInteractiveAuthentication", "KbdInteractiveAuthentication", "no", v => v == "no"),
        ("ssh.permitEmptyPasswords", "PermitEmptyPasswords", "no", v => v == "no"),
        ("ssh.maxAuthTries", "MaxAuthTries", "3", v => int.TryParse(v, out var n) && n <= 4),
        ("ssh.x11Forwarding", "X11Forwarding", "no", v => v == "no"),
    ];

    /// <summary>Ausgabe von "sshd -T" (Kleinbuchstaben, "schlüssel wert").</summary>
    public static Dictionary<string, string> ParseSshdEffective(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var space = line.IndexOf(' ');
            if (space > 0)
            {
                values.TryAdd(line[..space].Trim(), line[(space + 1)..].Trim());
            }
        }

        return values;
    }

    public static List<RemoteAccessSetting> EvaluateSsh(Dictionary<string, string> effective, bool fixable, string? note)
    {
        // Ältere OpenSSH-Versionen heißen ChallengeResponseAuthentication statt KbdInteractiveAuthentication.
        if (!effective.ContainsKey("kbdinteractiveauthentication") && effective.TryGetValue("challengeresponseauthentication", out var cr))
        {
            effective["kbdinteractiveauthentication"] = cr;
        }

        return SshChecks.Select(c =>
        {
            effective.TryGetValue(c.Key, out var value);
            return new RemoteAccessSetting(c.Id, value, c.Ok(value), fixable, note);
        }).ToList();
    }

    /// <summary>Inhalt der Drop-in-Datei mit den angewendeten Empfehlungen (bestehende plus neue).</summary>
    public static string SshDropIn(IEnumerable<string> ids)
    {
        var chosen = SshChecks.Where(c => ids.Contains(c.Id)).ToList();
        return "# Managed by AIOPatcher (Dashboard: Host -> Compliance). Remove this file to undo.\n"
               + string.Concat(chosen.Select(c => $"{c.Key} {c.Recommended}\n"));
    }

    /// <summary>Ids, die in einer vorhandenen Drop-in-Datei schon gesetzt sind.</summary>
    public static List<string> SshDropInIds(string content) =>
        SshChecks.Where(c => content.Replace("\r", "").Split('\n').Any(l => l.StartsWith(c.Key + " ", StringComparison.OrdinalIgnoreCase))).Select(c => c.Id).ToList();

    [GeneratedRegex(@"^\s*Include\s+.*sshd_config\.d/\*\.conf", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SshIncludeLine();

    public static bool SshConfigIncludesDropIns(string sshdConfig) => SshIncludeLine().IsMatch(sshdConfig);

    // --- Linux-Benutzer -------------------------------------------------------------------------------

    public sealed record PasswdEntry(string Name, int Uid, int Gid, string Gecos, string Home, string Shell);

    public static List<PasswdEntry> ParsePasswd(string content) =>
        content.Replace("\r", "").Split('\n')
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 7 && int.TryParse(f[2], out _) && int.TryParse(f[3], out _))
            .Select(f => new PasswdEntry(f[0], int.Parse(f[2]), int.Parse(f[3]), f[4], f[5], f[6]))
            .ToList();

    /// <summary>Name → (Passwortfeld, Ablauftag seit 1970 oder null).</summary>
    public static Dictionary<string, (string Password, long? ExpireDay)> ParseShadow(string content) =>
        content.Replace("\r", "").Split('\n')
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 2 && f[0].Length > 0)
            .GroupBy(f => f[0])
            .ToDictionary(g => g.Key, g =>
            {
                var f = g.First();
                return (f[1], f.Length >= 8 && long.TryParse(f[7], out var e) ? e : (long?)null);
            });

    /// <summary>Gruppe → (GID, Mitglieder).</summary>
    public static Dictionary<string, (int Gid, string[] Members)> ParseGroups(string content) =>
        content.Replace("\r", "").Split('\n')
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 4 && int.TryParse(f[2], out _))
            .GroupBy(f => f[0])
            .ToDictionary(g => g.Key, g => (int.Parse(g.First()[2]), g.First()[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    public static readonly string[] AdminGroups = ["sudo", "wheel", "admin"];

    private static readonly string[] NoLoginShells = ["/usr/sbin/nologin", "/sbin/nologin", "/bin/false", "/usr/bin/false", "/bin/sync"];

    /// <summary>"set", "empty", "locked" oder "none" aus dem Passwortfeld von /etc/shadow.</summary>
    public static string PasswordState(string? field) => field switch
    {
        null => "none",
        "" => "empty",
        "!" or "!!" or "*" or "!*" or "x" => "none",
        _ when field.StartsWith('!') => "locked",
        _ => "set",
    };

    /// <summary>Menschen und root: UID 0 und UIDs ab 1000 (ohne nobody). Dienstkonten bleiben außen vor.</summary>
    public static List<LocalUser> BuildLinuxUsers(string passwd, string shadow, string group, IReadOnlyDictionary<string, int> sshKeys,
        IReadOnlyDictionary<string, DateTime?> lastLogons, DateTime today)
    {
        var shadowEntries = ParseShadow(shadow);
        var groups = ParseGroups(group);
        var adminGids = AdminGroups.Where(groups.ContainsKey).Select(g => groups[g].Gid).ToHashSet();
        var adminMembers = AdminGroups.Where(groups.ContainsKey).SelectMany(g => groups[g].Members).ToHashSet();
        var todayDay = (long)(today.Date - DateTime.UnixEpoch).TotalDays;

        return ParsePasswd(passwd)
            .Where(p => p.Uid == 0 || (p.Uid >= 1000 && p.Uid < 65534))
            .Select(p =>
            {
                shadowEntries.TryGetValue(p.Name, out var s);
                var expired = s.ExpireDay is { } day && day <= todayDay;
                var loginShell = !NoLoginShells.Contains(p.Shell);
                return new LocalUser(
                    p.Name,
                    Enabled: !expired && loginShell,
                    IsAdmin: p.Uid == 0 || adminMembers.Contains(p.Name) || adminGids.Contains(p.Gid),
                    IsBuiltIn: p.Uid == 0,
                    PasswordState: PasswordState(s.Password),
                    LastLogonUtc: lastLogons.GetValueOrDefault(p.Name),
                    SshKeys: sshKeys.GetValueOrDefault(p.Name),
                    FullName: p.Gecos.Split(',')[0] is { Length: > 0 } full ? full : null);
            })
            .OrderBy(u => u.IsBuiltIn ? 0 : 1).ThenBy(u => u.Name)
            .ToList();
    }

    /// <summary>Ausgabe von "lastlog" (Debian/Ubuntu). Name → letzter Login (UTC) oder null.</summary>
    public static Dictionary<string, DateTime?> ParseLastlog(string output)
    {
        var result = new Dictionary<string, DateTime?>();
        foreach (var line in output.Replace("\r", "").Split('\n').Skip(1))
        {
            var parts = Whitespace().Split(line.Trim());
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            if (line.Contains("**Never logged in**", StringComparison.Ordinal))
            {
                result[parts[0]] = null;
                continue;
            }

            // ... Mon Nov 17 08:05:14 +0100 2025
            if (parts.Length >= 7 && DateTimeOffset.TryParseExact(string.Join(' ', parts[^6..]), "ddd MMM d HH:mm:ss zzz yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var when))
            {
                result[parts[0]] = when.UtcDateTime;
            }
        }

        return result;
    }

    public static int CountAuthorizedKeys(string content) =>
        content.Replace("\r", "").Split('\n').Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));

    // --- Eingaben vom Server ------------------------------------------------------------------------

    [GeneratedRegex(@"^[a-z_][a-z0-9_.-]{0,31}\$?$")]
    private static partial Regex LinuxUserName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$")]
    private static partial Regex WindowsUserName();

    public static bool IsValidUserName(string? name, bool windows) =>
        name is not null && (windows ? WindowsUserName() : LinuxUserName()).IsMatch(name);

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) [A-Za-z0-9+/]+={0,3}( [^\r\n]{0,200})?$")]
    private static partial Regex PublicKey();

    /// <summary>Genau eine Zeile authorized_keys ohne Optionen (keine command="...", kein from=...).</summary>
    public static bool IsValidPublicKey(string? key) => key is not null && PublicKey().IsMatch(key.Trim());

    /// <summary>Schlüsselteil (Base64) einer authorized_keys-Zeile, zum Vergleichen.</summary>
    public static string? KeyData(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts ? parts[1] : null;

    public static bool IsValidPassword(string? password) =>
        password is { Length: >= 12 and <= 128 } && !password.Any(c => c is '\n' or '\r' or '\0');

    /// <summary>
    /// Würde nach dieser Änderung noch ein Admin übrig bleiben, der sich anmelden kann? Schutz gegen Aussperren
    /// (root sperren, letztem sudo-Benutzer die Rechte nehmen).
    /// </summary>
    public static bool HasOtherUsableAdmin(IEnumerable<LocalUser> users, string exceptName) =>
        users.Any(u => !string.Equals(u.Name, exceptName, StringComparison.OrdinalIgnoreCase) && u.IsAdmin && u.Enabled && (u.PasswordState == "set" || u.SshKeys > 0));
}
