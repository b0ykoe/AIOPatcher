using System.Threading.Channels;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Notifications;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Critical,
}

/// <summary>Eine Benachrichtigung. Titel und Text sind bereits in der Server-Sprache formuliert.</summary>
public sealed record Notification(
    NotificationEvents Event,
    string Title,
    string Message,
    NotificationSeverity Severity,
    Guid? SiteId = null,
    Guid? HostId = null,
    string? LinkPath = null,
    Guid? RemoteSiteId = null)
{
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Nimmt Benachrichtigungen entgegen, ohne den Aufrufer zu blockieren. Zugestellt wird im Hintergrund
/// (<see cref="NotificationDispatcher"/>), damit ein langsamer Mailserver keinen Agent-Check-in aufhält.
/// </summary>
public sealed class Notifier
{
    private readonly Channel<Notification> _queue = Channel.CreateBounded<Notification>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public void Publish(Notification notification) => _queue.Writer.TryWrite(notification);

    public ChannelReader<Notification> Reader => _queue.Reader;
}

public sealed class NotificationDispatcher(
    Notifier notifier,
    IServiceScopeFactory scopes,
    NotificationSenders senders,
    ILogger<NotificationDispatcher> logger,
    ServerClock? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in notifier.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(notification, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Dispatching notification {Event} failed", notification.Event);
            }
        }
    }

    /// <summary>Host- und Site-Name, Ereignis und Ortszeit für die Stile "Ausführlich" und "Schick".</summary>
    public static async Task<NotificationDetails> DetailsAsync(AppDbContext db, Notification n, ServerClock? clock, CancellationToken ct)
    {
        var host = n.HostId is { } hostId ? await db.Hosts.AsNoTracking().Where(h => h.Id == hostId).Select(h => h.Name).FirstOrDefaultAsync(ct) : null;
        var site = n.SiteId is { } siteId ? await db.Sites.AsNoTracking().Where(x => x.Id == siteId).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var local = clock is null ? n.CreatedAt.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(n.CreatedAt, clock.Zone);
        return new NotificationDetails(host, site, NotificationFormat.EventLabel(n.Event), local);
    }

    private async Task DispatchAsync(Notification notification, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Geteilte Sites (andere Instanz) nur an Kanäle, die sie ausdrücklich gewählt haben.
        System.Linq.Expressions.Expression<Func<NotificationChannel, bool>> channelFilter = notification.RemoteSiteId == null
            ? c => c.RemoteSiteId == null && (c.SiteId == null || c.SiteId == notification.SiteId)
            : c => c.RemoteSiteId == notification.RemoteSiteId;
        var channels = await db.NotificationChannels
            .Where(c => c.Enabled)
            .Where(channelFilter)
            .ToListAsync(ct);

        var targets = channels.Where(c => (c.Events & notification.Event) != 0).ToList();
        var details = targets.Count == 0 ? null : await DetailsAsync(db, notification, clock, ct);
        foreach (var channel in targets)
        {
            var error = await senders.SendAsync(channel, notification, ct, details);
            if (error is null)
            {
                channel.LastSentAt = DateTime.UtcNow;
                channel.LastError = null;
            }
            else
            {
                channel.LastError = error.Length > 500 ? error[..500] : error;
                logger.LogWarning("Notification channel {Channel} failed: {Error}", channel.Name, error);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Helfer, damit Ereignisquellen einheitliche Texte und Links erzeugen.</summary>
public static class NotificationTexts
{
    /// <summary>
    /// Link ins Dashboard: die öffentliche Dashboard-Adresse aus Einstellungen → Öffentlicher Zugang, wenn eingerichtet
    /// (z. B. https://patch.example.com), sonst die interne Adresse mit Port.
    /// </summary>
    public static string DashboardUrl(AIOPatcherOptions options, string? path, string? publicDashboardUrl = null) =>
        string.IsNullOrWhiteSpace(publicDashboardUrl)
            ? $"https://{options.UrlHost}:{options.DashboardPort}{path ?? "/"}"
            : publicDashboardUrl.TrimEnd('/') + (path ?? "/");
}
