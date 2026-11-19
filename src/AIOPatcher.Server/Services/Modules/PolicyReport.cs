using System.Text.Json;
using AIOPatcher.Contracts;

namespace AIOPatcher.Server.Services.Modules;

/// <summary>Gemeldete lokale Policy eines Hosts (nur Anzeige) als JSON in <see cref="Data.ManagedHost.ReportedPolicy"/>.</summary>
public static class PolicyReport
{
    private const int MaxLength = 2000;

    /// <summary>Für die Datenbank; Werte werden begrenzt (AllowPatch kurz, höchstens 32 gesperrte Module).</summary>
    public static string? Serialize(AgentPolicyReport report)
    {
        var clean = report with
        {
            AllowPatch = report.AllowPatch is "All" or "Security" or "None" ? report.AllowPatch : "All",
            DisabledModules = report.DisabledModules?.Where(m => m.Length <= 32).Take(32).ToList(),
        };
        var json = JsonSerializer.Serialize(clean, PullJsonContext.Default.AgentPolicyReport);
        return json.Length <= MaxLength ? json : null;
    }

    public static AgentPolicyReport? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, PullJsonContext.Default.AgentPolicyReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
