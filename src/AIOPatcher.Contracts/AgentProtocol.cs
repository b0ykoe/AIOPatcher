using System.Text.Json.Serialization;

namespace AIOPatcher.Contracts;

// Protokoll zwischen Agent und Server (Port 8443, mTLS).
// Änderungen hier immer abwärtskompatibel halten: Server muss Agent-Version n und n-1 verstehen.

public static class AgentProtocol
{
    public const int Version = 1;

    public const string EnrollPath = "/agent/enroll";
    public const string CheckinPath = "/agent/checkin";
    public const string RenewPath = "/agent/renew";
    public static string JobLogPath(Guid jobId) => $"/agent/jobs/{jobId}/log";
    public static string JobResultPath(Guid jobId) => $"/agent/jobs/{jobId}/result";
}

[JsonConverter(typeof(JsonStringEnumConverter<OsFamily>))]
public enum OsFamily
{
    Linux,
    Windows,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobType>))]
public enum JobType
{
    Scan,
    Patch,
    Reboot,

    /// <summary>Programme per winget aktualisieren (Windows). Nur an Agents ab Protokoll mit Software-Inventar senden.</summary>
    AppUpdate,
}

[JsonConverter(typeof(JsonStringEnumConverter<PatchScope>))]
public enum PatchScope
{
    Security,
    All,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobOutcome>))]
public enum JobOutcome
{
    Succeeded,
    Failed,
    Rejected, // durch lokale Policy des Agents abgelehnt
}

/// <summary>Einmalige Registrierung: Token + CSR (PEM) rein, Zertifikat raus.</summary>
public sealed record EnrollRequest(
    string EnrollmentToken,
    string CertificateSigningRequestPem,
    string Hostname,
    OsFamily OsFamily,
    string AgentVersion);

public sealed record EnrollResponse(
    Guid HostId,
    Guid SiteId,
    string ClientCertificatePem,
    string CaCertificatePem);

public sealed record NetworkAddress(string InterfaceName, string Address, int PrefixLength);

/// <param name="SourcePackage">Debian/Ubuntu: Quellpaket (z. B. "openssl" für libssl3). Für die CVE-Zuordnung.</param>
public sealed record AvailableUpdate(
    string Name,
    string? CurrentVersion,
    string? AvailableVersion,
    bool IsSecurity,
    string? Classification,
    string? SourcePackage = null);

public sealed record CheckinRequest(
    int ProtocolVersion,
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
    IReadOnlyList<AvailableUpdate>? Updates, // null = in diesem Check-in nicht gescannt
    Guid? RunningJobId,
    IReadOnlyList<ContainerReport>? Containers = null, // null = nicht erfasst (kein Docker oder kein Scan)
    string? Architecture = null, // "x64" oder "arm64", für das passende Update-Binary
    IReadOnlyList<InstalledSoftware>? Software = null); // null = in diesem Check-in nicht erfasst

/// <summary>
/// Installiertes Programm bzw. Paket. Source: "dpkg", "snap", "registry" (Windows-Programme), "winget".
/// PackageId: Debian-Quellpaket bzw. winget-ID. AvailableVersion: laut winget verfügbare neuere Version.
/// </summary>
public sealed record InstalledSoftware(
    string Name,
    string? Version,
    string? Publisher,
    string Source,
    string? PackageId = null,
    string? AvailableVersion = null,
    string? Url = null);

/// <summary>Laufender Docker-Container. RepoDigests: "repo@sha256:…" des lokalen Images (leer bei selbst gebauten Images).</summary>
public sealed record ContainerReport(string Name, string Image, string? ImageId, IReadOnlyList<string>? RepoDigests, string? State);

/// <param name="Holds">Muster zurückgehaltener Updates (siehe <see cref="HoldPatterns"/>). Ältere Server senden nichts.</param>
/// <param name="Packages">AppUpdate: winget-IDs der zu aktualisierenden Programme.</param>
public sealed record AgentJob(Guid JobId, JobType Type, PatchScope Scope, bool AllowReboot, IReadOnlyList<string>? Holds = null, IReadOnlyList<string>? Packages = null);

/// <param name="Update">Neuere, signierte Agent-Version verfügbar.</param>
/// <param name="RenewCertificate">Client-Zertifikat läuft bald ab: neues per /agent/renew anfordern.</param>
public sealed record CheckinResponse(int CheckinIntervalSeconds, AgentJob? Job, AgentUpdateOffer? Update = null, bool RenewCertificate = false);

/// <summary>Signatur: ECDSA P-256 über SHA-256 des Binaries (DER, Base64), geprüft gegen den im Agent eingebauten Release-Schlüssel.</summary>
public sealed record AgentUpdateOffer(string Version, string DownloadPath, string Sha256, string Signature);

public sealed record RenewRequest(string CertificateSigningRequestPem);

public sealed record RenewResponse(string ClientCertificatePem);

public sealed record JobLogRequest(IReadOnlyList<JobLogLine> Lines);

public sealed record JobLogLine(DateTime TimestampUtc, string Text);

public sealed record JobResultRequest(JobOutcome Outcome, string? Summary, bool RebootPerformed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(CheckinRequest))]
[JsonSerializable(typeof(CheckinResponse))]
[JsonSerializable(typeof(JobLogRequest))]
[JsonSerializable(typeof(JobResultRequest))]
[JsonSerializable(typeof(RenewRequest))]
[JsonSerializable(typeof(RenewResponse))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
