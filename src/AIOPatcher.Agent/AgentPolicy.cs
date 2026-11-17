using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOPatcher.Agent;

[JsonConverter(typeof(JsonStringEnumConverter<PatchPermission>))]
public enum PatchPermission
{
    None,
    Security,
    All,
}

/// <summary>
/// Lokale Policy auf dem Host (policy.json im Agent-Verzeichnis). Der Server kann sie nicht ändern:
/// Selbst ein kompromittierter Server kann nicht mehr anstoßen, als hier erlaubt ist.
/// </summary>
/// <param name="AllowSelfUpdate">Signierte Agent-Updates vom Server automatisch installieren.</param>
/// <param name="AllowAppUpdates">Programm-Updates per winget erlauben (zusätzlich muss der Server sie freigeben).</param>
/// <param name="TrustServerUpdates">
/// Updates annehmen, die der AIOPatcher-Server selbst signiert hat (Image ohne Release-Schlüssel). false = nur noch per
/// Installationsbefehl aktualisieren. Agents mit eingebautem Release-Schlüssel ignorieren Server-Signaturen ohnehin.
/// </param>
/// <param name="AllowSystemChanges">
/// Firewall, SSH/RDP-Einstellungen und lokale Benutzer ändern lassen. Ab Werk aus: Diese Aktionen können einen Host
/// aussperren oder übernehmen, daher muss sie jeder Host selbst freigeben (zusätzlich zu den Schaltern im Dashboard).
/// </param>
public sealed record AgentPolicy(PatchPermission AllowPatch = PatchPermission.All, bool AllowReboot = true, bool AllowSelfUpdate = true, bool AllowAppUpdates = true, bool TrustServerUpdates = true,
    bool AllowSystemChanges = false)
{
    /// <summary>Einzelnen Schalter in policy.json setzen (Befehl "policy set"), übrige Einträge bleiben.</summary>
    public static AgentPolicy Set(string directory, string key, string value)
    {
        var policy = Load(directory);
        bool Bool() => bool.TryParse(value, out var b) ? b : throw new ArgumentException($"'{value}' is not true/false.");
        policy = key.ToLowerInvariant() switch
        {
            "allowpatch" => policy with { AllowPatch = Enum.TryParse<PatchPermission>(value, true, out var p) ? p : throw new ArgumentException("allowPatch: None, Security or All.") },
            "allowreboot" => policy with { AllowReboot = Bool() },
            "allowselfupdate" => policy with { AllowSelfUpdate = Bool() },
            "allowappupdates" => policy with { AllowAppUpdates = Bool() },
            "trustserverupdates" => policy with { TrustServerUpdates = Bool() },
            "allowsystemchanges" => policy with { AllowSystemChanges = Bool() },
            _ => throw new ArgumentException($"Unknown policy key '{key}'."),
        };
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(policy, AgentPolicyJsonContext.Default.AgentPolicy) + "\n");
        return policy;
    }

    public const string FileName = "policy.json";

    public static AgentPolicy Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new AgentPolicy();
        }

        return JsonSerializer.Deserialize(File.ReadAllText(path), AgentPolicyJsonContext.Default.AgentPolicy)
               ?? throw new InvalidDataException($"{FileName} is empty.");
    }

    public static void WriteDefaultIfMissing(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new AgentPolicy(), AgentPolicyJsonContext.Default.AgentPolicy) + "\n");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AgentPolicy))]
public sealed partial class AgentPolicyJsonContext : JsonSerializerContext;
