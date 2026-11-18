using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Scan-Intervalle (gecacht, gelten ohne Neustart): Standard aus den Einstellungen, je Host überschreibbar.
/// Update-Scan: <see cref="Modules.UpdatesServerModule"/>; Registry-Abfrage: <see cref="Security.ContainerUpdateService"/>.
/// </summary>
public sealed class ScanSchedule
{
    /// <summary>Wählbare Intervalle in Stunden.</summary>
    public static readonly int[] AllowedHours = [1, 2, 3, 6, 12, 24];

    public const int DefaultHours = 6;

    private volatile ScanSettingsRecord _current = new();

    public ScanSettingsRecord Current => _current;

    public async Task LoadAsync(AppDbContext db, CancellationToken ct) =>
        _current = await db.ScanSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new ScanSettingsRecord();

    public void Use(ScanSettingsRecord settings) => _current = settings;

    public TimeSpan UpdateScanInterval(ManagedHost host) => TimeSpan.FromHours(host.UpdateScanHours ?? _current.UpdateScanHours);

    /// <param name="hostHours">Abweichender Wert des Hosts, auf dem das Image läuft (null = Standard).</param>
    public TimeSpan ImageCheckInterval(int? hostHours) => TimeSpan.FromHours(hostHours ?? _current.ImageCheckHours);

    /// <summary>null (= Standard) oder einer der wählbaren Werte.</summary>
    public static bool IsAllowed(int? hours) => hours is null || AllowedHours.Contains(hours.Value);
}
