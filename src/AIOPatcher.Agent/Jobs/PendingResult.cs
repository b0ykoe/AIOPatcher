using System.Text.Json;
using System.Text.Json.Serialization;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Jobs;

/// <summary>
/// Ergebnis eines Jobs, das erst nach dem Neustart gemeldet wird. Liegt im Agent-Verzeichnis,
/// damit es den Neustart überlebt.
/// </summary>
public sealed record PendingResult(
    Guid JobId,
    JobOutcome Outcome,
    string Summary,
    DateTime RebootRequestedAtUtc,
    bool AwaitReboot = true,
    int ForceAttempts = 0,
    DateTime? LastForcedAtUtc = null)
{
    public const string FileName = "pending-result.json";

    /// <summary>So lange warten wir nach dem Auslösen auf den tatsächlichen Neustart, bevor wir aufgeben.</summary>
    public static readonly TimeSpan RebootGracePeriod = TimeSpan.FromMinutes(15);

    /// <summary>Kam der Neustart so lange nicht, wird er sofort erzwungen (höchstens <see cref="MaxForceAttempts"/>-mal).</summary>
    public static readonly TimeSpan ForceRebootAfter = TimeSpan.FromMinutes(3);

    public const int MaxForceAttempts = 2;

    /// <summary>
    /// Neustart erzwingen? Ja, wenn er seit dem Auslösen (bzw. dem letzten Erzwingen) 3 Minuten ausblieb, z. B. weil
    /// jemand ihn abgebrochen hat oder ein Programm blockierte.
    /// </summary>
    public bool ShouldForceReboot(DateTime bootTimeUtc, DateTime nowUtc) =>
        AwaitReboot && !HasRebooted(bootTimeUtc) && ForceAttempts < MaxForceAttempts
        && nowUtc - (LastForcedAtUtc ?? RebootRequestedAtUtc) >= ForceRebootAfter;

    public static PendingResult? Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), PendingResultJsonContext.Default.PendingResult)
            : null;
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, PendingResultJsonContext.Default.PendingResult));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string directory) => File.Delete(Path.Combine(directory, FileName));

    /// <summary>Hat seit dem Auslösen wirklich ein Neustart stattgefunden? (Bootzeit liegt danach.)</summary>
    public bool HasRebooted(DateTime bootTimeUtc) => bootTimeUtc > RebootRequestedAtUtc.AddSeconds(-5);

    public static DateTime CurrentBootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PendingResult))]
public sealed partial class PendingResultJsonContext : JsonSerializerContext;
