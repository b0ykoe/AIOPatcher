using System.Text.Json;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Weitere einstellbare Intervalle (Einstellungen → Allgemein → Intervalle): wie oft der Server Daten von Agents anfordert
/// und Integrationen abfragt. Gespeichert als JSON in <see cref="Data.ScanSettingsRecord.TimingsJson"/>; nur Abweichungen
/// vom Standard. Untergrenzen schützen Hosts und fremde APIs (GitHub ohne Anmeldung: 60 Abfragen pro Stunde).
/// </summary>
public static class Timings
{
    public const string ReleaseCheck = "releaseCheck";
    public const string Metrics = "metrics";
    public const string System = "system";
    public const string Containers = "containers";
    public const string Inventory = "inventory";
    public const string Monitoring = "monitoring";
    public const string Backups = "backups";
    public const string Opnsense = "opnsense";
    public const string Sharing = "sharing";

    /// <param name="Allowed">Wählbare Werte in Minuten.</param>
    public sealed record Definition(string Key, int DefaultMinutes, int[] Allowed);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(Metrics, 5, [1, 2, 5, 10, 15, 30]),
        new(System, 15, [5, 15, 30, 60]),
        new(Containers, 30, [5, 15, 30, 60, 120]),
        new(Inventory, 360, [60, 180, 360, 720, 1440]),
        new(Monitoring, 60, [15, 30, 60, 180, 360]),
        new(Backups, 15, [5, 15, 30, 60]),
        new(Opnsense, 30, [10, 30, 60, 180]),
        new(Sharing, 5, [2, 5, 15, 30, 60]),
        new(ReleaseCheck, 15, [15, 30, 60, 360, 1440]),
    ];

    /// <summary>Periode des Timers vor dem nächsten Warten auf den eingestellten Wert setzen (für "while (Retime(...) && await ...)").</summary>
    public static bool Retime(PeriodicTimer timer, ScanSchedule? schedule, string key)
    {
        var period = schedule?.Interval(key) ?? Default(key);
        if (timer.Period != period)
        {
            timer.Period = period;
        }

        return true;
    }

    public static Definition? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    public static TimeSpan Default(string key) => TimeSpan.FromMinutes(Find(key)?.DefaultMinutes ?? 60);

    public static bool IsAllowed(string key, int minutes) => Find(key) is { } d && d.Allowed.Contains(minutes);

    /// <summary>Gespeicherte Abweichungen lesen; Unbekanntes und nicht (mehr) erlaubte Werte fallen weg.</summary>
    public static Dictionary<string, int> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [])
                .Where(kv => IsAllowed(kv.Key, kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Wert setzen (Standard = Eintrag entfernen) und als JSON zurückgeben (null = alles Standard).</summary>
    public static string? With(string? json, string key, int minutes)
    {
        var values = Parse(json);
        if (Find(key)?.DefaultMinutes == minutes)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = minutes;
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
