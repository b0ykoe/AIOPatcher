namespace AIOPatcher.Server.Data;

/// <summary>
/// Ziel für Monitoring-Clients: Wazuh-Manager bzw. Checkmk-Site. Gilt für eine Site von AIOPatcher oder (SiteId null)
/// für alle Hosts ohne eigenes Ziel. AIOPatcher verwaltet nur die Clients, nie den Manager bzw. die Site selbst.
/// </summary>
public sealed class MonitoringTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>"wazuh" oder "checkmk" (<see cref="AIOPatcher.Contracts.MonitoringProducts"/>).</summary>
    public required string Product { get; set; }

    /// <summary>Hosts dieser Site; null = alle übrigen.</summary>
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }

    // --- Wazuh ---

    /// <summary>Manager-Adresse (Hostname oder IP).</summary>
    public string? Address { get; set; }
    public int? Port { get; set; }

    /// <summary>"tcp" oder "udp"; null = nicht ändern.</summary>
    public string? Protocol { get; set; }

    /// <summary>"new" = beim Manager neu anmelden, "keys" = client.keys wurden auf den Manager übernommen.</summary>
    public string Enrollment { get; set; } = "new";
    public int? EnrollmentPort { get; set; }
    public string? Groups { get; set; }

    /// <summary>Version des Managers: Clients werden auf genau diese Version installiert bzw. aktualisiert.</summary>
    public string? ManagerVersion { get; set; }

    // --- Checkmk ---

    /// <summary>URL der Site, z. B. https://cmk.example.com/mysite (daraus Server und Site-Name).</summary>
    public string? SiteUrl { get; set; }

    /// <summary>Port des Agent Receivers (Standard 8000).</summary>
    public int? ReceiverPort { get; set; }

    /// <summary>Benutzer für "cmk-agent-ctl register" (z. B. agent_registration).</summary>
    public string? RegistrationUser { get; set; }

    /// <summary>IPs/Netze der Site für Legacy-Agents (only_from), durch Leerzeichen oder Komma getrennt.</summary>
    public string? OnlyFrom { get; set; }

    /// <summary>Optional: MSI für Windows-Versionen, die der Agent der Site nicht mehr unterstützt (z. B. 2.1 für 2008 R2).</summary>
    public string? LegacyPackageUrl { get; set; }

    /// <summary>SHA-256 des TLS-Zertifikats der Site (selbstsigniert). Leer = normale Prüfung.</summary>
    public string? TlsFingerprintSha256 { get; set; }

    /// <summary>Version der Site, zuletzt aus der Paketliste gelesen.</summary>
    public string? SiteVersion { get; set; }

    // --- beide ---

    /// <summary>Wazuh: Anmeldepasswort (authd.pass). Checkmk: Passwort des Registrierungsbenutzers. Per Data Protection verschlüsselt.</summary>
    public string? ProtectedSecret { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
}

/// <summary>Zuletzt gemeldeter Zustand eines Monitoring-Clients auf einem Host (aus monitoring.status).</summary>
public sealed class MonitoringClientState
{
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public required string Product { get; set; }

    public required string Variant { get; set; }
    public string? Version { get; set; }
    public bool ServiceRunning { get; set; }

    /// <summary>Eingetragene Ziele, eine Zeile je Eintrag.</summary>
    public string Addresses { get; set; } = "";
    public bool? Connected { get; set; }
    public string? Detail { get; set; }
    public bool CanConfigure { get; set; }
    public bool Held { get; set; }
    public DateTime SeenAt { get; set; }

    /// <summary>Für die Benachrichtigung: seit wann getrennt (null = verbunden bzw. unbekannt).</summary>
    public DateTime? DisconnectedSince { get; set; }

    public IReadOnlyList<string> AddressList => Addresses.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
