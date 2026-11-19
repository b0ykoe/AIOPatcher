using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Benachrichtigungskanäle (nur Admins).</summary>
public static class NotificationEndpoints
{
    /// <summary>Platzhalter für gespeicherte Geheimnisse: kommt er zurück, bleibt der alte Wert.</summary>
    public const string SecretMask = "••••••••";

    public sealed record ChannelDto(Guid Id, string Name, ChannelType Type, bool Enabled, string[] Events, Guid? SiteId, ChannelConfig Config, DateTime? LastSentAt, string? LastError, Guid? RemoteSiteId,
        NotificationStyle Style = NotificationStyle.Standard);

    /// <param name="Style">null = unverändert (bzw. Standard beim Anlegen).</param>
    public sealed record ChannelRequest(string Name, ChannelType Type, bool Enabled, string[] Events, Guid? SiteId, ChannelConfig Config, Guid? RemoteSiteId = null,
        NotificationStyle? Style = null);

    public static readonly NotificationEvents[] AllEvents = Enum.GetValues<NotificationEvents>().Where(e => e != NotificationEvents.None).ToArray();

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notification-channels").RequireAuthorization(UserAccounts.AdminPolicy);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/test", Test);
        group.MapPost("/preview", Preview);
    }

    public sealed record PreviewRequest(ChannelType Type, NotificationStyle Style);

    /// <summary>Vorschau im Kanal-Dialog: Beispielnachricht, genau so formatiert, wie sie verschickt würde.</summary>
    public static IResult Preview(PreviewRequest request, ServerClock clock, Options.AIOPatcherOptions options, PublicAccess? access)
    {
        var n = new Notification(NotificationEvents.JobFailed, L.T("rigidtest: Patch fehlgeschlagen", "rigidtest: Patch failed"),
            L.T("1 von 2 Updates installiert, 1 fehlgeschlagen", "1 of 2 updates installed, 1 failed"), NotificationSeverity.Critical, LinkPath: "/jobs/preview");
        var details = new NotificationDetails("rigidtest", "Homelab", NotificationFormat.EventLabel(n.Event), TimeZoneInfo.ConvertTimeFromUtc(n.CreatedAt, clock.Zone));
        var link = NotificationTexts.DashboardUrl(options, n.LinkPath, access?.Current.DashboardUrl);
        var style = request.Style;
        System.Text.Json.Nodes.JsonNode payload = request.Type switch
        {
            ChannelType.Telegram => NotificationFormat.Telegram(style, n, details, "123", link, withButton: true),
            ChannelType.Discord => NotificationFormat.Discord(style, n, details, link),
            ChannelType.Slack => NotificationFormat.Slack(style, n, details, link),
            ChannelType.Email => new System.Text.Json.Nodes.JsonObject
            {
                ["subject"] = $"[AIOPatcher] {n.Title}",
                ["text"] = NotificationFormat.MailText(style, n, details, link),
                ["html"] = style == NotificationStyle.Rich ? NotificationFormat.MailHtml(n, details, link) : null,
            },
            ChannelType.Ntfy => new System.Text.Json.Nodes.JsonObject
            {
                ["title"] = style == NotificationStyle.Short ? null : n.Title,
                ["body"] = NotificationFormat.NtfyBody(style, n, details),
                ["markdown"] = style == NotificationStyle.Rich,
                ["click"] = link,
            },
            _ => new System.Text.Json.Nodes.JsonObject
            {
                ["event"] = n.Event.ToString(), ["severity"] = n.Severity.ToString(), ["title"] = n.Title, ["message"] = n.Message,
                ["site"] = details.Site, ["host"] = details.Host, ["eventLabel"] = details.Event, ["url"] = link, ["timestamp"] = n.CreatedAt.ToString("O"),
            },
        };
        return Results.Text(payload.ToJsonString(), "application/json");
    }

    private static async Task<List<ChannelDto>> List(AppDbContext db, NotificationSenders senders, CancellationToken ct) =>
        (await db.NotificationChannels.OrderBy(c => c.Name).ToListAsync(ct)).Select(c => ToDto(c, senders)).ToList();

    private static async Task<IResult> Create(ChannelRequest request, AppDbContext db, NotificationSenders senders, AuditLog audit, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        if (await UnknownRemoteSite(request, db, ct) is { } unknown)
        {
            return unknown;
        }

        var channel = new NotificationChannel
        {
            Name = request.Name.Trim(),
            Type = request.Type,
            Enabled = request.Enabled,
            Events = ParseEvents(request.Events),
            SiteId = request.RemoteSiteId is null ? request.SiteId : null,
            RemoteSiteId = request.RemoteSiteId,
            ProtectedConfig = senders.Protect(request.Config),
            Style = request.Style ?? NotificationStyle.Standard,
        };
        db.NotificationChannels.Add(channel);
        audit.Add("notification.channel.created", $"{channel.Name} ({channel.Type})");
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(channel, senders));
    }

    private static async Task<IResult> Update(Guid id, ChannelRequest request, AppDbContext db, NotificationSenders senders, AuditLog audit, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FindAsync([id], ct);
        if (channel is null)
        {
            return Results.NotFound();
        }

        // Maskierte Geheimnisse durch die gespeicherten Werte ersetzen.
        var stored = senders.Unprotect(channel.ProtectedConfig);
        var merged = request.Config;
        foreach (var field in ChannelConfig.SecretFields)
        {
            var property = typeof(ChannelConfig).GetProperty(field)!;
            if (property.GetValue(merged) as string == SecretMask)
            {
                property.SetValue(merged, property.GetValue(stored));
            }
        }

        var validated = request with { Config = merged };
        if (Validate(validated) is { } problem)
        {
            return problem;
        }

        if (await UnknownRemoteSite(request, db, ct) is { } unknown)
        {
            return unknown;
        }

        channel.Name = request.Name.Trim();
        channel.Type = request.Type;
        channel.Enabled = request.Enabled;
        channel.Events = ParseEvents(request.Events);
        channel.SiteId = request.RemoteSiteId is null ? request.SiteId : null;
        channel.RemoteSiteId = request.RemoteSiteId;
        channel.ProtectedConfig = senders.Protect(merged);
        channel.Style = request.Style ?? channel.Style;
        audit.Add("notification.channel.updated", $"{channel.Name} ({channel.Type})");
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(channel, senders));
    }

    private static async Task<IResult> Delete(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FindAsync([id], ct);
        if (channel is null)
        {
            return Results.NotFound();
        }

        db.NotificationChannels.Remove(channel);
        audit.Add("notification.channel.deleted", channel.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Testnachricht direkt senden (nicht über die Warteschlange), damit der Fehler sofort sichtbar ist.</summary>
    private static async Task<IResult> Test(Guid id, AppDbContext db, NotificationSenders senders, ServerClock clock, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FindAsync([id], ct);
        if (channel is null)
        {
            return Results.NotFound();
        }

        var error = await senders.SendAsync(channel, new Notification(
            NotificationEvents.None,
            L.T("Testnachricht", "Test message"),
            L.T("Dieser Kanal ist richtig eingerichtet. So sehen Nachrichten in diesem Stil aus.", "This channel is set up correctly. This is how messages look in this style."),
            NotificationSeverity.Info), ct,
            // Beispieldetails, damit "Ausführlich" und "Schick" zeigen, wie echte Nachrichten aussehen
            new NotificationDetails("web01", "Homelab", NotificationFormat.EventLabel(NotificationEvents.None), TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, clock.Zone)));

        channel.LastError = error;
        if (error is null)
        {
            channel.LastSentAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return error is null
            ? Results.NoContent()
            : Results.Problem(L.T($"Senden fehlgeschlagen: {error}", $"Sending failed: {error}"), statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult?> UnknownRemoteSite(ChannelRequest request, AppDbContext db, CancellationToken ct) =>
        request.RemoteSiteId is { } id && !await db.RemoteSites.AnyAsync(r => r.Id == id, ct)
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["remoteSiteId"] = [L.T("Geteilte Site existiert nicht.", "Shared site does not exist.")] })
            : null;

    private static IResult? Validate(ChannelRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        if (!Enum.IsDefined(request.Type))
        {
            errors["type"] = [L.T("Unbekannter Kanaltyp.", "Unknown channel type.")];
        }
        else if (NotificationSenders.Validate(request.Type, request.Config ?? new ChannelConfig()) is { } configError)
        {
            errors["config"] = [configError];
        }

        if ((request.Events ?? []).Any(e => !Enum.TryParse<NotificationEvents>(e, out _)))
        {
            errors["events"] = [L.T("Unbekanntes Ereignis.", "Unknown event.")];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static NotificationEvents ParseEvents(IEnumerable<string>? names) =>
        (names ?? []).Aggregate(NotificationEvents.None, (all, name) => Enum.TryParse<NotificationEvents>(name, out var e) ? all | e : all);

    private static ChannelDto ToDto(NotificationChannel c, NotificationSenders senders)
    {
        var config = senders.Unprotect(c.ProtectedConfig);
        foreach (var field in ChannelConfig.SecretFields)
        {
            var property = typeof(ChannelConfig).GetProperty(field)!;
            if (!string.IsNullOrEmpty(property.GetValue(config) as string))
            {
                property.SetValue(config, SecretMask);
            }
        }

        var events = AllEvents.Where(e => (c.Events & e) != 0).Select(e => e.ToString()).ToArray();
        return new ChannelDto(c.Id, c.Name, c.Type, c.Enabled, events, c.SiteId, config, c.LastSentAt, c.LastError, c.RemoteSiteId, c.Style);
    }
}
