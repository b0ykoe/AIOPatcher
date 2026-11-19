namespace AIOPatcher.Contracts;

// Monitoring-Clients (Wazuh-Agent, Checkmk-Agent): erkennen, installieren, konfigurieren, deinstallieren.
// AIOPatcher verwaltet nur den Client auf dem Host, nie den Wazuh-Manager oder die Checkmk-Site selbst.

public static class MonitoringProducts
{
    public const string Wazuh = "wazuh";
    public const string Checkmk = "checkmk";

    public static readonly string[] All = [Wazuh, Checkmk];
}

/// <summary>Erkannte Varianten eines Clients (bestimmen, wie umgestellt wird).</summary>
public static class MonitoringVariants
{
    /// <summary>Wazuh 3.0 bis 4.x: ossec.conf mit &lt;client&gt;&lt;server&gt;&lt;address&gt;.</summary>
    public const string Wazuh = "wazuh";

    /// <summary>Wazuh 5: &lt;agent&gt;&lt;manager&gt;&lt;endpoint&gt;.</summary>
    public const string Wazuh5 = "wazuh5";

    /// <summary>OSSEC bzw. Wazuh vor 3.0: &lt;server-ip&gt; / &lt;server-hostname&gt;.</summary>
    public const string Ossec = "ossec";

    /// <summary>Checkmk ab 2.1 mit Agent Controller (cmk-agent-ctl, TLS-Registrierung).</summary>
    public const string CheckmkController = "checkmk-ctl";

    /// <summary>Checkmk ohne Controller: Legacy-Pull über xinetd bzw. Windows-Agent mit check_mk.user.yml.</summary>
    public const string CheckmkLegacy = "checkmk-legacy";

    /// <summary>Alter Checkmk-1.x-Windows-Agent (check_mk.ini): nur anzeigen.</summary>
    public const string CheckmkIni = "checkmk-ini";
}

/// <summary>Antwort auf monitoring.status.</summary>
/// <param name="ChangesAllowed">policy.json erlaubt Installieren/Umstellen/Deinstallieren (allowMonitoringChanges).</param>
public sealed record MonitoringStatus(IReadOnlyList<MonitoringClient> Clients, bool ChangesAllowed = false);

/// <summary>Ein installierter Client.</summary>
/// <param name="Addresses">Eingetragene Ziele: Wazuh "manager[:port]", Checkmk-Controller "server[:port]/site", Legacy "only_from"-Einträge.</param>
/// <param name="Connected">true/false laut Client; null = lässt sich nicht feststellen (z. B. Legacy-Pull: die Site holt ab).</param>
/// <param name="CanConfigure">Kann der Agent diese Variante umstellen? Sonst steht in Detail, warum nicht.</param>
/// <param name="Held">Linux: Paket ist per apt-mark hold festgehalten (normales Patchen hebt die Version nicht).</param>
public sealed record MonitoringClient(
    string Product,
    string Variant,
    string? Version,
    bool ServiceRunning,
    IReadOnlyList<string> Addresses,
    bool? Connected,
    string? Detail = null,
    bool CanConfigure = true,
    bool Held = false,
    string? Hostname = null);

/// <summary>Zielvorgabe für MonitoringInstall/MonitoringConfigure (bzw. nur Product für MonitoringUninstall).</summary>
/// <param name="Address">Wazuh: Manager-Adresse.</param>
/// <param name="Port">Wazuh: Verbindungsport (Standard 1514).</param>
/// <param name="Protocol">Wazuh: "tcp" oder "udp".</param>
/// <param name="Enrollment">Wazuh: "keys" = Schlüssel wurden auf den neuen Manager übernommen (nur Adresse ändern);
/// "new" = beim neuen Manager neu anmelden (client.keys leeren, Auto-Enrollment bzw. agent-auth).</param>
/// <param name="EnrollmentPort">Wazuh: Port des Anmeldedienstes (Standard 1515).</param>
/// <param name="Groups">Wazuh: Agent-Gruppen, kommagetrennt.</param>
/// <param name="ManagerVersion">Wazuh: Version des Managers; neuere Clients werden abgelehnt.</param>
/// <param name="Server">Checkmk: Server der Site, optional mit Receiver-Port ("cmk.example.com:8000").</param>
/// <param name="Site">Checkmk: Name der Site.</param>
/// <param name="User">Checkmk: Benutzer für die Registrierung (z. B. agent_registration).</param>
/// <param name="Hostname">Checkmk: Name des Hosts in der Site (muss dort angelegt sein).</param>
/// <param name="OnlyFrom">Checkmk-Legacy: IPs/Netze, die den Agent abfragen dürfen (die der Site).</param>
/// <param name="SiteVersion">Checkmk: Version der Site (nur zur Warnung, wenn der Client neuer ist).</param>
/// <param name="Package">Install: zu installierendes Paket (Download über den Agent-Kanal, SHA-256 geprüft).</param>
public sealed record MonitoringSpec(
    string Product,
    string? Address = null,
    int? Port = null,
    string? Protocol = null,
    string? Enrollment = null,
    int? EnrollmentPort = null,
    string? Groups = null,
    string? ManagerVersion = null,
    string? Server = null,
    string? Site = null,
    string? User = null,
    string? Hostname = null,
    IReadOnlyList<string>? OnlyFrom = null,
    string? SiteVersion = null,
    MonitoringPackage? Package = null);

/// <param name="Id">Kennung beim Server (Download-Pfad <see cref="Path"/>).</param>
/// <param name="FileName">Originaler Dateiname (Endung bestimmt den Installer: .deb, .msi).</param>
public sealed record MonitoringPackage(string Id, string FileName, string Sha256, long Size, string? Version)
{
    public const string PathPrefix = "/agent/v2/monitoring-package/";
    public string Path => PathPrefix + Id;
}
