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

    /// <summary>Wählbare Abstände für Agent-Update-Angebote in Minuten.</summary>
    public static readonly int[] AllowedAgentMinutes = [15, 60, 120, 180, 360, 720, 1440];

    private volatile ScanSettingsRecord _current = new();
    private volatile IReadOnlyDictionary<string, int> _timings = new Dictionary<string, int>();

    public ScanSettingsRecord Current => _current;

    public async Task LoadAsync(AppDbContext db, CancellationToken ct) =>
        Use(await db.ScanSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new ScanSettingsRecord());

    public void Use(ScanSettingsRecord settings)
    {
        _timings = Timings.Parse(settings.TimingsJson);
        _current = settings;
    }

    /// <summary>Eingestelltes Intervall (siehe <see cref="Timings"/>), sonst der Standard.</summary>
    public TimeSpan Interval(string key) => _timings.TryGetValue(key, out var minutes) ? TimeSpan.FromMinutes(minutes) : Timings.Default(key);

    /// <summary>Eingestellte Minuten je Schlüssel (nur Abweichungen).</summary>
    public IReadOnlyDictionary<string, int> TimingOverrides => _timings;

    public TimeSpan UpdateScanInterval(ManagedHost host) => TimeSpan.FromHours(host.UpdateScanHours ?? _current.UpdateScanHours);

    /// <param name="hostHours">Abweichender Wert des Hosts, auf dem das Image läuft (null = Standard).</param>
    public TimeSpan ImageCheckInterval(int? hostHours) => TimeSpan.FromHours(hostHours ?? _current.ImageCheckHours);

    /// <summary>Wie oft einem Agent eine neuere Version angeboten wird (läuft ein Update, wartet das nächste Angebot so lange).</summary>
    public TimeSpan AgentUpdateInterval => TimeSpan.FromMinutes(_current.AgentUpdateMinutes);

    public static bool IsAllowedAgentMinutes(int minutes) => AllowedAgentMinutes.Contains(minutes);

    /// <summary>null (= Standard) oder einer der wählbaren Werte.</summary>
    public static bool IsAllowed(int? hours) => hours is null || AllowedHours.Contains(hours.Value);
}
