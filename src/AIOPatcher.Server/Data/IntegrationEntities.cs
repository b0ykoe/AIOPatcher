namespace AIOPatcher.Server.Data;

public enum IntegrationType
{
    Proxmox,
    HetznerCloud,
    Opnsense,
}

/// <summary>Verbindung zu einer externen API: Proxmox VE, Hetzner Cloud oder OPNsense.</summary>
public sealed class Integration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public IntegrationType Type { get; set; }

    /// <summary>Optional: gehört zu dieser Site (z. B. ein Kunden-Projekt bei Hetzner).</summary>
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Proxmox: https://pve.example:8006. OPNsense: https://fw.example. Hetzner: leer (feste API-URL).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Zugangsdaten, per Data Protection verschlüsselt. Proxmox: "user@realm!tokenid=uuid".
    /// Hetzner: API-Token. OPNsense: "key:secret".
    /// </summary>
    public required string ProtectedSecret { get; set; }

    /// <summary>SHA-256 des TLS-Zertifikats (selbstsigniertes Proxmox/OPNsense). Leer = normale Zertifikatsprüfung.</summary>
    public string? TlsFingerprintSha256 { get; set; }

    /// <summary>So viele AIOPatcher-Snapshots pro Maschine behalten; ältere werden gelöscht.</summary>
    public int KeepSnapshots { get; set; } = 3;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Zuletzt abgefragter Zustand als JSON (z. B. OPNsense-Firmware), fürs Dashboard.</summary>
    public string? StatusJson { get; set; }
}
