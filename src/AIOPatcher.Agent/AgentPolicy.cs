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
public sealed record AgentPolicy(PatchPermission AllowPatch = PatchPermission.All, bool AllowReboot = true, bool AllowSelfUpdate = true)
{
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
