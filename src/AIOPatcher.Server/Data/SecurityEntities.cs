namespace AIOPatcher.Server.Data;

/// <summary>Bewertung einer CVE: EPSS (Wahrscheinlichkeit einer Ausnutzung) und Eintrag im CISA-KEV-Katalog.</summary>
public sealed class CveInfo
{
    /// <summary>z. B. "CVE-2024-3094".</summary>
    public required string Id { get; set; }

    /// <summary>0..1, Wahrscheinlichkeit einer Ausnutzung in den nächsten 30 Tagen (FIRST EPSS).</summary>
    public double? Epss { get; set; }
    public double? EpssPercentile { get; set; }

    /// <summary>Wird laut CISA aktiv ausgenutzt.</summary>
    public bool IsKev { get; set; }
    public DateTime? KevDateAdded { get; set; }
    public string? KevName { get; set; }
    public bool KevRansomware { get; set; }

    public DateTime? EpssUpdatedAt { get; set; }
}

/// <summary>Zwischenspeicher für Abfragen bei OSV.dev und MSRC (Schlüssel z. B. "osv|Debian:12|openssl|3.0.13-1|3.0.15-1").</summary>
public sealed class VulnLookup
{
    public required string Key { get; set; }

    /// <summary>Kommagetrennte CVE-IDs, leer = keine bekannt.</summary>
    public string Cves { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}

/// <summary>Docker-Container auf einem Host, wie zuletzt vom Agent gemeldet.</summary>
public sealed class ContainerInfo
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public required string Name { get; set; }

    /// <summary>Konfiguriertes Image, z. B. "nginx:1.27" oder "ghcr.io/org/app:latest".</summary>
    public required string Image { get; set; }

    /// <summary>Normalisierte Referenz für den Registry-Abgleich ("docker.io/library/nginx:1.27"), null bei Digest-gepinnten Images.</summary>
    public string? Reference { get; set; }
    public string? ImageId { get; set; }

    /// <summary>
    /// Digests des lokalen Images aus RepoDigests für dieses Repository ("sha256:…", mehrere durch Leerzeichen getrennt:
    /// ein Image kann über mehrere Tags gezogen worden sein). null bei lokal gebauten Images.
    /// </summary>
    public string? LocalDigest { get; set; }

    /// <summary>Gilt das lokale Image als aktuell, wenn die Registry diesen Digest meldet?</summary>
    public bool HasDigest(string remote) => LocalDigest is not null && LocalDigest.Split(' ').Contains(remote);
    public string? State { get; set; }
    public DateTime SeenAt { get; set; }

    /// <summary>Compose-Projekt und -Dienst (null = nicht per Compose gestartet).</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string? ComposeProject { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string? ComposeService { get; set; }

    /// <summary>Compose-Dateien laut Label, durch Komma getrennt.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(1000)]
    public string? ComposeFiles { get; set; }

    /// <summary>Liegen alle Compose-Dateien auf dem Host? (Portainer-Stacks: nein)</summary>
    public bool ComposeFilesFound { get; set; }

    /// <summary>Warum sich der Container nicht per Klick aktualisieren lässt: "notCompose", "portainer", "noFile"; null = geht.</summary>
    public string? UpdateBlocker =>
        ComposeProject is null || ComposeService is null ? "notCompose"
        : ComposeFilesFound ? null
        : (ComposeFiles ?? "").Split(',').Any(f => f.StartsWith("/data/compose/", StringComparison.Ordinal)) ? "portainer"
        : "noFile";
}

/// <summary>
/// Lücke in einem installierten Paket, für die es (noch) kein Update gibt. Nur relevante werden gespeichert
/// (KEV oder EPSS über der Schwelle), sonst würde allein der Linux-Kernel tausende Einträge erzeugen.
/// </summary>
public sealed class HostVulnerability
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public required string CveId { get; set; }

    /// <summary>Quellpaket, z. B. "openssl".</summary>
    public required string Package { get; set; }

    /// <summary>Version des Quellpakets.</summary>
    public string? InstalledVersion { get; set; }
    public DateTime FoundAt { get; set; }
}

/// <summary>
/// Bewusst hingenommene Lücke: wird auf der Sicherheitsseite ausgeblendet. HostId null = auf allen Hosts (auch künftigen).
/// ExpiresAt: danach wieder sichtbar (z. B. "in 30 Tagen nachsehen, ob es einen Fix gibt").
/// </summary>
public sealed class VulnerabilityAck
{
    public long Id { get; set; }
    public required string CveId { get; set; }
    public Guid? HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public string? Note { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public bool Covers(Guid hostId, DateTime now) => (HostId is null || HostId == hostId) && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>Installiertes Programm bzw. Paket eines Hosts (vom Agent, ersetzt bei jedem Inventar-Scan).</summary>
public sealed class InstalledApp
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }

    /// <summary>"dpkg", "snap", "registry", "winget".</summary>
    public required string Source { get; set; }

    /// <summary>Debian-Quellpaket bzw. winget-ID.</summary>
    public string? PackageId { get; set; }
    public string? AvailableVersion { get; set; }

    /// <summary>Version des Debian-Quellpakets (null bei älteren Agents und anderen Quellen).</summary>
    public string? SourceVersion { get; set; }

    /// <summary>Herstellerseite laut Windows-Registry.</summary>
    public string? Url { get; set; }
}

/// <summary>Letzter bekannter Digest eines Image-Tags in der Registry (gecacht, gilt für alle Hosts).</summary>
public sealed class ImageCheck
{
    /// <summary>Normalisierte Referenz, z. B. "docker.io/library/nginx:1.27".</summary>
    public required string Reference { get; set; }
    public string? RemoteDigest { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Für diesen Remote-Digest wurde schon benachrichtigt.</summary>
    public string? NotifiedDigest { get; set; }
}
