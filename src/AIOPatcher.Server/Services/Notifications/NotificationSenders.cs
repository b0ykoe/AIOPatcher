using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.AspNetCore.DataProtection;

namespace AIOPatcher.Server.Services.Notifications;

/// <summary>
/// Kanal-Konfiguration. Alle Typen teilen sich ein flaches Objekt; welche Felder gelten, hängt vom Typ ab.
/// Geheime Felder werden in der API nur maskiert ausgeliefert (siehe <see cref="SecretFields"/>).
/// </summary>
public sealed class ChannelConfig
{
    /// <summary>ntfy: Topic-URL (https://ntfy.sh/mein-topic). Webhook/Discord/Slack: Ziel-URL.</summary>
    public string? Url { get; set; }

    /// <summary>ntfy: Access-Token. Telegram: Bot-Token.</summary>
    public string? Token { get; set; }

    /// <summary>Webhook: Schlüssel für die HMAC-SHA256-Signatur.</summary>
    public string? Secret { get; set; }

    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }

    /// <summary>"StartTls" (Standard, Port 587) oder "None" (nur im internen Netz).</summary>
    public string? SmtpSecurity { get; set; }
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>Telegram: Chat-ID.</summary>
    public string? ChatId { get; set; }

    public static readonly string[] SecretFields = [nameof(Token), nameof(Secret), nameof(SmtpPassword), nameof(Url)];
}

public sealed class NotificationSenders(
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection,
    AIOPatcherOptions options)
{
    private const string ProtectorPurpose = "AIOPatcher.NotificationChannel.Config";
    public const string HttpClientName = "notifications";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Protect(ChannelConfig config) =>
        dataProtection.CreateProtector(ProtectorPurpose).Protect(JsonSerializer.Serialize(config, Json));

    public ChannelConfig Unprotect(string protectedConfig) =>
        JsonSerializer.Deserialize<ChannelConfig>(dataProtection.CreateProtector(ProtectorPurpose).Unprotect(protectedConfig), Json) ?? new ChannelConfig();

    /// <summary>Validiert die Konfiguration. Gibt eine Fehlermeldung zurück oder null.</summary>
    public static string? Validate(ChannelType type, ChannelConfig c) => type switch
    {
        ChannelType.Ntfy or ChannelType.Webhook or ChannelType.Discord or ChannelType.Slack when !IsHttpUrl(c.Url) =>
            L.T("Gültige http(s)-URL angeben.", "Enter a valid http(s) URL."),
        ChannelType.Email when string.IsNullOrWhiteSpace(c.SmtpHost) || string.IsNullOrWhiteSpace(c.From) || string.IsNullOrWhiteSpace(c.To) =>
            L.T("SMTP-Server, Absender und Empfänger angeben.", "Enter SMTP server, sender and recipient."),
        ChannelType.Email when !MailAddressesValid(c.From) || !MailAddressesValid(c.To) =>
            L.T("Ungültige E-Mail-Adresse.", "Invalid email address."),
        ChannelType.Telegram when string.IsNullOrWhiteSpace(c.Token) || string.IsNullOrWhiteSpace(c.ChatId) =>
            L.T("Bot-Token und Chat-ID angeben.", "Enter bot token and chat ID."),
        _ => null,
    };

    /// <summary>Sendet und gibt bei Fehler eine kurze Beschreibung zurück (nie eine Exception).</summary>
    public async Task<string?> SendAsync(NotificationChannel channel, Notification n, CancellationToken ct)
    {
        try
        {
            var config = Unprotect(channel.ProtectedConfig);
            var link = NotificationTexts.DashboardUrl(options, n.LinkPath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            switch (channel.Type)
            {
                case ChannelType.Ntfy:
                    await SendNtfyAsync(config, n, link, timeout.Token);
                    break;
                case ChannelType.Email:
                    await SendEmailAsync(config, n, link, timeout.Token);
                    break;
                case ChannelType.Webhook:
                    await SendWebhookAsync(config, n, link, timeout.Token);
                    break;
                case ChannelType.Discord:
                    await PostJsonAsync(config.Url!, new JsonObject { ["content"] = $"**{Emoji(n.Severity)} {n.Title}**\n{n.Message}\n{link}" }, null, timeout.Token);
                    break;
                case ChannelType.Slack:
                    await PostJsonAsync(config.Url!, new JsonObject { ["text"] = $"*{Emoji(n.Severity)} {n.Title}*\n{n.Message}\n<{link}|AIOPatcher>" }, null, timeout.Token);
                    break;
                case ChannelType.Telegram:
                    await PostJsonAsync(
                        $"https://api.telegram.org/bot{config.Token}/sendMessage",
                        new JsonObject { ["chat_id"] = config.ChatId, ["text"] = $"{Emoji(n.Severity)} {n.Title}\n{n.Message}\n{link}", ["disable_web_page_preview"] = true },
                        null,
                        timeout.Token);
                    break;
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Timeout";
        }
        catch (Exception ex)
        {
            // Bot-Tokens stehen in der Telegram-URL: nie die Exception-Message mit URL durchreichen.
            return ex is HttpRequestException http ? $"HTTP {(int?)http.StatusCode} {http.HttpRequestError}" : ex.GetType().Name + ": " + ex.Message;
        }
    }

    private async Task SendNtfyAsync(ChannelConfig c, Notification n, string link, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, c.Url) { Content = new StringContent(n.Message, Encoding.UTF8, "text/plain") };
        // Header sind ASCII-only: Titel per RFC 2047 kodieren, das versteht ntfy.
        request.Headers.TryAddWithoutValidation("Title", $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(n.Title))}?=");
        request.Headers.TryAddWithoutValidation("Priority", n.Severity switch { NotificationSeverity.Critical => "high", NotificationSeverity.Warning => "default", _ => "low" });
        request.Headers.TryAddWithoutValidation("Tags", n.Severity switch { NotificationSeverity.Critical => "rotating_light", NotificationSeverity.Warning => "warning", NotificationSeverity.Success => "white_check_mark", _ => "information_source" });
        request.Headers.TryAddWithoutValidation("Click", link);
        if (!string.IsNullOrWhiteSpace(c.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
        }

        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SendEmailAsync(ChannelConfig c, Notification n, string link, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(c.From!),
            Subject = $"[AIOPatcher] {n.Title}",
            Body = $"{n.Message}\n\n{link}\n",
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        foreach (var to in SplitAddresses(c.To))
        {
            message.To.Add(to);
        }

#pragma warning disable SYSLIB0014 // SmtpClient genügt für einfache Benachrichtigungen (STARTTLS auf 587).
        using var smtp = new SmtpClient(c.SmtpHost, c.SmtpPort ?? 587)
        {
            EnableSsl = !string.Equals(c.SmtpSecurity, "None", StringComparison.OrdinalIgnoreCase),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20000,
        };
#pragma warning restore SYSLIB0014
        if (!string.IsNullOrEmpty(c.SmtpUser))
        {
            smtp.Credentials = new NetworkCredential(c.SmtpUser, c.SmtpPassword);
        }

        await smtp.SendMailAsync(message, ct);
    }

    private async Task SendWebhookAsync(ChannelConfig c, Notification n, string link, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["event"] = n.Event.ToString(),
            ["severity"] = n.Severity.ToString(),
            ["title"] = n.Title,
            ["message"] = n.Message,
            ["siteId"] = n.SiteId?.ToString(),
            ["hostId"] = n.HostId?.ToString(),
            ["url"] = link,
            ["timestamp"] = n.CreatedAt.ToString("O"),
        };
        await PostJsonAsync(c.Url!, body, c.Secret, ct);
    }

    private async Task PostJsonAsync(string url, JsonObject body, string? hmacSecret, CancellationToken ct)
    {
        var json = body.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(hmacSecret))
        {
            // Empfänger prüft: HMAC-SHA256(secret, rohen Body) == Header-Wert.
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hmacSecret), Encoding.UTF8.GetBytes(json)));
            request.Headers.TryAddWithoutValidation("X-AIOPatcher-Signature", $"sha256={signature}");
        }

        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string Emoji(NotificationSeverity s) => s switch
    {
        NotificationSeverity.Critical => "🚨",
        NotificationSeverity.Warning => "⚠️",
        NotificationSeverity.Success => "✅",
        _ => "ℹ️",
    };

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static IEnumerable<string> SplitAddresses(string? list) =>
        (list ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MailAddressesValid(string? list) =>
        SplitAddresses(list).Any() && SplitAddresses(list).All(a => MailAddress.TryCreate(a, out _));
}
