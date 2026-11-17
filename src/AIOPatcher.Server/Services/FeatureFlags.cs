using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Schaltbare Funktionen. Riskante sind standardmäßig aus.</summary>
public enum Feature
{
    /// <summary>Notaus-Gegenstück: Auto-Patch und geplante/laufende Abläufe dürfen Jobs starten.</summary>
    Automation,
    PatchJobs,
    Reboots,
    AppUpdates,
    AgentSelfUpdate,
    SnapshotTests,
    OpnsenseUpdates,
    ApiTokens,
    CveLookup,
    ContainerChecks,
    ReleaseCheck,

    /// <summary>Firewall, SSH/RDP und Benutzer erfassen (nur lesen).</summary>
    ComplianceChecks,

    /// <summary>Alter Check-in (Agents vor dem Pull-Protokoll schicken ungefragt Daten). Übergang, bis alle aktualisiert sind.</summary>
    LegacyAgentProtocol,

    /// <summary>Firewall-Regelsätze ausrollen und wiederherstellen.</summary>
    FirewallManagement,

    /// <summary>SSH/RDP-Empfehlungen anwenden und zurücknehmen.</summary>
    RemoteAccessFixes,

    /// <summary>Lokale Benutzer sperren, Adminrechte, Passwörter, SSH-Schlüssel.</summary>
    UserManagement,

    /// <summary>Dauerhaft kaputte Fremd-Paketquellen (apt) vor dem Patchen deaktivieren.</summary>
    AptSourceFixes,

    /// <summary>Laufende Dienste/Container vor und nach Neustarts vergleichen und Fehlendes melden (nur lesen).</summary>
    RebootCheck,

    /// <summary>Eigene Update-Automatik der Hosts (unattended-upgrades, Windows Update) aus dem Dashboard an-/abschalten.</summary>
    AutoUpdateControl,

    /// <summary>Auslastung (CPU, RAM, Datenträger, Container) der Hosts und Proxmox-Gäste erfassen und anzeigen.</summary>
    Metrics,
}

public enum FeatureRisk
{
    Low,
    Medium,
    High,
}

/// <summary>Gespeicherter Schalter (nur Abweichungen vom Standard müssten gespeichert sein, wir speichern jede Änderung).</summary>
public sealed class FeatureToggle
{
    public required string Key { get; set; }
    public bool Enabled { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>
/// Funktionsschalter mit Durchsetzung auf dem Server. Zwei Ebenen:
/// 1. Admin im Dashboard schaltet an/aus (gespeichert in der DB).
/// 2. Wer den Server betreibt, kann Funktionen per Konfiguration endgültig sperren (AIOPatcher__LockedFeatures,
///    bzw. die bisherigen Schalter CveLookupEnabled/ContainerUpdateCheckEnabled/AgentAutoUpdate). Das kann im
///    Dashboard niemand aufheben, auch kein Admin mit gestohlenem Login.
/// </summary>
public sealed class FeatureFlags(AIOPatcherOptions options)
{
    public sealed record Definition(Feature Feature, bool Default, FeatureRisk Risk, string Group);

    public static readonly Definition[] Definitions =
    [
        new(Feature.Automation, true, FeatureRisk.Medium, "hosts"),
        new(Feature.PatchJobs, true, FeatureRisk.Medium, "hosts"),
        new(Feature.Reboots, true, FeatureRisk.Medium, "hosts"),
        new(Feature.AppUpdates, false, FeatureRisk.High, "hosts"),
        new(Feature.AgentSelfUpdate, true, FeatureRisk.Medium, "hosts"),
        new(Feature.LegacyAgentProtocol, true, FeatureRisk.Medium, "hosts"),
        new(Feature.AptSourceFixes, true, FeatureRisk.Low, "hosts"),
        new(Feature.RebootCheck, true, FeatureRisk.Low, "hosts"),
        new(Feature.AutoUpdateControl, true, FeatureRisk.Medium, "hosts"),
        new(Feature.Metrics, true, FeatureRisk.Low, "hosts"),
        new(Feature.SnapshotTests, true, FeatureRisk.Low, "integrations"),
        new(Feature.OpnsenseUpdates, false, FeatureRisk.High, "integrations"),
        new(Feature.ApiTokens, true, FeatureRisk.Medium, "access"),
        new(Feature.CveLookup, true, FeatureRisk.Low, "external"),
        new(Feature.ContainerChecks, true, FeatureRisk.Low, "external"),
        new(Feature.ReleaseCheck, true, FeatureRisk.Low, "external"),
        new(Feature.ComplianceChecks, false, FeatureRisk.Medium, "compliance"),
        new(Feature.FirewallManagement, false, FeatureRisk.High, "compliance"),
        new(Feature.RemoteAccessFixes, false, FeatureRisk.High, "compliance"),
        new(Feature.UserManagement, false, FeatureRisk.High, "compliance"),
    ];

    private readonly Lock _lock = new();
    private Dictionary<Feature, FeatureToggle> _stored = [];

    public async Task LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.FeatureToggles.ToListAsync(ct);
        lock (_lock)
        {
            _stored = rows.Where(r => Enum.TryParse<Feature>(r.Key, out _)).ToDictionary(r => Enum.Parse<Feature>(r.Key));
        }
    }

    /// <summary>Grund, warum die Funktion per Server-Konfiguration gesperrt ist, sonst null.</summary>
    public string? LockReason(Feature feature)
    {
        var locked = (options.LockedFeatures ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (locked.Any(l => string.Equals(l, feature.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return "AIOPatcher__LockedFeatures";
        }

        return feature switch
        {
            Feature.CveLookup when !options.CveLookupEnabled => "AIOPatcher__CveLookupEnabled=false",
            Feature.ContainerChecks when !options.ContainerUpdateCheckEnabled => "AIOPatcher__ContainerUpdateCheckEnabled=false",
            Feature.AgentSelfUpdate when !options.AgentAutoUpdate => "AIOPatcher__AgentAutoUpdate=false",
            Feature.ReleaseCheck when string.IsNullOrWhiteSpace(options.UpdateCheckRepository) => "AIOPatcher__UpdateCheckRepository",
            _ => null,
        };
    }

    public bool IsOn(Feature feature)
    {
        if (LockReason(feature) is not null)
        {
            return false;
        }

        lock (_lock)
        {
            return _stored.TryGetValue(feature, out var toggle) ? toggle.Enabled : Definitions.Single(d => d.Feature == feature).Default;
        }
    }

    public FeatureToggle? Stored(Feature feature)
    {
        lock (_lock)
        {
            return _stored.GetValueOrDefault(feature);
        }
    }

    /// <summary>Schaltet um und speichert. false, wenn die Funktion per Konfiguration gesperrt ist.</summary>
    public async Task<bool> SetAsync(AppDbContext db, Feature feature, bool enabled, string changedBy, CancellationToken ct)
    {
        if (enabled && LockReason(feature) is not null)
        {
            return false;
        }

        var key = feature.ToString();
        var row = await db.FeatureToggles.FindAsync([key], ct);
        if (row is null)
        {
            db.FeatureToggles.Add(row = new FeatureToggle { Key = key });
        }

        row.Enabled = enabled;
        row.ChangedBy = changedBy;
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        lock (_lock)
        {
            // Kopie: die Zeile gehört zum (kurzlebigen) DbContext der Anfrage.
            _stored = new Dictionary<Feature, FeatureToggle>(_stored) { [feature] = new FeatureToggle { Key = key, Enabled = enabled, ChangedBy = changedBy, ChangedAt = row.ChangedAt } };
        }

        return true;
    }

    /// <summary>Meldung für abgelehnte Aktionen.</summary>
    public static string DisabledMessage(Feature feature) =>
        L.T($"„{Label(feature, true)}“ ist unter Einstellungen → Funktionen & Sicherheit abgeschaltet.",
            $"\"{Label(feature, false)}\" is turned off under Settings → Features & security.");

    private static string Label(Feature feature, bool german) => feature switch
    {
        Feature.Automation => german ? "Automatisch patchen" : "Automatic patching",
        Feature.PatchJobs => german ? "Updates installieren" : "Install updates",
        Feature.Reboots => german ? "Neustarts" : "Reboots",
        Feature.AppUpdates => german ? "Programm-Updates per winget" : "Program updates via winget",
        Feature.AgentSelfUpdate => german ? "Agent-Self-Update" : "Agent self-update",
        Feature.SnapshotTests => german ? "Test-Snapshots" : "Test snapshots",
        Feature.OpnsenseUpdates => german ? "OPNsense-Firmware-Update" : "OPNsense firmware update",
        Feature.ApiTokens => german ? "API-Tokens" : "API tokens",
        Feature.CveLookup => german ? "CVE-Abgleich" : "CVE lookup",
        Feature.ContainerChecks => german ? "Container-Prüfung" : "Container checks",
        Feature.ReleaseCheck => german ? "Versionsprüfung" : "Version check",
        Feature.ComplianceChecks => german ? "Compliance-Prüfung" : "Compliance checks",
        Feature.LegacyAgentProtocol => german ? "Alter Agent-Check-in" : "Legacy agent check-in",
        Feature.FirewallManagement => german ? "Firewall verwalten" : "Manage firewalls",
        Feature.RemoteAccessFixes => german ? "SSH/RDP korrigieren" : "Fix SSH/RDP",
        Feature.UserManagement => german ? "Benutzer verwalten" : "Manage users",
        Feature.AptSourceFixes => german ? "Kaputte Paketquellen deaktivieren" : "Disable broken package sources",
        Feature.RebootCheck => german ? "Dienste nach Neustart prüfen" : "Check services after reboot",
        Feature.AutoUpdateControl => german ? "Update-Automatik der Hosts steuern" : "Control hosts' automatic updates",
        Feature.Metrics => german ? "Auslastung erfassen" : "Collect utilization",
        _ => feature.ToString(),
    };
}
