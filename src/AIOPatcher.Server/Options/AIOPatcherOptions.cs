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

    /// <summary>Zeitzone der Wartungsfenster, z. B. "Europe/Berlin". Leer = Systemzeitzone (im Container: TZ).</summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Sprache für Texte, die ohne Anfrage entstehen (Notizen des Planers, Job-Zusammenfassungen, Ablauf-Meldungen): "de" oder "en".
    /// API-Fehlermeldungen folgen der Sprache des Browsers bzw. Benutzers.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// CVE-Zuordnung über OSV.dev und Microsoft MSRC, Bewertung über CISA KEV und FIRST EPSS.
    /// Der Server fragt dafür Paketnamen/KB-Nummern bei diesen Diensten ab. false = aus.
    /// </summary>
    public bool CveLookupEnabled { get; set; } = true;

    /// <summary>
    /// Optionaler NVD-API-Schlüssel für die CVE-Suche (kostenlos bei nvd.nist.gov). Ohne Schlüssel erlaubt NVD
    /// 5 Abfragen je 30 Sekunden, das reicht für gelegentliches Nachschlagen.
    /// </summary>
    public string? NvdApiKey { get; set; }

    /// <summary>
    /// Lokale CVE-Datenbank: lädt die NVD-Datenfeeds (alle CVEs, einmalig ca. 250 MB) und gleicht sie alle 2 Stunden ab.
    /// Die CVE-Suche läuft dann lokal. Braucht zusätzlich den Schalter "CVE-Abgleich". false = aus (Suche fragt NVD live).
    /// </summary>
    public bool CveMirrorEnabled { get; set; } = true;

    /// <summary>Docker-Images gegen ihre Registry prüfen (anonym, nur öffentliche Images).</summary>
    public bool ContainerUpdateCheckEnabled { get; set; } = true;

    /// <summary>Signierte Agent-Updates an ältere Agents verteilen (der Agent prüft die Signatur selbst).</summary>
    public bool AgentAutoUpdate { get; set; } = true;

    /// <summary>
    /// GitHub-Repository ("owner/name"), dessen Releases auf neue AIOPatcher-Versionen geprüft werden.
    /// Leer = keine Prüfung. Es wird nichts automatisch installiert.
    /// </summary>
    public string? UpdateCheckRepository { get; set; }

    /// <summary>
    /// Funktionen, die im Dashboard nicht eingeschaltet werden können (kommagetrennt), z. B. "AppUpdates,OpnsenseUpdates".
    /// Namen siehe Einstellungen → Funktionen &amp; Sicherheit. Schutz auch gegen einen kompromittierten Admin-Login.
    /// </summary>
    public string? LockedFeatures { get; set; }

    /// <summary>
    /// Reverse Proxys vor dem Dashboard (IPs oder Netze, kommagetrennt), z. B. "127.0.0.1" oder "172.30.0.0/24".
    /// Nur von diesen wird X-Forwarded-For übernommen (echte Client-IP für Login-Bremse und Audit-Log). Leer = kein Proxy.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>Gültigkeit eines Enrollment-Tokens.</summary>
    public int EnrollmentTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>Servername für URLs: IPv6-Adressen in eckigen Klammern.</summary>
    public string UrlHost => System.Net.IPAddress.TryParse(PublicHostname, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{ip}]"
        : PublicHostname;

    public string AgentApiUrl => $"https://{UrlHost}:{AgentPort}";
    public TimeSpan OfflineAfter => TimeSpan.FromSeconds(AgentCheckinIntervalSeconds * 3);

    /// <summary>Aufbewahrung von Job-Logs in Tagen.</summary>
    public int JobLogRetentionDays { get; set; } = 90;

    public string DatabasePath => Path.Combine(DataDirectory, "aiopatcher.db");
    public string CaDirectory => Path.Combine(DataDirectory, "ca");
    public string DataProtectionDirectory => Path.Combine(DataDirectory, "dataprotection");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>Wiederbeschaffbare Daten (lokale CVE-Datenbank); nicht Teil der Sicherungen.</summary>
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
}
