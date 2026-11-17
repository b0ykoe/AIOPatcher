using System.Text.Json;
using System.Text.Json.Serialization;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Maintenance;

/// <summary>Laufendes Self-Update: neue Version ist installiert, hat sich aber noch nicht beim Server gemeldet.</summary>
public sealed record PendingSelfUpdate(string FromVersion, string ToVersion, DateTime StartedAtUtc);

/// <summary>
/// Dateien im Agent-Verzeichnis rund ums Self-Update. Alles liegt auf der Platte, weil dazwischen Neustarts liegen
/// (und im Fehlerfall ein Rollback durch den Wächter, der den Agent gar nicht braucht).
/// <list type="bullet">
/// <item><c>update-pending.json</c>: Update läuft. Existiert die Datei nach der Wartezeit noch, stellt der Wächter die alte Version her.</item>
/// <item><c>update-result.json</c>: Ergebnis für den Server, wird beim nächsten erfolgreichen Check-in gemeldet und gelöscht.</item>
/// <item><c>update-key.pub</c>: Update-Schlüssel des Servers (erster gesehener gilt).</item>
/// <item><c>last-checkin</c>: Zeitpunkt des letzten erfolgreichen Check-ins (für den Installationscheck).</item>
/// </list>
/// </summary>
public static class UpdateState
{
    public const string PendingFile = "update-pending.json";
    public const string ResultFile = "update-result.json";
    public const string ServerKeyFile = "update-key.pub";
    public const string LastCheckinFile = "last-checkin";

    /// <summary>So lange hat eine neue Version Zeit, sich beim Server zu melden, sonst Rollback.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(5);

    public static PendingSelfUpdate? LoadPending(string directory) => Read(directory, PendingFile, UpdateStateJsonContext.Default.PendingSelfUpdate);

    public static void SavePending(string directory, PendingSelfUpdate pending) =>
        Write(directory, PendingFile, JsonSerializer.Serialize(pending, UpdateStateJsonContext.Default.PendingSelfUpdate));

    public static AgentUpdateReport? LoadResult(string directory) => Read(directory, ResultFile, UpdateStateJsonContext.Default.AgentUpdateReport);

    public static void SaveResult(string directory, AgentUpdateReport report) =>
        Write(directory, ResultFile, JsonSerializer.Serialize(report, UpdateStateJsonContext.Default.AgentUpdateReport));

    public static string? LoadServerKey(string directory)
    {
        var path = Path.Combine(directory, ServerKeyFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } key ? key : null : null;
    }

    public static void SaveServerKey(string directory, string key) => Write(directory, ServerKeyFile, key);

    public static void TouchLastCheckin(string directory) =>
        Write(directory, LastCheckinFile, $"{DateTime.UtcNow:O} {AgentState.AgentVersion}");

    /// <summary>Neue Registrierung (evtl. anderer Server): gemerkten Schlüssel und Update-Reste vergessen.</summary>
    public static void Reset(string directory)
    {
        foreach (var file in new[] { PendingFile, ResultFile, ServerKeyFile })
        {
            Delete(Path.Combine(directory, file));
        }
    }

    /// <summary>
    /// Beim Start: Was ist aus einem laufenden Update geworden?
    /// Marker mit unserer Version = neue Version läuft, wartet auf Bestätigung (Rückgabe).
    /// Marker mit anderer Version = wir sind die alte Version, der Wächter hat zurückgerollt: Ergebnis vormerken.
    /// </summary>
    public static PendingSelfUpdate? Resume(string directory, string currentVersion, string? backupPath)
    {
        var pending = LoadPending(directory);
        if (pending is null)
        {
            return null;
        }

        if (string.Equals(pending.ToVersion, currentVersion, StringComparison.Ordinal))
        {
            return pending;
        }

        SaveResult(directory, new AgentUpdateReport(pending.FromVersion, pending.ToVersion, AgentUpdateOutcome.RolledBack,
            $"Version {pending.ToVersion} did not report to the server within {ConfirmTimeout.TotalMinutes:0} minutes (crashed or hung). Previous version restored.",
            DateTime.UtcNow));
        Delete(Path.Combine(directory, PendingFile));
        if (backupPath is not null)
        {
            Delete(backupPath);
        }

        return null;
    }

    /// <summary>Erster erfolgreicher Check-in der neuen Version: Update ist bestätigt.</summary>
    public static void Confirm(string directory, string? backupPath)
    {
        Delete(Path.Combine(directory, PendingFile));
        if (backupPath is not null)
        {
            Delete(backupPath);
        }
    }

    public static void DeleteResult(string directory) => Delete(Path.Combine(directory, ResultFile));

    private static T? Read<T>(string directory, string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), type);
        }
        catch (JsonException)
        {
            Delete(path); // kaputte Datei nicht ewig mitschleppen
            return null;
        }
    }

    private static void Write(string directory, string file, string content)
    {
        var path = Path.Combine(directory, file);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content + "\n");
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PendingSelfUpdate))]
[JsonSerializable(typeof(AgentUpdateReport))]
[JsonSerializable(typeof(Dictionary<string, int?>))]
public sealed partial class UpdateStateJsonContext : JsonSerializerContext;
