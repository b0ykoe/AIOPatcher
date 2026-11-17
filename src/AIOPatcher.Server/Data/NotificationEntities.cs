namespace AIOPatcher.Server.Data;

/// <summary>Ereignisse, für die ein Kanal benachrichtigt werden kann (Bitmaske).</summary>
[Flags]
public enum NotificationEvents
{
    None = 0,
    JobFailed = 1,
    JobSucceeded = 2,
    HostOffline = 4,
    HostOnline = 8,
    NewSecurityUpdates = 16,

    /// <summary>Ablauf angehalten oder wartet auf Freigabe.</summary>
    RunNeedsAttention = 32,
    RunFinished = 64,

    /// <summary>Offenes Update schließt eine Lücke aus dem CISA-KEV-Katalog.</summary>
    KevDetected = 128,
    ContainerUpdates = 256,
    CertificateExpiring = 512,

    /// <summary>Agent-Update zurückgerollt oder abgelehnt (Selbsttest fehlgeschlagen).</summary>
    AgentUpdateFailed = 1024,
}

public enum ChannelType
{
    Ntfy,
    Email,
    Webhook,
    Discord,
    Slack,
    Telegram,
}

public sealed class NotificationChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ChannelType Type { get; set; }
    public bool Enabled { get; set; } = true;
    public NotificationEvents Events { get; set; }

    /// <summary>Nur Ereignisse dieser Site. null = alle Sites.</summary>
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Konfiguration als JSON, per Data Protection verschlüsselt (enthält Tokens/Passwörter).</summary>
    public required string ProtectedConfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentAt { get; set; }
    public string? LastError { get; set; }
}
