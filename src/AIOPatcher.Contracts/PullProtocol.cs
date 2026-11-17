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
// nur die Agent-Version als Header bei (wie ein User-Agent).

public static class PullProtocol
{
    public const int Version = 2;

    public const string PollPath = "/agent/v2/poll";
    public const string VersionHeader = "X-AIOPatcher-Agent-Version";
    public static string TaskLogPath(Guid taskId) => $"/agent/v2/tasks/{taskId}/log";
    public static string TaskResultPath(Guid taskId) => $"/agent/v2/tasks/{taskId}/result";
}

/// <summary>Module des Agents. Jedes Modul kennt nur seine festen Aktionen.</summary>
public static class AgentModules
{
    /// <summary>describe: Systemdaten, IPs, Module, Ergebnis des letzten Self-Updates.</summary>
    public const string System = "system";

    /// <summary>scan, install, reboot (apt bzw. Windows Update).</summary>
    public const string Updates = "updates";

    /// <summary>inventory, appupdate (winget).</summary>
    public const string Software = "software";

    /// <summary>inventory (laufende Container).</summary>
    public const string Docker = "docker";

    /// <summary>status, change.</summary>
    public const string Firewall = "firewall";

    /// <summary>status, change (SSH/RDP).</summary>
    public const string RemoteAccess = "remoteaccess";

    /// <summary>list, change.</summary>
    public const string Users = "users";

    /// <summary>update (Self-Update), renew (Client-Zertifikat).</summary>
    public const string Agent = "agent";

    public static readonly string[] All = [System, Updates, Software, Docker, Firewall, RemoteAccess, Users, Agent];

    /// <summary>Ohne diese geht nichts: nicht abschaltbar.</summary>
    public static readonly string[] Required = [System, Agent];

    /// <summary>Modul für eine Systemänderung.</summary>
    public static string ForChange(SystemChangeKind kind) => kind switch
    {
        SystemChangeKind.FirewallApply or SystemChangeKind.FirewallRestoreOriginal => Firewall,
        SystemChangeKind.RemoteAccessFix or SystemChangeKind.RemoteAccessRevert => RemoteAccess,
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
    public const string Status = "status";
    public const string List = "list";
    public const string Change = "change";
    public const string Update = "update";
    public const string Renew = "renew";
}

/// <param name="Task">Höchstens ein Auftrag pro Abfrage. Der Agent fragt nach Erledigung sofort wieder.</param>
/// <param name="UpdateKey">Öffentlicher Update-Schlüssel des Servers (siehe <see cref="CheckinResponse"/>).</param>
public sealed record PollResponse(int PollIntervalSeconds, AgentTask? Task, string? UpdateKey = null);

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
    AgentUpdateReport? UpdateReport = null);

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

public sealed record ContainerList(IReadOnlyList<ContainerReport> Containers);

public sealed record RemoteAccessList(IReadOnlyList<RemoteAccessSetting> Settings);

public sealed record UserList(IReadOnlyList<LocalUser> Users);

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
[JsonSerializable(typeof(UpdateScanResult))]
[JsonSerializable(typeof(List<BrokenAptSource>))]
[JsonSerializable(typeof(InstallRequest))]
[JsonSerializable(typeof(AppUpdateRequest))]
[JsonSerializable(typeof(SoftwareList))]
[JsonSerializable(typeof(ContainerList))]
[JsonSerializable(typeof(FirewallStatus))]
[JsonSerializable(typeof(RemoteAccessList))]
[JsonSerializable(typeof(UserList))]
[JsonSerializable(typeof(SystemChange))]
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
