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

    [MaxLength(200)]
    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
