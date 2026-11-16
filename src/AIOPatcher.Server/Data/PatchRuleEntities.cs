namespace AIOPatcher.Server.Data;

/// <summary>
/// Patchpause: in diesem Zeitraum startet weder Auto-Patch noch ein geplanter Ablauf (z. B. Jahresabschluss,
/// Urlaub, Release-Woche). Manuelle Jobs bleiben möglich, das Dashboard warnt aber.
/// </summary>
public sealed class PatchFreeze
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>null = alle Sites.</summary>
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }

    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Zurückgehaltenes Update: wird bei Patch-Jobs nicht installiert. Muster siehe <see cref="AIOPatcher.Contracts.HoldPatterns"/>.
/// Gilt für einen Host, eine Site oder (beides leer) überall.
/// </summary>
public sealed class PackageHold
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Pattern { get; set; }

    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }
    public Guid? HostId { get; set; }
    public ManagedHost? Host { get; set; }

    public string? Reason { get; set; }

    /// <summary>Optional: danach gilt die Regel nicht mehr (z. B. "bis der Hersteller das Update repariert").</summary>
    public DateTime? ExpiresAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
