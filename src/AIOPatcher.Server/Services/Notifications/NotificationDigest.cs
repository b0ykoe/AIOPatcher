using System.Globalization;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Notifications;

/// <summary>
/// Sammelnachrichten: Kanäle mit <see cref="NotificationChannel.DigestMinutes"/> &gt; 0 bekommen weniger Wichtiges gebündelt.
/// Raus geht die Sammlung, sobald eine Weile nichts Neues kam (dann haben sich alle Hosts gemeldet), spätestens nach der
/// eingestellten Zeit. Wichtiges (<see cref="NotificationChannel.InstantEvents"/>) geht weiter sofort raus.
/// </summary>
public static class NotificationDigest
{
    /// <summary>Erlaubte Sammelzeiten in Minuten (0 = sofort).</summary>
    public static readonly int[] AllowedMinutes = [0, 15, 30, 60, 120, 240];

    /// <summary>Ruhe, nach der die Sammlung rausgeht: 10 Minuten, bei kurzer Sammelzeit weniger.</summary>
    public static TimeSpan Quiet(int digestMinutes) => TimeSpan.FromMinutes(Math.Min(10, Math.Max(1, digestMinutes / 2)));

    public static bool ShouldCollect(NotificationChannel channel, NotificationEvents e) =>
        channel.DigestMinutes > 0 && e != NotificationEvents.None && (channel.InstantEvents & e) == 0;

    /// <summary>Fällig, wenn seit der letzten Nachricht Ruhe ist oder die älteste die Sammelzeit erreicht hat.</summary>
    public static bool IsDue(DateTime oldest, DateTime newest, int digestMinutes, DateTime now) =>
        digestMinutes <= 0 || now - newest >= Quiet(digestMinutes) || now - oldest >= TimeSpan.FromMinutes(digestMinutes);

    public static PendingNotification ToPending(Guid channelId, Notification n, NotificationDetails? d) => new()
    {
        ChannelId = channelId,
        CreatedAt = n.CreatedAt,
        Event = n.Event,
        Severity = n.Severity.ToString(),
        Title = Cut(n.Title, 500),
        Message = Cut(n.Message, 4000),
        HostId = n.HostId,
        HostName = d?.Host,
        SiteId = n.SiteId,
        SiteName = d?.Site,
        LinkPath = n.LinkPath,
    };

    public static Notification FromPending(PendingNotification p) =>
        new(p.Event, p.Title, p.Message, ParseSeverity(p.Severity), p.SiteId, p.HostId, p.LinkPath) { CreatedAt = p.CreatedAt };

    /// <summary>Eine einzelne gesammelte Nachricht geht unverändert raus (mit ihren Details), mehrere als Sammelnachricht.</summary>
    public static (Notification Notification, NotificationDetails Details) Build(IReadOnlyList<PendingNotification> items, TimeZoneInfo zone)
    {
        var sorted = items.OrderBy(i => i.CreatedAt).ToList();
        if (sorted.Count == 1)
        {
            var single = sorted[0];
            return (FromPending(single), new NotificationDetails(single.HostName, single.SiteName, NotificationFormat.EventLabel(single.Event),
                TimeZoneInfo.ConvertTimeFromUtc(single.CreatedAt, zone)));
        }

        var groups = sorted.GroupBy(i => i.Event).OrderByDescending(g => g.Max(i => ParseSeverity(i.Severity))).ThenByDescending(g => g.Count()).ToList();
        var since = TimeZoneInfo.ConvertTimeFromUtc(sorted[0].CreatedAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var summary = string.Join(", ", groups.Take(3).Select(g => $"{g.Count()}× {NotificationFormat.EventLabel(g.Key)}")) + (groups.Count > 3 ? ", …" : "");
        var title = L.T($"{sorted.Count} Meldungen seit {since}: {summary}", $"{sorted.Count} notifications since {since}: {summary}");

        var sections = groups.Select(g => $"{NotificationFormat.EventLabel(g.Key)} ({g.Count()})\n" + string.Join("\n", Lines(g.ToList()).Select(l => "• " + l)));
        var message = string.Join("\n\n", sections);

        var hosts = sorted.Select(i => i.HostId).Distinct().ToList();
        var sites = sorted.Select(i => i.SiteName).Distinct().ToList();
        var links = sorted.Select(i => i.LinkPath).Distinct().ToList();
        var digest = new Notification(sorted.Aggregate(NotificationEvents.None, (all, i) => all | i.Event), title, message,
            sorted.Max(i => ParseSeverity(i.Severity)),
            SiteId: sorted.Select(i => i.SiteId).Distinct().Count() == 1 ? sorted[0].SiteId : null,
            HostId: hosts.Count == 1 ? hosts[0] : null,
            LinkPath: links.Count == 1 ? links[0] : "/")
        {
            CreatedAt = sorted[^1].CreatedAt,
            Items = sorted.Select(FromPending).ToList(),
        };
        var details = new NotificationDetails(hosts.Count == 1 ? sorted[0].HostName : null, sites.Count == 1 ? sites[0] : null,
            L.T("Sammelnachricht", "Digest"), TimeZoneInfo.ConvertTimeFromUtc(sorted[^1].CreatedAt, zone));
        return (digest, details);
    }

    /// <summary>
    /// Zeilen eines Ereignisses: Gleiche Meldung auf mehreren Hosts wird eine Zeile ("python3-jwt: vpn, pivpn, monitor"),
    /// sonst je Meldung "Host: Titel — erste Textzeile".
    /// </summary>
    private static IEnumerable<string> Lines(List<PendingNotification> items)
    {
        foreach (var same in items.GroupBy(i => FirstLine(i.Message)))
        {
            var list = same.ToList();
            if (list.Count > 1 && same.Key.Length > 0 && list.All(i => i.HostName != null))
            {
                yield return $"{same.Key}: {string.Join(", ", list.Select(i => i.HostName).Distinct())}";
                continue;
            }

            foreach (var i in list)
            {
                var title = i.HostName != null && !i.Title.Contains(i.HostName, StringComparison.OrdinalIgnoreCase) ? $"{i.HostName}: {i.Title}" : i.Title;
                var first = FirstLine(i.Message);
                yield return first.Length > 0 && first.Length <= 120 && !title.Contains(first, StringComparison.Ordinal) ? $"{title} — {first}" : title;
            }
        }
    }

    private static string FirstLine(string s) => (s ?? "").Split('\n', 2)[0].Trim();

    private static NotificationSeverity ParseSeverity(string s) => Enum.TryParse<NotificationSeverity>(s, out var v) ? v : NotificationSeverity.Info;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
