using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIOPatcher.Contracts;

// Compliance: Firewall, SSH/RDP und lokale Benutzer. Lesen meldet der Agent beim Check-in, Ändern sind Jobs
// vom Typ SystemChange. Der Agent kennt nur feste Aktionen und feste Einstellungen (keine freie Konfiguration,
// keine Shell-Befehle vom Server); alle Werte werden vor der Verwendung geprüft (siehe FirewallRules.Validate).

public sealed record ComplianceReport(
    FirewallStatus Firewall,
    IReadOnlyList<RemoteAccessSetting> RemoteAccess,
    IReadOnlyList<LocalUser> Users,
    DateTime CollectedAtUtc);

/// <param name="Product">"ufw", "firewalld", "nftables", "iptables", "windows" oder "none".</param>
/// <param name="Manageable">AIOPatcher kann diese Firewall verwalten (ufw, Windows).</param>
/// <param name="AppliedConfigHash">SHA-256 der zuletzt von AIOPatcher angewendeten Konfiguration, sonst null.</param>
/// <param name="HasOriginalBackup">Stand vor dem ersten Eingriff durch AIOPatcher ist gesichert (wiederherstellbar).</param>
public sealed record FirewallStatus(
    string Product,
    bool Enabled,
    bool Manageable,
    string? IncomingDefault,
    string? OutgoingDefault,
    IReadOnlyList<FirewallRuleInfo> Rules,
    int OtherRuleCount,
    string? AppliedConfigHash,
    bool HasOriginalBackup,
    string? Detail = null);

/// <param name="Text">Regel lesbar, z. B. "ALLOW IN 443/tcp from Anywhere".</param>
public sealed record FirewallRuleInfo(string Text, bool Managed);

/// <summary>
/// Eine geprüfte Einstellung. Id ist fest (z. B. "ssh.permitRootLogin"), das Dashboard übersetzt Name und Empfehlung.
/// Fixable: der Agent kann die Empfehlung auf diesem Host anwenden.
/// </summary>
public sealed record RemoteAccessSetting(string Id, string? Value, bool Compliant, bool Fixable, string? Note = null);

/// <param name="IsAdmin">sudo/wheel (Linux) bzw. Gruppe Administratoren (Windows).</param>
/// <param name="IsBuiltIn">root bzw. eingebauter Administrator (RID 500).</param>
/// <param name="PasswordState">"set", "empty", "locked", "none" (kein Passwort-Login) oder "unknown" (Windows: nicht feststellbar).</param>
public sealed record LocalUser(
    string Name,
    bool Enabled,
    bool IsAdmin,
    bool IsBuiltIn,
    string PasswordState,
    DateTime? LastLogonUtc,
    int SshKeys,
    string? FullName = null,
    IReadOnlyList<SshKeyInfo>? Keys = null);

/// <summary>Ein Schlüssel aus authorized_keys. Fingerprint wie "ssh-keygen -lf": "SHA256:" + Base64 ohne Auffüllung.</summary>
public sealed record SshKeyInfo(string Fingerprint, string Type, string? Comment);

[JsonConverter(typeof(JsonStringEnumConverter<SystemChangeKind>))]
public enum SystemChangeKind
{
    FirewallApply,

    /// <summary>Stand vor dem ersten Eingriff durch AIOPatcher wiederherstellen.</summary>
    FirewallRestoreOriginal,

    /// <summary>Empfehlungen anwenden (Settings = Ids aus <see cref="RemoteAccessSetting"/>).</summary>
    RemoteAccessFix,

    /// <summary>Von AIOPatcher gesetzte SSH/RDP-Einstellungen zurücknehmen.</summary>
    RemoteAccessRevert,

    UserLock,
    UserUnlock,
    UserGrantAdmin,
    UserRevokeAdmin,
    UserSetPassword,
    UserAddSshKey,
    UserRemoveSshKey,

    /// <summary>Eigene Update-Automatik des Systems abschalten (unattended-upgrades bzw. Windows: nur herunterladen).</summary>
    AutoUpdatesOff,

    /// <summary>Eigene Update-Automatik wieder einschalten (Linux: an; Windows: Standard).</summary>
    AutoUpdatesOn,

    /// <summary>Lokalen Benutzer anlegen (Admin = zusätzlich sudo/wheel bzw. Administratoren). Ohne Passwort.</summary>
    UserCreate,

    /// <summary>SSH/RDP-Einstellungen auf bestimmte Werte setzen (Values: Id → Wert), nicht nur die Empfehlung.</summary>
    RemoteAccessSet,

    /// <summary>Mehrere Änderungen nacheinander (Compliance-Vorgabe angleichen); bricht beim ersten Fehler ab.</summary>
    Batch,
}

/// <param name="Secret">Nur UserSetPassword. Liegt auf dem Server nur verschlüsselt bis zur Übergabe, wird nie geloggt.</param>
/// <param name="SshKey">Öffentlicher Schlüssel (eine Zeile authorized_keys) für UserAddSshKey/UserRemoveSshKey; zum Entfernen auch "SHA256:…".</param>
/// <param name="Admin">UserCreate: mit Administratorrechten.</param>
/// <param name="Values">RemoteAccessSet: Id → Sollwert, z. B. "ssh.permitRootLogin" → "no".</param>
/// <param name="Steps">Batch: die einzelnen Änderungen in Reihenfolge.</param>
public sealed record SystemChange(
    SystemChangeKind Kind,
    FirewallConfig? Firewall = null,
    IReadOnlyList<string>? Settings = null,
    string? UserName = null,
    string? Secret = null,
    string? SshKey = null,
    bool Admin = false,
    IReadOnlyDictionary<string, string>? Values = null,
    IReadOnlyList<SystemChange>? Steps = null);

[JsonConverter(typeof(JsonStringEnumConverter<FirewallAction>))]
public enum FirewallAction
{
    Allow,
    Deny,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirewallProtocol>))]
public enum FirewallProtocol
{
    Any,
    Tcp,
    Udp,
}

/// <summary>Eingehende Regel. Ports null = alle; Source null = überall (any).</summary>
public sealed record FirewallRule(FirewallAction Action, FirewallProtocol Protocol, string? Ports, string? Source, string? Comment);

/// <summary>
/// Vollständiger Firewall-Stand, den AIOPatcher setzt. ufw: wird komplett neu aufgebaut. Windows: Regelgruppe
/// "AIOPatcher"; Exclusive = alle anderen eingehenden Regeln abschalten (nicht löschen).
/// </summary>
public sealed record FirewallConfig(
    bool Enabled,
    FirewallAction IncomingDefault,
    FirewallAction OutgoingDefault,
    IReadOnlyList<FirewallRule> Rules,
    bool Exclusive = false);

public static partial class FirewallRules
{
    public const int MaxRules = 200;

    [GeneratedRegex(@"^\d{1,5}(-\d{1,5})?(,\d{1,5}(-\d{1,5})?)*$")]
    private static partial Regex PortsPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ._:/()-]{0,60}$")]
    private static partial Regex CommentPattern();

    /// <summary>
    /// Prüft eine Konfiguration streng. Server (beim Speichern) und Agent (vor dem Anwenden) rufen das auf; nur
    /// geprüfte Werte landen in ufw- bzw. PowerShell-Aufrufen. Rückgabe: Fehlermeldungen (englisch), leer = gültig.
    /// </summary>
    public static List<string> Validate(FirewallConfig config)
    {
        var errors = new List<string>();
        if (config.Rules is null)
        {
            errors.Add("Rules missing.");
            return errors;
        }

        if (config.Rules.Count > MaxRules)
        {
            errors.Add($"At most {MaxRules} rules.");
        }

        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var n = i + 1;
            if (!Enum.IsDefined(rule.Action) || !Enum.IsDefined(rule.Protocol))
            {
                errors.Add($"Rule {n}: invalid action or protocol.");
            }

            if (rule.Ports is { Length: > 0 } ports)
            {
                if (!PortsPattern().IsMatch(ports) || ports.Split(',').SelectMany(p => p.Split('-')).Any(p => int.Parse(p) is < 1 or > 65535))
                {
                    errors.Add($"Rule {n}: ports must look like 443, 8000-8100 or 80,443 (1-65535).");
                }
                else if (rule.Protocol == FirewallProtocol.Any)
                {
                    errors.Add($"Rule {n}: ports need protocol tcp or udp.");
                }
            }

            if (rule.Source is { Length: > 0 } source && NormalizeSource(source) is null)
            {
                errors.Add($"Rule {n}: source must be an IP address or network (e.g. 10.0.0.0/8), or empty for any.");
            }

            if (rule.Comment is not null && !CommentPattern().IsMatch(rule.Comment))
            {
                errors.Add($"Rule {n}: comment may only contain letters, digits, space and . _ : / ( ) - (max. 60).");
            }
        }

        return errors;
    }

    /// <summary>"10.0.0.0/8" bzw. "192.168.1.5" → normalisiert, sonst null.</summary>
    public static string? NormalizeSource(string source)
    {
        source = source.Trim();
        if (System.Net.IPNetwork.TryParse(source, out var network))
        {
            return network.ToString();
        }

        return IPAddress.TryParse(source, out var address) ? address.ToString() : null;
    }

    /// <summary>Würde diese Konfiguration SSH (22) bzw. RDP (3389) von überall bzw. allen Netzen sperren? Für Warnungen.</summary>
    public static bool AllowsPort(FirewallConfig config, int port) =>
        !config.Enabled
        || config.IncomingDefault == FirewallAction.Allow
        || config.Rules.Any(r => r.Action == FirewallAction.Allow && r.Protocol != FirewallProtocol.Udp && (r.Ports is null or "" || CoversPort(r.Ports, port)));

    private static bool CoversPort(string ports, int port) =>
        ports.Split(',').Any(part =>
        {
            var range = part.Split('-');
            return int.TryParse(range[0], out var from) && int.TryParse(range[^1], out var to) && port >= from && port <= to;
        });

    /// <summary>Kennung eines Stands (z. B. "welche Version ist auf dem Host aktiv?").</summary>
    public static string Hash(FirewallConfig config) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config, AgentJsonContext.Default.FirewallConfig)))[..16].ToLowerInvariant();
}
