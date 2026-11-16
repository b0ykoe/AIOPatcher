using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services;

/// <summary>Auto-Patch-Einstellungen eines Hosts, wie sie das Dashboard schickt.</summary>
public sealed record AutoPatchRequest(bool Enabled, PatchScope Scope, bool AllowReboot, int Days, string Start, int DurationMinutes);

/// <summary>Prüft und übernimmt Auto-Patch-Einstellungen (für einen oder viele Hosts gleich).</summary>
public static class AutoPatchSettings
{
    /// <summary>Prüft die Eingabe unabhängig vom Host. Leeres Ergebnis = gültig.</summary>
    public static Dictionary<string, string[]> Validate(AutoPatchRequest request, out TimeOnly start)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Days is < 0 or > (int)MaintenanceDays.Daily)
        {
            errors["days"] = [L.T("Ungültige Wochentage.", "Invalid weekdays.")];
        }
        else if (request.Enabled && request.Days == 0)
        {
            errors["days"] = [L.T("Mindestens einen Wochentag wählen.", "Select at least one weekday.")];
        }

        if (!TimeOnly.TryParseExact(request.Start ?? "", ["HH:mm", "HH:mm:ss"], out start))
        {
            errors["start"] = [L.T("Startzeit im Format HH:mm angeben.", "Enter the start time as HH:mm.")];
        }

        if (request.DurationMinutes is < MaintenanceWindow.MinDurationMinutes or > MaintenanceWindow.MaxDurationMinutes)
        {
            errors["durationMinutes"] = [L.T(
                $"Dauer zwischen {MaintenanceWindow.MinDurationMinutes} und {MaintenanceWindow.MaxDurationMinutes} Minuten.",
                $"Duration must be between {MaintenanceWindow.MinDurationMinutes} and {MaintenanceWindow.MaxDurationMinutes} minutes.")];
        }

        return errors;
    }

    /// <summary>Gesperrte Hosts bekommen kein aktives Auto-Patch.</summary>
    public static string? CannotApply(ManagedHost host, AutoPatchRequest request) =>
        request.Enabled && host.IsRevoked ? L.T("Host ist gesperrt.", "Host is revoked.") : null;

    public static void Apply(ManagedHost host, AutoPatchRequest request, TimeOnly start)
    {
        host.AutoPatchEnabled = request.Enabled;
        host.AutoPatchScope = request.Scope;
        host.AutoRebootAllowed = request.AllowReboot;
        host.MaintenanceDays = (MaintenanceDays)request.Days;
        host.MaintenanceStart = start;
        host.MaintenanceDurationMinutes = request.DurationMinutes;
    }

    public static string Describe(ManagedHost host, AutoPatchRequest request, TimeOnly start) =>
        $"{host.Name}: {(request.Enabled ? "on" : "off")}, {request.Scope}, reboot {(request.AllowReboot ? "yes" : "no")}, days {request.Days}, {start:HH\\:mm} for {request.DurationMinutes} min";
}
