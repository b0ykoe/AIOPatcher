namespace AIOPatcher.Server.Data;

/// <summary>Eine Messung der Auslastung (alle 5 Minuten, 7 Tage aufbewahrt) für die Verlaufskurven.</summary>
public sealed class HostMetricSample
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public DateTime At { get; set; }

    /// <summary>CPU in Prozent (Mittel seit der vorigen Messung).</summary>
    public float Cpu { get; set; }

    /// <summary>RAM belegt in Prozent.</summary>
    public float Memory { get; set; }

    /// <summary>Vollster Datenträger in Prozent (null = keiner gemeldet).</summary>
    public float? Disk { get; set; }
}
