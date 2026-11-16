using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Erkennt Hosts, die sich nicht mehr melden, und Agent-Zertifikate kurz vor Ablauf.</summary>
public sealed class HostMonitor(IServiceScopeFactory scopes, AIOPatcherOptions options, Notifier notifier, ILogger<HostMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await CheckAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), options.OfflineAfter, DateTime.UtcNow, notifier, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Host monitor failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task CheckAsync(AppDbContext db, TimeSpan offlineAfter, DateTime now, Notifier notifier, CancellationToken ct)
    {
        var threshold = now - offlineAfter;
        var gone = await db.Hosts
            .Include(h => h.Site)
            .Where(h => !h.IsRevoked && h.LastSeenAt != null && h.LastSeenAt < threshold && h.OfflineNotifiedAt == null)
            .ToListAsync(ct);

        foreach (var host in gone)
        {
            host.OfflineNotifiedAt = now;
            notifier.Publish(new Notification(NotificationEvents.HostOffline,
                L.T($"{host.Name} ist offline", $"{host.Name} is offline"),
                L.T($"Letzter Kontakt {host.LastSeenAt:yyyy-MM-dd HH:mm} UTC · Site {host.Site?.Name}",
                    $"Last contact {host.LastSeenAt:yyyy-MM-dd HH:mm} UTC · site {host.Site?.Name}"),
                NotificationSeverity.Critical, host.SiteId, host.Id, $"/hosts/{host.Id}"));
        }

        // Zertifikate, die trotz automatischer Erneuerung bald ablaufen (Agent lange offline oder Erneuerung scheitert)
        var soon = now.AddDays(14);
        var expiring = await db.Hosts
            .Where(h => !h.IsRevoked && h.CertificateExpiresAt > DateTime.MinValue && h.CertificateExpiresAt < soon && h.CertificateExpiryNotifiedAt == null)
            .ToListAsync(ct);
        foreach (var host in expiring)
        {
            host.CertificateExpiryNotifiedAt = now;
            notifier.Publish(new Notification(NotificationEvents.CertificateExpiring,
                L.T($"{host.Name}: Agent-Zertifikat läuft ab", $"{host.Name}: agent certificate expiring"),
                L.T($"Gültig bis {host.CertificateExpiresAt:yyyy-MM-dd}. Der Agent erneuert es selbst, sobald er sich meldet; sonst neu registrieren.",
                    $"Valid until {host.CertificateExpiresAt:yyyy-MM-dd}. The agent renews it itself when it checks in; otherwise re-enroll."),
                NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
        }

        await db.SaveChangesAsync(ct);
    }
}
