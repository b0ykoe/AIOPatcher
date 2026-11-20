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

    /// <summary>Nur bei Sammelnachrichten: die einzelnen Meldungen (für den Webhook).</summary>
    public IReadOnlyList<Notification>? Items { get; init; }
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
    private static readonly TimeSpan DigestCheck = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ConsumeAsync(stoppingToken), FlushLoopAsync(stoppingToken));
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
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

    private async Task FlushLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DigestCheck);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await FlushDigestsAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), senders, clock?.Zone ?? TimeZoneInfo.Local, DateTime.UtcNow, logger, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Sending notification digests failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Fällige Sammlungen senden (je Kanal eine Nachricht) und aus der Warteschlange löschen, auch bei Fehlern.</summary>
    public static async Task<int> FlushDigestsAsync(AppDbContext db, NotificationSenders? senders, TimeZoneInfo zone, DateTime now, ILogger? logger, CancellationToken ct)
    {
        var pending = await db.PendingNotifications.ToListAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var channelIds = pending.Select(p => p.ChannelId).Distinct().ToList();
        var channels = await db.NotificationChannels.Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var sent = 0;
        foreach (var group in pending.GroupBy(p => p.ChannelId))
        {
            var items = group.ToList();
            if (!channels.TryGetValue(group.Key, out var channel) || !channel.Enabled || channel.DigestMinutes <= 0)
            {
                // Kanal gelöscht, aus oder auf "sofort" umgestellt: Gesammeltes trotzdem noch zustellen (außer bei "aus")
                if (channel is { Enabled: true } && senders is not null)
                {
                    var (single, details) = NotificationDigest.Build(items, zone);
                    await SendAndRecordAsync(channel, single, details);
                }

                db.PendingNotifications.RemoveRange(items);
                continue;
            }

            if (!NotificationDigest.IsDue(items.Min(i => i.CreatedAt), items.Max(i => i.CreatedAt), channel.DigestMinutes, now))
            {
                continue;
            }

            var (digest, digestDetails) = NotificationDigest.Build(items, zone);
            if (senders is not null)
            {
                await SendAndRecordAsync(channel, digest, digestDetails);
            }

            db.PendingNotifications.RemoveRange(items);
            sent++;
        }

        await db.SaveChangesAsync(ct);
        return sent;

        async Task SendAndRecordAsync(NotificationChannel channel, Notification n, NotificationDetails details)
        {
            var error = await senders!.SendAsync(channel, n, ct, details);
            if (error is null)
            {
                channel.LastSentAt = DateTime.UtcNow;
                channel.LastError = null;
            }
            else
            {
                channel.LastError = error.Length > 500 ? error[..500] : error;
                logger?.LogWarning("Notification channel {Channel} failed: {Error}", channel.Name, error);
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
            if (NotificationDigest.ShouldCollect(channel, notification.Event))
            {
                db.PendingNotifications.Add(NotificationDigest.ToPending(channel.Id, notification, details));
                continue;
            }

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
