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

    /// <summary>Nach einem Neustart laufen Dienste oder Container nicht mehr, die vorher liefen.</summary>
    RebootCheckFailed = 2048,

    /// <summary>Ein laufender Container ist gestoppt oder sein Healthcheck meldet "unhealthy".</summary>
    ContainerStopped = 4096,

    /// <summary>Ein Monitoring-Client (Wazuh, Checkmk) hat die Verbindung verloren oder sein Dienst läuft nicht mehr.</summary>
    MonitoringDisconnected = 8192,

    /// <summary>Proxmox Backup Server: Task fehlgeschlagen, Datastore fast voll oder nicht verfügbar, Backup zu alt.</summary>
    BackupProblem = 16384,

    /// <summary>Site-Sharing: Freigabe angenommen oder beendet, Einladung verbrannt, Aktion einer anderen Instanz.</summary>
    SharingActivity = 32768,
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

/// <summary>Aufmachung der Nachrichten eines Kanals.</summary>
public enum NotificationStyle
{
    /// <summary>Titel, Text, Link (wie bisher).</summary>
    Standard,

    /// <summary>Nur Titel und Link.</summary>
    Short,

    /// <summary>Dazu Host, Site, Ereignis und Uhrzeit.</summary>
    Detailed,

    /// <summary>Eigene Formatierung des Dienstes (Telegram-HTML mit Knopf, Discord-Embed, Slack-Blocks, HTML-Mail, ntfy-Markdown).</summary>
    Rich,
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

    /// <summary>Nur Ereignisse dieser geteilten Site (Site einer anderen Instanz). Solche Ereignisse gehen nur an Kanäle, die sie ausdrücklich gewählt haben.</summary>
    public Guid? RemoteSiteId { get; set; }
    public RemoteSite? RemoteSite { get; set; }

    /// <summary>Konfiguration als JSON, per Data Protection verschlüsselt (enthält Tokens/Passwörter).</summary>
    public required string ProtectedConfig { get; set; }

    public NotificationStyle Style { get; set; } = NotificationStyle.Standard;

    /// <summary>
    /// Sammeln: 0 = jede Nachricht sofort. Sonst gehen Nachrichten gesammelt raus, sobald eine Weile nichts Neues kam
    /// (alle Hosts haben sich gemeldet), spätestens nach so vielen Minuten.
    /// </summary>
    public int DigestMinutes { get; set; }

    /// <summary>Ereignisse, die auch beim Sammeln sofort rausgehen (Wichtiges wie KEV, fehlgeschlagene Jobs, Ausfälle).</summary>
    public NotificationEvents InstantEvents { get; set; } = ImportantEvents;

    /// <summary>Vorgabe für "sofort trotz Sammeln".</summary>
    public const NotificationEvents ImportantEvents = NotificationEvents.JobFailed | NotificationEvents.HostOffline | NotificationEvents.RunNeedsAttention |
        NotificationEvents.KevDetected | NotificationEvents.CertificateExpiring | NotificationEvents.AgentUpdateFailed | NotificationEvents.RebootCheckFailed |
        NotificationEvents.ContainerStopped | NotificationEvents.MonitoringDisconnected | NotificationEvents.BackupProblem;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Gesammelte Nachricht eines Kanals, die mit der nächsten Sammelnachricht rausgeht.</summary>
public sealed class PendingNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }
    public NotificationChannel? Channel { get; set; }
    public DateTime CreatedAt { get; set; }
    public NotificationEvents Event { get; set; }
    public string Severity { get; set; } = "Info";
    public required string Title { get; set; }
    public string Message { get; set; } = "";
    public Guid? HostId { get; set; }
    public string? HostName { get; set; }
    public Guid? SiteId { get; set; }
    public string? SiteName { get; set; }
    public string? LinkPath { get; set; }
}
