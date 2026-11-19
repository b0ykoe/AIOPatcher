using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Notifications;

/// <summary>Zusatzangaben für die Stile "Ausführlich" und "Schick": Host, Site, Ereignis und Zeit (Zeitzone des Servers).</summary>
public sealed record NotificationDetails(string? Host, string? Site, string Event, DateTime LocalTime);

/// <summary>
/// Inhalt einer Benachrichtigung je Stil und Kanal (reine Funktionen, testbar). Kurz: Titel und Link. Standard: Titel,
/// Text, Link. Ausführlich: dazu Host, Site, Ereignis, Uhrzeit. Schick: die eigene Formatierung des Dienstes (Telegram-HTML
/// mit Knopf, Discord-Embed, Slack-Blocks, HTML-Mail, ntfy-Markdown mit Aktion).
/// </summary>
public static class NotificationFormat
{
    public static string Emoji(NotificationSeverity s) => s switch
    {
        NotificationSeverity.Critical => "🚨",
        NotificationSeverity.Warning => "⚠️",
        NotificationSeverity.Success => "✅",
        _ => "ℹ️",
    };

    /// <summary>Farbe für Discord-Embeds bzw. die HTML-Mail (rot, orange, grün, blau).</summary>
    public static int Color(NotificationSeverity s) => s switch
    {
        NotificationSeverity.Critical => 0xE03131,
        NotificationSeverity.Warning => 0xF08C00,
        NotificationSeverity.Success => 0x2F9E44,
        _ => 0x1C7ED6,
    };

    public static string EventLabel(NotificationEvents e) => e switch
    {
        NotificationEvents.JobFailed => L.T("Job fehlgeschlagen", "Job failed"),
        NotificationEvents.JobSucceeded => L.T("Job erfolgreich", "Job succeeded"),
        NotificationEvents.HostOffline => L.T("Host offline", "Host offline"),
        NotificationEvents.HostOnline => L.T("Host wieder online", "Host back online"),
        NotificationEvents.NewSecurityUpdates => L.T("Neue Security-Updates", "New security updates"),
        NotificationEvents.RunNeedsAttention => L.T("Patch-Plan braucht dich", "Patch plan needs you"),
        NotificationEvents.RunFinished => L.T("Patch-Plan beendet", "Patch plan finished"),
        NotificationEvents.KevDetected => L.T("Aktiv ausgenutzte Lücke (KEV)", "Actively exploited vulnerability (KEV)"),
        NotificationEvents.ContainerUpdates => L.T("Container-Updates", "Container updates"),
        NotificationEvents.CertificateExpiring => L.T("Zertifikat läuft ab", "Certificate expiring"),
        NotificationEvents.AgentUpdateFailed => L.T("Agent-Update fehlgeschlagen", "Agent update failed"),
        NotificationEvents.RebootCheckFailed => L.T("Dienste fehlen nach Neustart", "Services missing after reboot"),
        NotificationEvents.ContainerStopped => L.T("Container gestoppt", "Container stopped"),
        NotificationEvents.MonitoringDisconnected => L.T("Monitoring getrennt", "Monitoring disconnected"),
        NotificationEvents.BackupProblem => L.T("Backup-Problem", "Backup problem"),
        NotificationEvents.SharingActivity => L.T("Site-Sharing", "Site sharing"),
        _ => L.T("Test", "Test"),
    };

    /// <summary>"Host web01 · Site Homelab · Job fehlgeschlagen · 2026-11-19 22:46".</summary>
    public static string DetailLine(NotificationDetails? d)
    {
        if (d is null)
        {
            return "";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Host)) parts.Add($"{L.T("Host", "Host")} {d.Host}");
        if (!string.IsNullOrWhiteSpace(d.Site)) parts.Add($"{L.T("Site", "Site")} {d.Site}");
        parts.Add(d.Event);
        parts.Add(d.LocalTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>Text ohne Formatierung (Standard, Kurz, Ausführlich) für Telegram, Discord, Slack und Mail.</summary>
    public static string PlainBody(NotificationStyle style, Notification n, NotificationDetails? d) => style switch
    {
        NotificationStyle.Short => "",
        NotificationStyle.Detailed => Join(n.Message, DetailLine(d)),
        _ => n.Message,
    };

    public static string OpenLabel => L.T("In AIOPatcher öffnen", "Open in AIOPatcher");

    // --- Telegram -----------------------------------------------------------------------------------

    public static JsonObject Telegram(NotificationStyle style, Notification n, NotificationDetails? d, string chatId, string link, bool withButton)
    {
        var body = new JsonObject { ["chat_id"] = chatId, ["disable_web_page_preview"] = true };
        if (style != NotificationStyle.Rich)
        {
            body["text"] = style == NotificationStyle.Short
                ? $"{Emoji(n.Severity)} {n.Title}\n{link}"
                : Join($"{Emoji(n.Severity)} {n.Title}", PlainBody(style, n, d), link);
            return body;
        }

        static string H(string s) => WebUtility.HtmlEncode(s);
        var text = Join($"<b>{Emoji(n.Severity)} {H(n.Title)}</b>", H(n.Message), d is null ? "" : $"<i>{H(DetailLine(d))}</i>");
        body["parse_mode"] = "HTML";
        if (withButton)
        {
            body["reply_markup"] = new JsonObject
            {
                ["inline_keyboard"] = new JsonArray(new JsonArray(new JsonObject { ["text"] = OpenLabel, ["url"] = link })),
            };
        }
        else
        {
            text = Join(text, $"<a href=\"{H(link)}\">{H(OpenLabel)}</a>");
        }

        body["text"] = text;
        return body;
    }

    // --- Discord ------------------------------------------------------------------------------------

    public static JsonObject Discord(NotificationStyle style, Notification n, NotificationDetails? d, string link)
    {
        if (style == NotificationStyle.Short)
        {
            return new JsonObject { ["content"] = $"**{Emoji(n.Severity)} {n.Title}** <{link}>" };
        }

        if (style != NotificationStyle.Rich)
        {
            return new JsonObject { ["content"] = Join($"**{Emoji(n.Severity)} {n.Title}**", PlainBody(style, n, d), link) };
        }

        var fields = new JsonArray();
        if (!string.IsNullOrWhiteSpace(d?.Host)) fields.Add(Field(L.T("Host", "Host"), d.Host));
        if (!string.IsNullOrWhiteSpace(d?.Site)) fields.Add(Field(L.T("Site", "Site"), d.Site));
        if (d is not null) fields.Add(Field(L.T("Ereignis", "Event"), d.Event));
        return new JsonObject
        {
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = Truncate($"{Emoji(n.Severity)} {n.Title}", 256),
                ["description"] = Truncate(n.Message, 4000),
                ["url"] = link,
                ["color"] = Color(n.Severity),
                ["fields"] = fields,
                ["timestamp"] = n.CreatedAt.ToString("O"),
                ["footer"] = new JsonObject { ["text"] = "AIOPatcher" },
            }),
        };

        static JsonObject Field(string name, string value) => new() { ["name"] = name, ["value"] = Truncate(value, 1024), ["inline"] = true };
    }

    // --- Slack --------------------------------------------------------------------------------------

    public static JsonObject Slack(NotificationStyle style, Notification n, NotificationDetails? d, string link)
    {
        var fallback = $"{Emoji(n.Severity)} {n.Title}";
        if (style == NotificationStyle.Short)
        {
            return new JsonObject { ["text"] = $"*{fallback}* <{link}|AIOPatcher>" };
        }

        if (style != NotificationStyle.Rich)
        {
            return new JsonObject { ["text"] = Join($"*{fallback}*", PlainBody(style, n, d), $"<{link}|AIOPatcher>") };
        }

        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Truncate(fallback, 150), ["emoji"] = true } },
        };
        if (!string.IsNullOrWhiteSpace(n.Message))
        {
            blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = Truncate(n.Message, 2900) } });
        }

        if (d is not null)
        {
            blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray(new JsonObject { ["type"] = "mrkdwn", ["text"] = DetailLine(d) }) });
        }

        blocks.Add(new JsonObject
        {
            ["type"] = "actions",
            ["elements"] = new JsonArray(new JsonObject
            {
                ["type"] = "button",
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = OpenLabel },
                ["url"] = link,
            }),
        });
        return new JsonObject { ["text"] = fallback, ["blocks"] = blocks };
    }

    // --- E-Mail -------------------------------------------------------------------------------------

    public static string MailText(NotificationStyle style, Notification n, NotificationDetails? d, string link) =>
        style == NotificationStyle.Short ? $"{n.Title}\n\n{link}\n" : $"{Join(PlainBody(style == NotificationStyle.Rich ? NotificationStyle.Detailed : style, n, d), link)}\n";

    /// <summary>HTML-Mail für "Schick": Karte mit farbigem Rand, Details und Knopf (nur Inline-Styles, die Mail-Programme verstehen).</summary>
    public static string MailHtml(Notification n, NotificationDetails? d, string link)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var color = $"#{Color(n.Severity):X6}";
        var message = H(n.Message).Replace("\n", "<br>");
        var details = d is null ? "" : $"<p style=\"margin:12px 0 0;color:#868e96;font-size:13px\">{H(DetailLine(d))}</p>";
        return $"""
            <!doctype html><html><body style="margin:0;padding:24px;background:#f1f3f5;font-family:Segoe UI,Arial,sans-serif">
            <div style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:10px;border-left:6px solid {color};padding:20px 24px">
            <h2 style="margin:0 0 8px;font-size:18px;color:#212529">{Emoji(n.Severity)} {H(n.Title)}</h2>
            <p style="margin:0;color:#343a40;font-size:14px;line-height:1.5">{message}</p>
            {details}
            <p style="margin:18px 0 0"><a href="{H(link)}" style="background:#7048e8;color:#ffffff;text-decoration:none;padding:9px 16px;border-radius:6px;font-size:14px">{H(OpenLabel)}</a></p>
            </div>
            <p style="text-align:center;color:#adb5bd;font-size:12px;margin-top:12px">AIOPatcher</p>
            </body></html>
            """;
    }

    // --- ntfy ---------------------------------------------------------------------------------------

    /// <summary>ntfy-Text je Stil. Schick: Markdown (fetter Titel steht als Kopfzeile, Details kursiv).</summary>
    public static string NtfyBody(NotificationStyle style, Notification n, NotificationDetails? d) => style switch
    {
        NotificationStyle.Short => n.Title,
        NotificationStyle.Detailed => Join(n.Message, DetailLine(d)),
        NotificationStyle.Rich => Join(n.Message, d is null ? "" : $"_{DetailLine(d)}_"),
        _ => n.Message,
    };

    private static string Join(params string[] parts) => string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
