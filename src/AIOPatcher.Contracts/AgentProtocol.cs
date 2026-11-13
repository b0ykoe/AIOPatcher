using System.Text.Json.Serialization;

namespace AIOPatcher.Contracts;

// Protokoll zwischen Agent und Server (Port 8443, mTLS).
// Änderungen hier immer abwärtskompatibel halten: Server muss Agent-Version n und n-1 verstehen.

public static class AgentProtocol
{
    public const int Version = 1;

    public const string EnrollPath = "/agent/enroll";
    public const string CheckinPath = "/agent/checkin";
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

public sealed record AvailableUpdate(
    string Name,
    string? CurrentVersion,
    string? AvailableVersion,
    bool IsSecurity,
    string? Classification);

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
    Guid? RunningJobId);

public sealed record AgentJob(Guid JobId, JobType Type, PatchScope Scope, bool AllowReboot);

public sealed record CheckinResponse(int CheckinIntervalSeconds, AgentJob? Job);

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
public sealed partial class AgentJsonContext : JsonSerializerContext;
