using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOPatcher.Contracts;

// Protokoll v2: Der Agent ist ein reiner Pull-Agent.
//
//   GET  /agent/v2/poll[?running=<taskId>]      → höchstens ein Auftrag
//   POST /agent/v2/tasks/{taskId}/log            → Log-Zeilen zu genau diesem Auftrag
//   POST /agent/v2/tasks/{taskId}/result         → Ergebnis zu genau diesem Auftrag
//
// Der Agent sendet nie ungefragt Daten: Inventar, Updates, Programme, Container, Compliance, Update-Berichte und
// Zertifikatserneuerung fordert der Server als Auftrag an, der Agent antwortet darauf. Der Abfrage selbst liegt
// nur die Agent-Version als Header bei (wie ein User-Agent), dazu eine zufällige Kennung des laufenden Prozesses:
// Ändert sie sich (Neustart von Agent oder Host), fragt der Server sofort neue Systemdaten an (z. B. Uptime).
// Dazu ein Fingerabdruck der lokalen policy.json: Weicht er vom zuletzt gemeldeten Stand ab (z. B. nach
// "aiopatcher-agent policy set"), fragt der Server ebenfalls sofort neue Systemdaten samt Policy an.

public static class PullProtocol
{
    public const int Version = 2;

    public const string PollPath = "/agent/v2/poll";
    public const string VersionHeader = "X-AIOPatcher-Agent-Version";
    public const string InstanceHeader = "X-AIOPatcher-Agent-Instance";
    public const string PolicyHeader = "X-AIOPatcher-Agent-Policy";

    /// <summary>
    /// Kurzer Fingerabdruck einer Policy (16 Hex-Zeichen aus SHA-256 des JSON). Agent und Server rechnen ihn gleich,
    /// der Server aus der gespeicherten Meldung. Nur zum Erkennen von Änderungen, keine Sicherheitsfunktion.
    /// </summary>
    public static string PolicyFingerprint(AgentPolicyReport report) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(report, PullJsonContext.Default.AgentPolicyReport)))[..16];
    public static string TaskLogPath(Guid taskId) => $"/agent/v2/tasks/{taskId}/log";
    public static string TaskResultPath(Guid taskId) => $"/agent/v2/tasks/{taskId}/result";
}

/// <summary>Module des Agents. Jedes Modul kennt nur seine festen Aktionen.</summary>
public static class AgentModules
{
    /// <summary>describe: Systemdaten, IPs, Module, Ergebnis des letzten Self-Updates.</summary>
    public const string System = "system";

    /// <summary>scan, install, reboot (apt bzw. Windows Update), change (eigene Update-Automatik des Systems an/aus).</summary>
    public const string Updates = "updates";

    /// <summary>inventory, appupdate (winget).</summary>
    public const string Software = "software";

    /// <summary>inventory (laufende Container).</summary>
    public const string Docker = "docker";

    /// <summary>list (laufende Dienste und Container, für den Vergleich vor/nach einem Neustart).</summary>
    public const string Services = "services";

    /// <summary>sample (CPU, RAM, Datenträger, Container-Auslastung).</summary>
    public const string Metrics = "metrics";

    /// <summary>status, change.</summary>
    public const string Firewall = "firewall";

    /// <summary>status, change (SSH/RDP).</summary>
    public const string RemoteAccess = "remoteaccess";

    /// <summary>list, change.</summary>
    public const string Users = "users";

    /// <summary>update (Self-Update), renew (Client-Zertifikat).</summary>
    public const string Agent = "agent";

    /// <summary>status, change (Wazuh-/Checkmk-Client installieren, konfigurieren, deinstallieren).</summary>
    public const string Monitoring = "monitoring";

    /// <summary>update: den AIOPatcher-Server auf diesem Host aktualisieren (nur wo das Setup aiopatcher-update eingerichtet hat).</summary>
    public const string ServerUpdate = "serverupdate";

    public static readonly string[] All = [System, Updates, Software, Docker, Services, Metrics, Firewall, RemoteAccess, Users, Agent, Monitoring, ServerUpdate];

    /// <summary>Ohne diese geht nichts: nicht abschaltbar.</summary>
    public static readonly string[] Required = [System, Agent];

    /// <summary>Modul für eine Systemänderung.</summary>
    public static string ForChange(SystemChangeKind kind) => kind switch
    {
        SystemChangeKind.FirewallApply or SystemChangeKind.FirewallRestoreOriginal => Firewall,
        SystemChangeKind.RemoteAccessFix or SystemChangeKind.RemoteAccessRevert or SystemChangeKind.RemoteAccessSet => RemoteAccess,
        SystemChangeKind.AutoUpdatesOff or SystemChangeKind.AutoUpdatesOn => Updates,
        SystemChangeKind.MonitoringInstall or SystemChangeKind.MonitoringConfigure or SystemChangeKind.MonitoringUninstall => Monitoring,
        _ => Users,
    };
}

public static class ModuleActions
{
    public const string Describe = "describe";
    public const string Scan = "scan";
    public const string Install = "install";
    public const string Reboot = "reboot";
    public const string Inventory = "inventory";
    public const string AppUpdate = "appupdate";

    /// <summary>docker.appupdatemany: mehrere Container in einem Auftrag, je Compose-Projekt gebündelt ("Alle aktualisieren").</summary>
    public const string AppUpdateMany = "appupdatemany";
    public const string Status = "status";
    public const string List = "list";
    public const string Change = "change";
    public const string Update = "update";
    public const string Renew = "renew";
    public const string Sample = "sample";
    public const string Changelog = "changelog";
    public const string Packages = "packages";

    /// <summary>docker.recipe: Update-Rezept der App ausführen (siehe <see cref="UpdateRecipeCatalog"/>).</summary>
    public const string Recipe = "recipe";

    /// <summary>docker.manage: Mini-Docker-Manager (siehe <see cref="ContainerOps"/>).</summary>
    public const string Manage = "manage";
}

/// <summary>docker.packages: Paketliste dieser Images lesen (je Image ein Container; docker cp, nur lesen).</summary>
public sealed record ImagePackagesRequest(IReadOnlyList<ImagePackagesTarget> Images);

public sealed record ImagePackagesTarget(string ContainerId, string ImageId);

public sealed record ImagePackagesResult(IReadOnlyList<ImagePackages> Images);

/// <param name="OsId">/etc/os-release ID, z. B. "debian", "ubuntu", "alpine".</param>
/// <param name="OsVersion">/etc/os-release VERSION_ID, z. B. "12", "22.04", "3.20.3".</param>
/// <param name="Format">"dpkg" oder "apk"; null, wenn keine Paketdatenbank gefunden wurde.</param>
/// <param name="Error">Warum es keine Liste gibt (z. B. distroless/scratch, Container weg).</param>
public sealed record ImagePackages(string ImageId, string? OsId, string? OsVersion, string? Format, IReadOnlyList<ImagePackage> Packages, string? Error = null);

/// <param name="Source">Quellpaket (dpkg "Source", apk "o:"), falls abweichend.</param>
/// <param name="SourceVersion">Version des Quellpakets, falls abweichend (dpkg "Source: name (version)").</param>
public sealed record ImagePackage(string Name, string Version, string? Source = null, string? SourceVersion = null);

/// <summary>updates.changelog: Changelogs dieser Pakete in der angebotenen Version holen (apt-get changelog).</summary>
public sealed record ChangelogRequest(IReadOnlyList<ChangelogPackage> Packages);

public sealed record ChangelogPackage(string Name, string Version);

/// <param name="Text">Anfang des Changelogs (neueste Einträge zuerst, gekürzt); null, wenn keiner zu haben war.</param>
/// <param name="Error">Warum es keinen gibt (z. B. Quelle bietet keine Changelogs an).</param>
public sealed record PackageChangelog(string Name, string Version, string? Text, string? Error);

public sealed record ChangelogResult(IReadOnlyList<PackageChangelog> Changelogs);

/// <param name="Task">Höchstens ein Auftrag pro Abfrage. Der Agent fragt nach Erledigung sofort wieder.</param>
/// <param name="UpdateKey">Öffentlicher Update-Schlüssel des Servers (siehe <see cref="CheckinResponse"/>).</param>
/// <param name="PublicUrl">Öffentliche Adresse für diesen Agent (über Reverse Proxy), falls eingerichtet; der Agent merkt sie sich als Ausweichweg.</param>
public sealed record PollResponse(int PollIntervalSeconds, AgentTask? Task, string? UpdateKey = null, string? PublicUrl = null);

/// <param name="Payload">Parameter der Aktion, z. B. <see cref="InstallRequest"/> oder <see cref="SystemChange"/>.</param>
public sealed record AgentTask(Guid TaskId, string Module, string Action, JsonElement? Payload = null);

/// <param name="Payload">Daten der Antwort, z. B. <see cref="SystemDescription"/> oder <see cref="UpdateScanResult"/>.</param>
public sealed record TaskResultRequest(JobOutcome Outcome, string? Summary, JsonElement? Payload = null, bool RebootPerformed = false);

/// <summary>Rückgabe des Servers auf ein Ergebnis, z. B. das neue Zertifikat bei agent.renew.</summary>
public sealed record TaskResultResponse(JsonElement? Payload = null);

/// <param name="DisabledLocally">In policy.json gesperrt (disabledModules).</param>
public sealed record ModuleInfo(string Id, bool Available, bool DisabledLocally, IReadOnlyList<string> Actions);

public sealed record SystemDescription(
    string AgentVersion,
    string Hostname,
    OsFamily OsFamily,
    string OsName,
    string OsVersion,
    string? KernelVersion,
    TimeSpan Uptime,
    bool RebootRequired,
    bool NativeAutoUpdatesEnabled,
    IReadOnlyList<NetworkAddress> Addresses,
    string Architecture,
    IReadOnlyList<ModuleInfo> Modules,
    AgentUpdateReport? UpdateReport = null,
    AgentPolicyReport? Policy = null);

/// <summary>
/// Lokale policy.json des Hosts, nur zur Anzeige gemeldet (der Server kann sie nicht ändern). null bei einem Wert = der
/// Agent meldet ihn nicht (Lite-Agents kennen nur einen Teil).
/// </summary>
public sealed record AgentPolicyReport(
    string AllowPatch,
    bool? AllowReboot = null,
    bool? AllowSelfUpdate = null,
    bool? AllowAppUpdates = null,
    bool? TrustServerUpdates = null,
    bool? AllowSystemChanges = null,
    bool? AllowSourceFixes = null,
    bool? AllowAutoUpdateChanges = null,
    bool? AllowMonitoringChanges = null,
    bool? AllowServerUpdate = null,
    bool? AllowUpdateRecipes = null,
    bool? AllowContainerManagement = null,
    bool? AllowComposeEdit = null,
    string? ComposeRoot = null,
    IReadOnlyList<string>? DisabledModules = null);

/// <param name="BrokenSources">Paketquellen, die beim letzten apt-get update nicht ladbar waren (null = Agent prüft das nicht).</param>
public sealed record UpdateScanResult(IReadOnlyList<AvailableUpdate> Updates, IReadOnlyList<BrokenAptSource>? BrokenSources = null);

/// <summary>Nicht ladbare apt-Paketquelle.</summary>
/// <param name="Source">"URI Suite", z. B. "https://deb.nodesource.com/node_18.x jammy".</param>
/// <param name="Permanent">404 bzw. "no longer has a Release file": wird nicht von selbst wieder gehen (anders als DNS/Timeout).</param>
/// <param name="File">Datei unter /etc/apt/sources.list.d, in der die Quelle steht (falls gefunden).</param>
/// <param name="Fixable">Darf automatisch deaktiviert werden: dauerhaft kaputt, keine Distributions-Quelle, Datei enthält nur kaputte Einträge.</param>
public sealed record BrokenAptSource(string Source, string Reason, bool Permanent, string? File = null, bool Fixable = false);

/// <param name="FixBrokenSources">Dauerhaft kaputte Fremdquellen vor dem Patchen deaktivieren (Schalter im Dashboard; policy.json "allowSourceFixes").</param>
public sealed record InstallRequest(PatchScope Scope, bool AllowReboot, IReadOnlyList<string>? Holds = null, bool FixBrokenSources = false);

public sealed record AppUpdateRequest(IReadOnlyList<string> Packages);

public sealed record SoftwareList(IReadOnlyList<InstalledSoftware> Software);

/// <param name="Disk">"docker system df" (Images, Container, Volumes, Build-Cache), für "Platz freigeben"; null = älterer Agent.</param>
public sealed record ContainerList(IReadOnlyList<ContainerReport> Containers, IReadOnlyList<DockerDiskUsage>? Disk = null);

/// <summary>
/// services.list: was gerade läuft und nach einem Neustart wieder laufen sollte. Der Server vergleicht den Stand vor dem
/// Neustart mit dem ein paar Minuten danach.
/// </summary>
/// <param name="Uptime">Zeit seit dem Systemstart (daran erkennt der Server einen Neustart).</param>
/// <param name="Services">Laufende Dienste, die beim Systemstart automatisch starten (systemd: enabled; Windows: Automatisch, ohne Verzögerung/Trigger).</param>
/// <param name="Containers">Laufende Docker-Container (null = kein Docker).</param>
/// <param name="StoppedServices">Solche Dienste, die gerade nicht laufen (systemd: failed; Windows: gestoppt). Nur als Hinweis.</param>
/// <param name="ContainersWithoutRestart">Laufende Container mit Restart-Policy "no": starten nach einem Neustart nicht von selbst.</param>
public sealed record RunningServices(
    TimeSpan Uptime,
    IReadOnlyList<string> Services,
    IReadOnlyList<string>? Containers = null,
    IReadOnlyList<string>? StoppedServices = null,
    IReadOnlyList<string>? ContainersWithoutRestart = null);

public sealed record RemoteAccessList(IReadOnlyList<RemoteAccessSetting> Settings);

public sealed record UserList(IReadOnlyList<LocalUser> Users);

/// <summary>metrics.sample: Auslastung jetzt (CPU als Mittel seit der letzten Messung).</summary>
/// <param name="Load">Linux: Load 1/5/15 Minuten.</param>
/// <param name="Containers">Laufende Docker-Container (null = kein Docker).</param>
public sealed record HostMetrics(
    TimeSpan Uptime,
    double CpuPercent,
    int CpuCores,
    long MemoryTotal,
    long MemoryUsed,
    long? SwapTotal,
    long? SwapUsed,
    IReadOnlyList<double>? Load,
    IReadOnlyList<DiskUsage> Disks,
    IReadOnlyList<ContainerUsage>? Containers = null);

/// <summary>Datenträger: Used = Total − für Benutzer verfügbar (wie df).</summary>
public sealed record DiskUsage(string Mount, string? FileSystem, long Total, long Used);

public sealed record ContainerUsage(string Name, double CpuPercent, long MemoryUsed, long? MemoryLimit);

/// <summary>Antwort des Agents auf agent.renew.</summary>
public sealed record RenewAnswer(string CertificateSigningRequestPem);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PollResponse))]
[JsonSerializable(typeof(AgentTask))]
[JsonSerializable(typeof(TaskResultRequest))]
[JsonSerializable(typeof(TaskResultResponse))]
[JsonSerializable(typeof(SystemDescription))]
[JsonSerializable(typeof(AgentPolicyReport))]
[JsonSerializable(typeof(UpdateScanResult))]
[JsonSerializable(typeof(List<BrokenAptSource>))]
[JsonSerializable(typeof(InstallRequest))]
[JsonSerializable(typeof(AppUpdateRequest))]
[JsonSerializable(typeof(RecipeUpdateRequest))]
[JsonSerializable(typeof(ContainerManageRequest))]
[JsonSerializable(typeof(ComposeFileResult))]
[JsonSerializable(typeof(List<DockerDiskUsage>))]
[JsonSerializable(typeof(ChangelogRequest))]
[JsonSerializable(typeof(ChangelogResult))]
[JsonSerializable(typeof(SoftwareList))]
[JsonSerializable(typeof(ContainerList))]
[JsonSerializable(typeof(ImagePackagesRequest))]
[JsonSerializable(typeof(ImagePackagesResult))]
[JsonSerializable(typeof(RunningServices))]
[JsonSerializable(typeof(HostMetrics))]
[JsonSerializable(typeof(FirewallStatus))]
[JsonSerializable(typeof(RemoteAccessList))]
[JsonSerializable(typeof(UserList))]
[JsonSerializable(typeof(SystemChange))]
[JsonSerializable(typeof(MonitoringStatus))]
[JsonSerializable(typeof(AgentUpdateOffer))]
[JsonSerializable(typeof(RenewAnswer))]
[JsonSerializable(typeof(RenewResponse))]
[JsonSerializable(typeof(JobLogRequest))]
public sealed partial class PullJsonContext : JsonSerializerContext
{
    public static JsonElement ToElement<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToElement(value, type);

    public static T? FromElement<T>(JsonElement? element, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        element is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e ? e.Deserialize(type) : default;
}
