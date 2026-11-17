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

    /// <summary>Startzeit des Systems (UTC), aus der gemeldeten Uptime berechnet; die Anzeige rechnet damit weiter.</summary>
    public DateTime? BootedAt { get; set; }

    /// <summary>Kennung des laufenden Agent-Prozesses (Poll-Header) und wann sie zuletzt wechselte (= Neustart).</summary>
    public string? AgentInstance { get; set; }
    public DateTime? AgentRestartedAt { get; set; }
    public bool RebootRequired { get; set; }

    /// <summary>unattended-upgrades bzw. Windows-Automatik ist aktiv: Gefahr, dass doppelt gepatcht wird.</summary>
    public bool NativeAutoUpdatesEnabled { get; set; }

    /// <summary>Nicht ladbare Paketquellen aus dem letzten Scan (JSON-Liste von BrokenAptSource), null = keine/unbekannt.</summary>
    public string? BrokenAptSources { get; set; }

    // mTLS-Identität
    public required string CertificateThumbprint { get; set; }
    public DateTime CertificateExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>
    /// Erneuerung: neues Zertifikat ist ausgestellt, der Agent hat es aber noch nicht benutzt.
    /// Bis dahin gilt auch das alte, damit ein verlorenes Speichern den Agent nicht aussperrt.
    /// </summary>
    public string? PendingCertificateThumbprint { get; set; }
    public DateTime? PendingCertificateExpiresAt { get; set; }

    /// <summary>Wann "Zertifikat läuft ab" gemeldet wurde.</summary>
    public DateTime? CertificateExpiryNotifiedAt { get; set; }

    // Agent-Self-Update: letztes Ergebnis laut Agent
    public AgentUpdateOutcome? AgentUpdateOutcome { get; set; }
    public string? AgentUpdateFromVersion { get; set; }
    public string? AgentUpdateToVersion { get; set; }
    public string? AgentUpdateMessage { get; set; }
    public DateTime? AgentUpdateAt { get; set; }

    /// <summary>Diese Version ist auf dem Host gescheitert und wird nicht erneut angeboten (bis "Erneut versuchen").</summary>
    public string? AgentUpdateBlockedVersion { get; set; }

    // Pull-Agent (Protokoll v2). Wann was angefordert wurde, steht in AgentTasks.
    /// <summary>1 = alter Check-in (Push), 2 = Pull-Agent. 0 = noch nie gemeldet.</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>"x64" oder "arm64" (für das passende Update-Binary).</summary>
    public string? Architecture { get; set; }

    /// <summary>Module laut Agent (JSON-Liste von <see cref="AIOPatcher.Contracts.ModuleInfo"/>).</summary>
    public string? ReportedModules { get; set; }

    /// <summary>Im Dashboard abgeschaltete Module (kommagetrennt). Für die gibt es keine Aufträge.</summary>
    public string? DisabledModules { get; set; }

    /// <summary>Zuletzt erfolgreich ausgerollte Firewall-Version (null = keine bzw. Original wiederhergestellt).</summary>
    public Guid? FirewallVersionId { get; set; }
    public DateTime? FirewallAppliedAt { get; set; }

    // Auto-Patch
    public bool AutoPatchEnabled { get; set; }
    public PatchScope AutoPatchScope { get; set; } = PatchScope.Security;
    public bool AutoRebootAllowed { get; set; }
    public MaintenanceDays MaintenanceDays { get; set; } = MaintenanceDays.None;
    public TimeOnly MaintenanceStart { get; set; } = new(3, 0);
    public int MaintenanceDurationMinutes { get; set; } = 120;

    /// <summary>Beginn (UTC) des letzten Wartungsfensters, das der Planer schon behandelt hat. Verhindert doppelte Jobs.</summary>
    public DateTime? LastAutoPatchWindowStart { get; set; }

    /// <summary>Was der Planer im letzten Fenster entschieden hat (fürs Dashboard), z. B. "Nichts zu tun".</summary>
    public string? LastAutoPatchNote { get; set; }

    /// <summary>Wann "offline" gemeldet wurde. Wird beim nächsten Check-in wieder geleert (dann "wieder online").</summary>
    public DateTime? OfflineNotifiedAt { get; set; }

    /// <summary>Zuordnung zu Proxmox-VM/-Container oder Hetzner-Cloud-Server (für Snapshots und Backup-Prüfung).</summary>
    public Guid? IntegrationId { get; set; }
    public Integration? Integration { get; set; }

    /// <summary>Proxmox: "node/qemu/101" bzw. "node/lxc/105". Hetzner Cloud: Server-ID.</summary>
    public string? ExternalId { get; set; }

    /// <summary>KEV-CVEs, zu denen für diesen Host schon benachrichtigt wurde (kommagetrennt).</summary>
    public string? NotifiedKevCves { get; set; }

    public List<ContainerInfo> Containers { get; set; } = [];
    public List<InstalledApp> Apps { get; set; } = [];

    /// <summary>Frei vergebene Schlagworte, normalisiert (klein, sortiert, kommagetrennt), z. B. "db,prod".</summary>
    public string? Tags { get; set; }

    public List<PendingUpdate> PendingUpdates { get; set; } = [];
    public List<PatchJob> Jobs { get; set; } = [];
    public List<RunbookWaveHost> RunbookMemberships { get; set; } = [];
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

    /// <summary>Debian/Ubuntu-Quellpaket (vom Agent), für die CVE-Zuordnung.</summary>
    public string? SourcePackage { get; set; }

    /// <summary>CVEs, die dieses Update schließt (kommagetrennt). null = noch nicht ermittelt.</summary>
    public string? Cves { get; set; }
    public bool IsKev { get; set; }
    public double? MaxEpss { get; set; }
    public DateTime? CveCheckedAt { get; set; }
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

    /// <summary>AppUpdate: winget-IDs (kommagetrennt).</summary>
    public string? Packages { get; set; }

    /// <summary>SystemChange: <see cref="AIOPatcher.Contracts.SystemChange"/> als JSON, immer ohne Geheimnis.</summary>
    public string? Payload { get; set; }

    /// <summary>
    /// SystemChange mit Passwort: per ASP.NET Data Protection verschlüsselt. Wird bei der Übergabe an den Agent
    /// gelöscht (auch wenn der Job abgebrochen wird oder verfällt), steht nie im Klartext in DB, Log oder Audit.
    /// </summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>Firewall-Version, die dieser Job ausrollt (für "aktive Version" am Host).</summary>
    public Guid? FirewallVersionId { get; set; }

    /// <summary>Auto-Patch: spätester Start (Ende des Wartungsfensters). Danach wird der Job nicht mehr vergeben.</summary>
    public DateTime? NotAfter { get; set; }

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

    /// <summary>ARP-Tabelle der OPNsense (Geräte ohne Agent, z. B. Drucker, Switches).</summary>
    Opnsense,
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
    public string? MacAddress { get; set; }

    /// <summary>Name laut Quelle (OPNsense/DHCP) oder manuell vergeben.</summary>
    public string? Hostname { get; set; }
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

public enum UserRole
{
    /// <summary>Alles, inklusive Benutzer, Sites und Hosts löschen.</summary>
    Admin,

    /// <summary>Jobs, Abläufe, Auto-Patch, Hosts hinzufügen. Keine Benutzer/Sites.</summary>
    Operator,

    /// <summary>Nur lesen.</summary>
    Viewer,
}

/// <summary>Einfach: wenige Seiten, geführte Abläufe. Erweitert: alle Funktionen.</summary>
public enum UiMode
{
    Simple,
    Advanced,
}

/// <summary>Benutzerkonto (Tabelle heißt aus historischen Gründen "AdminUsers").</summary>
public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }

    /// <summary>Leer, solange die Einladung nicht angenommen wurde.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>TOTP-Secret, verschlüsselt per Data Protection. Leer, solange nicht eingerichtet.</summary>
    public required string ProtectedTotpSecret { get; set; }

    /// <summary>false bis Passwort und TOTP eingerichtet und bestätigt sind.</summary>
    public bool IsSetupComplete { get; set; }

    public UserRole Role { get; set; } = UserRole.Admin;
    public bool IsDisabled { get; set; }

    /// <summary>"de", "en" oder null (Browsersprache).</summary>
    public string? Language { get; set; }

    public UiMode UiMode { get; set; } = UiMode.Simple;

    /// <summary>Ändert sich bei Passwort-, Rollen- und Statuswechsel: alte Sitzungen werden dadurch ungültig.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>SHA-256 des Einladungs-Tokens (Base64). Nur gesetzt, solange eine Einladung offen ist.</summary>
    public string? InviteTokenHash { get; set; }
    public DateTime? InviteExpiresAt { get; set; }
    public string? CreatedBy { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }

    /// <summary>Zuletzt akzeptierter TOTP-Zeitschritt (Schutz vor Wiederverwendung eines Codes).</summary>
    public long LastTotpTimeStep { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Verknüpftes SSO-Konto ("Issuer|sub"). Benutzer ohne Passwort melden sich nur per SSO an.</summary>
    public string? SsoSubject { get; set; }
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
