using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Data;

// Site-Sharing zwischen AIOPatcher-Instanzen ("Föderation").
// A = Instanz, die eine Site teilt (Besitzer der Hosts). B = Instanz, in deren Dashboard die Site erscheint.
// B holt alles bei A ab (Pull über die externe Dashboard-Adresse von A), A meldet sich nie bei B.

/// <summary>Eigene Identität dieser Instanz (eine Zeile): stabile ID und ein Name, den Gegenstellen sehen.</summary>
public sealed class ServerIdentityRecord
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Was B auf einer geteilten Site darf (aufsteigend, jede Stufe enthält die vorige).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShareLevel>))]
public enum ShareLevel
{
    /// <summary>Hosts, Updates, Jobs ansehen.</summary>
    View,

    /// <summary>Zusätzlich: Scan anstoßen.</summary>
    Scan,

    /// <summary>Zusätzlich: patchen (ohne Neustart).</summary>
    Patch,

    /// <summary>Zusätzlich: neu starten bzw. patchen mit Neustart.</summary>
    Reboot,
}

/// <summary>Zusätzliche Daten, die über das Grundgerüst (Hosts, Zähler, Jobs) hinaus geteilt werden.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<ShareData>))]
public enum ShareData
{
    None = 0,

    /// <summary>Liste der anstehenden Updates mit Paketnamen, Versionen und CVEs.</summary>
    UpdateDetails = 1,

    /// <summary>Log-Zeilen der Jobs.</summary>
    JobLogs = 2,
}

/// <summary>
/// Einladung (Seite A): Link + Code. Rechte, Datenumfang und Laufzeit stehen hier fest und gehen beim Annehmen
/// unverändert in die Freigabe über. Nach FederationProtocol.MaxFailedCodes falschen Codes ist sie verbrannt.
/// </summary>
public sealed class ShareInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>SHA-256 des Codes (Base64). Der Code wird nur beim Anlegen einmal angezeigt.</summary>
    public required string CodeHash { get; set; }

    public ShareLevel Level { get; set; }
    public ShareData Data { get; set; }

    /// <summary>Laufzeit der Freigabe ab Annahme in Tagen, null = unbegrenzt.</summary>
    public int? ShareDays { get; set; }

    public DateTime ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? BurnedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? UsedByPeerId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Gegenstelle (Seite A): eine Instanz B, die mit einem Zertifikat unserer CA abruft.</summary>
public sealed class FederationPeer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Instanz-ID laut B (nur zur Anzeige, nicht zur Anmeldung).</summary>
    public Guid InstanceId { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }

    public required string CertificateThumbprint { get; set; }
    public DateTime CertificateExpiresAt { get; set; }

    /// <summary>Erneuertes Zertifikat, das B noch nicht benutzt hat. Bis dahin gilt auch das alte.</summary>
    public string? PendingCertificateThumbprint { get; set; }
    public DateTime? PendingCertificateExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public string? LastSeenIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    public List<SiteShare> Shares { get; set; } = [];
}

/// <summary>Freigabe (Seite A): eine Site für eine Gegenstelle. Nach dem Anlegen unveränderlich, nur beendbar.</summary>
public sealed class SiteShare
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PeerId { get; set; }
    public FederationPeer? Peer { get; set; }
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }

    public ShareLevel Level { get; set; }
    public ShareData Data { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public Guid InviteId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAccessAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndedBy { get; set; }
}

/// <summary>Instanz A, bei der wir (Seite B) geteilte Sites abrufen.</summary>
public sealed class RemoteInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public required string Name { get; set; }

    /// <summary>Externe Dashboard-Adresse von A, z. B. https://patch.example.com (aus dem Einladungslink).</summary>
    public required string BaseUrl { get; set; }

    /// <summary>SHA-256 der CA von A (aus dem Einladungslink, gepinnt).</summary>
    public required string CaFingerprint { get; set; }
    public required string CaCertificatePem { get; set; }

    /// <summary>Unser Schlüssel (PKCS#8, Base64), per Data Protection verschlüsselt.</summary>
    public required string ProtectedKey { get; set; }
    public required string CertificatePem { get; set; }
    public DateTime CertificateExpiresAt { get; set; }

    public string? Version { get; set; }

    /// <summary>Fähigkeiten von A (kommagetrennt), siehe FederationProtocol.Capabilities.</summary>
    public string? Capabilities { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }

    public List<RemoteSite> Sites { get; set; } = [];
}

/// <summary>Geteilte Site bei uns (Seite B): letzter Stand als JSON, Details holt das Dashboard live.</summary>
public sealed class RemoteSite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RemoteInstanceId { get; set; }
    public RemoteInstance? Instance { get; set; }

    public Guid ShareId { get; set; }
    public Guid RemoteSiteId { get; set; }
    public required string Name { get; set; }

    /// <summary>Eigener Anzeigename bei uns (null = Name von A).</summary>
    public string? LocalName { get; set; }

    public ShareLevel Level { get; set; }
    public ShareData Data { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Letzter Stand (JSON, FederationProtocol.SiteSnapshot).</summary>
    public string? SnapshotJson { get; set; }
    public DateTime? SnapshotAt { get; set; }

    /// <summary>Jobs bis zu diesem Zeitpunkt (Ende) sind für Benachrichtigungen schon ausgewertet.</summary>
    public DateTime? JobsCheckedUntil { get; set; }

    /// <summary>A hat die Freigabe beendet oder sie ist abgelaufen.</summary>
    public DateTime? EndedAt { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class FederationModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<ServerIdentityRecord>(e =>
        {
            e.ToTable("ServerIdentity");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
        });

        b.Entity<ShareInvite>(e =>
        {
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.Data).HasConversion<int>();
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });

        b.Entity<FederationPeer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Version).HasMaxLength(32);
            e.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.PendingCertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.LastSeenIp).HasMaxLength(64);
            e.Property(x => x.RevokedBy).HasMaxLength(200);
            e.HasIndex(x => x.CertificateThumbprint).IsUnique();
            e.HasIndex(x => x.PendingCertificateThumbprint);
        });

        b.Entity<SiteShare>(e =>
        {
            e.HasOne(x => x.Peer).WithMany(p => p.Shares).HasForeignKey(x => x.PeerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Data).HasConversion<int>();
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.EndedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.PeerId, x.SiteId });
        });

        b.Entity<RemoteInstance>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.BaseUrl).HasMaxLength(300);
            e.Property(x => x.CaFingerprint).HasMaxLength(128);
            e.Property(x => x.CaCertificatePem).HasMaxLength(8000);
            e.Property(x => x.ProtectedKey).HasMaxLength(4000);
            e.Property(x => x.CertificatePem).HasMaxLength(8000);
            e.Property(x => x.Version).HasMaxLength(32);
            e.Property(x => x.Capabilities).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(500);
        });

        b.Entity<RemoteSite>(e =>
        {
            e.HasOne(x => x.Instance).WithMany(i => i.Sites).HasForeignKey(x => x.RemoteInstanceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.LocalName).HasMaxLength(100);
            e.Property(x => x.Data).HasConversion<int>();
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.ShareId).IsUnique();
        });
    }
}
