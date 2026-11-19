using System.ComponentModel.DataAnnotations;

namespace AIOPatcher.Server.Data;

/// <summary>
/// Scan-Intervalle (eine Zeile): wie oft Agents nach Updates suchen und wie oft der Server die Registry nach neuen
/// Container-Images fragt. Je Host abweichend über <see cref="ManagedHost.UpdateScanHours"/> bzw.
/// <see cref="ManagedHost.ImageCheckHours"/>.
/// </summary>
public sealed class ScanSettingsRecord
{
    public int Id { get; set; } = 1;

    /// <summary>Update-Scan alle n Stunden (1, 2, 3, 6, 12, 24).</summary>
    public int UpdateScanHours { get; set; } = 6;

    /// <summary>Registry-Abfrage je Image alle n Stunden (1, 2, 3, 6, 12, 24).</summary>
    public int ImageCheckHours { get; set; } = 6;

    /// <summary>Neue Agent-Version höchstens alle n Minuten anbieten (15, 60, 120, 180, 360, 720, 1440).</summary>
    public int AgentUpdateMinutes { get; set; } = 360;

    /// <summary>Weitere Intervalle in Minuten, nur Abweichungen vom Standard (siehe <see cref="Services.Timings"/>).</summary>
    [MaxLength(2000)]
    public string? TimingsJson { get; set; }

    [MaxLength(200)]
    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
