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

    public sealed record ChannelDto(Guid Id, string Name, ChannelType Type, bool Enabled, string[] Events, Guid? SiteId, ChannelConfig Config, DateTime? LastSentAt, string? LastError);
    public sealed record ChannelRequest(string Name, ChannelType Type, bool Enabled, string[] Events, Guid? SiteId, ChannelConfig Config);

    public static readonly NotificationEvents[] AllEvents = Enum.GetValues<NotificationEvents>().Where(e => e != NotificationEvents.None).ToArray();

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notification-channels").RequireAuthorization(UserAccounts.AdminPolicy);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/test", Test);
    }

    private static async Task<List<ChannelDto>> List(AppDbContext db, NotificationSenders senders, CancellationToken ct) =>
        (await db.NotificationChannels.OrderBy(c => c.Name).ToListAsync(ct)).Select(c => ToDto(c, senders)).ToList();

    private static async Task<IResult> Create(ChannelRequest request, AppDbContext db, NotificationSenders senders, AuditLog audit, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var channel = new NotificationChannel
        {
            Name = request.Name.Trim(),
            Type = request.Type,
            Enabled = request.Enabled,
            Events = ParseEvents(request.Events),
            SiteId = request.SiteId,
            ProtectedConfig = senders.Protect(request.Config),
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

        channel.Name = request.Name.Trim();
        channel.Type = request.Type;
        channel.Enabled = request.Enabled;
        channel.Events = ParseEvents(request.Events);
        channel.SiteId = request.SiteId;
        channel.ProtectedConfig = senders.Protect(merged);
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
    private static async Task<IResult> Test(Guid id, AppDbContext db, NotificationSenders senders, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FindAsync([id], ct);
        if (channel is null)
        {
            return Results.NotFound();
        }

        var error = await senders.SendAsync(channel, new Notification(
            NotificationEvents.None,
            L.T("Testnachricht", "Test message"),
            L.T("Dieser Kanal ist richtig eingerichtet.", "This channel is set up correctly."),
            NotificationSeverity.Info), ct);

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
        return new ChannelDto(c.Id, c.Name, c.Type, c.Enabled, events, c.SiteId, config, c.LastSentAt, c.LastError);
    }
}
