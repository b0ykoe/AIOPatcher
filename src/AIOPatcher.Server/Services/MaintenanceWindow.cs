using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Services;

/// <summary>Ein konkretes Wartungsfenster (UTC).</summary>
public readonly record struct WindowOccurrence(DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Wartungsfenster pro Host: Wochentage + Startzeit (Ortszeit des Servers) + Dauer.
/// Fenster dürfen über Mitternacht gehen; sie gehören zum Tag, an dem sie beginnen.
/// </summary>
public static class MaintenanceWindow
{
    public const int MinDurationMinutes = 30;
    public const int MaxDurationMinutes = 720;

    /// <summary>Das Fenster, in dem <paramref name="nowUtc"/> gerade liegt, sonst null.</summary>
    public static WindowOccurrence? Current(MaintenanceDays days, TimeOnly start, int durationMinutes, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (days == MaintenanceDays.None || durationMinutes <= 0)
        {
            return null;
        }

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));

        // Heute beginnende Fenster und solche von gestern, die über Mitternacht reichen.
        foreach (var day in new[] { localToday, localToday.AddDays(-1) })
        {
            if (!IsActiveDay(days, day.DayOfWeek))
            {
                continue;
            }

            var occurrence = Occurrence(day, start, durationMinutes, zone);
            if (nowUtc >= occurrence.StartUtc && nowUtc < occurrence.EndUtc)
            {
                return occurrence;
            }
        }

        return null;
    }

    /// <summary>Das laufende oder nächste Fenster (für die Anzeige), sonst null.</summary>
    public static WindowOccurrence? Next(MaintenanceDays days, TimeOnly start, int durationMinutes, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (Current(days, start, durationMinutes, nowUtc, zone) is { } current)
        {
            return current;
        }

        if (days == MaintenanceDays.None || durationMinutes <= 0)
        {
            return null;
        }

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = localToday.AddDays(offset);
            if (!IsActiveDay(days, day.DayOfWeek))
            {
                continue;
            }

            var occurrence = Occurrence(day, start, durationMinutes, zone);
            if (occurrence.StartUtc > nowUtc)
            {
                return occurrence;
            }
        }

        return null;
    }

    public static bool IsActiveDay(MaintenanceDays days, DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => days.HasFlag(MaintenanceDays.Monday),
        DayOfWeek.Tuesday => days.HasFlag(MaintenanceDays.Tuesday),
        DayOfWeek.Wednesday => days.HasFlag(MaintenanceDays.Wednesday),
        DayOfWeek.Thursday => days.HasFlag(MaintenanceDays.Thursday),
        DayOfWeek.Friday => days.HasFlag(MaintenanceDays.Friday),
        DayOfWeek.Saturday => days.HasFlag(MaintenanceDays.Saturday),
        DayOfWeek.Sunday => days.HasFlag(MaintenanceDays.Sunday),
        _ => false,
    };

    private static WindowOccurrence Occurrence(DateOnly day, TimeOnly start, int durationMinutes, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(start, DateTimeKind.Unspecified);

        // Sommerzeit-Umstellung: 02:30 gibt es im Frühjahr nicht -> eine Stunde später beginnen.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        // Im Herbst gibt es 02:30 zweimal -> TimeZoneInfo nimmt die Standardzeit (das zweite Vorkommen); beides ist ok.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return new WindowOccurrence(startUtc, startUtc.AddMinutes(durationMinutes));
    }
}

/// <summary>Zeitzone, in der Wartungsfenster gelten (Option AIOPatcher:TimeZone, sonst Systemzeitzone bzw. TZ im Container).</summary>
public sealed class ServerClock(AIOPatcherOptions options)
{
    public TimeZoneInfo Zone { get; } = Resolve(options.TimeZone);

    public string ZoneName => Zone.Id;

    private static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Windows ohne ICU kennt IANA-Namen evtl. nicht: auf die Windows-ID ausweichen.
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)
                ? TimeZoneInfo.FindSystemTimeZoneById(windowsId)
                : TimeZoneInfo.Local;
        }
    }
}
