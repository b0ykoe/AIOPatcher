using AIOPatcher.Contracts;

namespace AIOPatcher.Server.Data;

// Alle Zeitstempel in UTC (DateTime, Kind=Utc). SQLite kann DateTimeOffset nicht sortieren.

public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Optionale IP-Allowlist für Agents dieser Site (CIDRs, kommagetrennt). Leer = keine Einschränkung.</summary>
    public string? AllowedAgentCidrs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ManagedHost> Hosts { get; set; } = [];
    public List<Subnet> Subnets { get; set; } = [];
}

public sealed class ManagedHost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }

    public required string Name { get; set; }
    public required string Hostname { get; set; }
    public OsFamily OsFamily { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? KernelVersion { get; set; }
    public string? AgentVersion { get; set; }

    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastScanAt { get; set; }
    public TimeSpan? Uptime { get; set; }
    public bool RebootRequired { get; set; }

    /// <summary>unattended-upgrades bzw. Windows-Automatik ist aktiv: Gefahr, dass doppelt gepatcht wird.</summary>
    public bool NativeAutoUpdatesEnabled { get; set; }

    // mTLS-Identität
    public required string CertificateThumbprint { get; set; }
    public DateTime CertificateExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    // Auto-Patch
    public bool AutoPatchEnabled { get; set; }
    public PatchScope AutoPatchScope { get; set; } = PatchScope.Security;
    public bool AutoRebootAllowed { get; set; }
    public MaintenanceDays MaintenanceDays { get; set; } = MaintenanceDays.None;
    public TimeOnly MaintenanceStart { get; set; } = new(3, 0);
    public int MaintenanceDurationMinutes { get; set; } = 120;

    public List<PendingUpdate> PendingUpdates { get; set; } = [];
    public List<PatchJob> Jobs { get; set; } = [];
}

[Flags]
public enum MaintenanceDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Daily = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday,
}

public sealed class PendingUpdate
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public required string Name { get; set; }
    public string? CurrentVersion { get; set; }
    public string? AvailableVersion { get; set; }
    public bool IsSecurity { get; set; }
    public string? Classification { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Rejected,
    Cancelled,
}

public sealed class PatchJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public JobType Type { get; set; }
    public PatchScope Scope { get; set; } = PatchScope.Security;
    public bool AllowReboot { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>"admin:&lt;name&gt;" oder "scheduler".</summary>
    public required string RequestedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Summary { get; set; }
    public bool RebootPerformed { get; set; }

    public List<JobLogEntry> LogEntries { get; set; } = [];
}

public sealed class JobLogEntry
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public PatchJob? Job { get; set; }
    public DateTime Timestamp { get; set; }
    public required string Text { get; set; }
}

public sealed class Subnet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Normalisierte CIDR-Schreibweise, z. B. "10.10.0.0/24" oder "2a01:4f8::/64".</summary>
    public required string Cidr { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum IpSource
{
    Agent,
    Manual,
}

public sealed class IpAddressEntry
{
    public long Id { get; set; }
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }
    public Guid? SubnetId { get; set; }
    public Subnet? Subnet { get; set; }
    public Guid? HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public required string Address { get; set; }
    public int? PrefixLength { get; set; }
    public string? InterfaceName { get; set; }
    public IpSource Source { get; set; }
    public string? Description { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>SHA-256 des Tokens (Base64). Der Klartext wird nur einmal angezeigt.</summary>
    public required string TokenHash { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? UsedByHostId { get; set; }
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>TOTP-Secret, verschlüsselt per Data Protection.</summary>
    public required string ProtectedTotpSecret { get; set; }

    /// <summary>false bis TOTP im Setup bestätigt wurde.</summary>
    public bool IsSetupComplete { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }

    /// <summary>Zuletzt akzeptierter TOTP-Zeitschritt (Schutz vor Wiederverwendung eines Codes).</summary>
    public long LastTotpTimeStep { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? HostId { get; set; }
    public string? Details { get; set; }
    public string? RemoteIp { get; set; }
}
