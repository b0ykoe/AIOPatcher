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
    string? LinkPath = null)
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
    ILogger<NotificationDispatcher> logger) : BackgroundService
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

    private async Task DispatchAsync(Notification notification, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var channels = await db.NotificationChannels
            .Where(c => c.Enabled && (c.SiteId == null || c.SiteId == notification.SiteId))
            .ToListAsync(ct);

        foreach (var channel in channels.Where(c => (c.Events & notification.Event) != 0))
        {
            var error = await senders.SendAsync(channel, notification, ct);
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
    public static string DashboardUrl(AIOPatcherOptions options, string? path) =>
        $"https://{options.PublicHostname}:{options.DashboardPort}{path ?? "/"}";
}
