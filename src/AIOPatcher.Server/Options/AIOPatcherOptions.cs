namespace AIOPatcher.Server.Options;

public sealed class AIOPatcherOptions
{
    public const string SectionName = "AIOPatcher";

    /// <summary>Fester DNS-Name, unter dem Agents und Dashboard den Server erreichen. Steht im Server-Zertifikat.</summary>
    public string PublicHostname { get; set; } = "localhost";

    /// <summary>
    /// Verzeichnis für Datenbank, CA, Data-Protection-Schlüssel und Backups. Im Container: /data.
    /// Lokaler Standard bewusst nicht "data": Windows unterscheidet nicht zwischen "data" und dem Code-Ordner "Data".
    /// </summary>
    public string DataDirectory { get; set; } = "runtime-data";

    /// <summary>Dashboard + Dashboard-API. Nur intern/VPN erreichbar machen.</summary>
    public int DashboardPort { get; set; } = 9443;

    /// <summary>Agent-API mit mTLS. Darf öffentlich erreichbar sein.</summary>
    public int AgentPort { get; set; } = 8443;

    /// <summary>Wie oft sich Agents melden. Ein Host gilt nach 3 verpassten Check-ins als offline.</summary>
    public int AgentCheckinIntervalSeconds { get; set; } = 60;

    /// <summary>Gültigkeit eines Enrollment-Tokens.</summary>
    public int EnrollmentTokenLifetimeMinutes { get; set; } = 15;

    public string AgentApiUrl => $"https://{PublicHostname}:{AgentPort}";
    public TimeSpan OfflineAfter => TimeSpan.FromSeconds(AgentCheckinIntervalSeconds * 3);

    /// <summary>Aufbewahrung von Job-Logs in Tagen.</summary>
    public int JobLogRetentionDays { get; set; } = 90;

    public string DatabasePath => Path.Combine(DataDirectory, "aiopatcher.db");
    public string CaDirectory => Path.Combine(DataDirectory, "ca");
    public string DataProtectionDirectory => Path.Combine(DataDirectory, "dataprotection");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
}
