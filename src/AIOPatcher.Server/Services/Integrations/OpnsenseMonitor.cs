using System.Net;
using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>
/// Fragt OPNsense-Firewalls regelmäßig ab: Firmware-Stand (einmal täglich mit Update-Suche) und ARP-Tabelle fürs IPAM.
/// </summary>
public sealed class OpnsenseMonitor(IServiceScopeFactory scopes, OpnsenseClientFactory clients, Notifier notifier, ILogger<OpnsenseMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, DateTime> _lastFullCheck = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var integration in await db.Integrations.Where(i => i.Type == IntegrationType.Opnsense && i.Enabled).ToListAsync(stoppingToken))
                {
                    var full = !_lastFullCheck.TryGetValue(integration.Id, out var last) || DateTime.UtcNow - last > TimeSpan.FromHours(24);
                    await RefreshAsync(db, integration, checkForUpdates: full, stoppingToken);
                    if (full)
                    {
                        _lastFullCheck[integration.Id] = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "OPNsense monitor failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Status abfragen und ARP importieren. Fehler landen in Integration.LastError.</summary>
    public async Task<OpnsenseFirmware?> RefreshAsync(AppDbContext db, Integration integration, bool checkForUpdates, CancellationToken ct)
    {
        var client = clients.For(integration);
        OpnsenseFirmware? firmware = null;
        try
        {
            var previous = Parse(integration.StatusJson);
            firmware = await client.GetFirmwareAsync(checkForUpdates, ct);
            integration.StatusJson = JsonSerializer.Serialize(firmware, Json);
            integration.LastError = null;

            if (firmware.UpdatesAvailable && previous?.UpdatesAvailable != true)
            {
                notifier.Publish(new Notification(NotificationEvents.NewSecurityUpdates,
                    L.T($"OPNsense {integration.Name}: Update verfügbar", $"OPNsense {integration.Name}: update available"),
                    $"{firmware.ProductVersion} → {firmware.LatestVersion ?? "?"} ({firmware.PackageUpdates} {L.T("Pakete", "packages")})",
                    NotificationSeverity.Warning, integration.SiteId, LinkPath: "/settings/integrations"));
            }

            if (integration.SiteId is { } siteId)
            {
                await ImportArpAsync(db, integration, siteId, await client.GetArpAsync(ct), ct);
            }
        }
        catch (IntegrationException ex)
        {
            integration.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        integration.LastCheckAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return firmware;
    }

    public static OpnsenseFirmware? Parse(string? statusJson)
    {
        if (string.IsNullOrEmpty(statusJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OpnsenseFirmware>(statusJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ARP-Einträge dieser OPNsense ersetzen (Kennung im Feld Description), Subnetz zuordnen.</summary>
    public static async Task ImportArpAsync(AppDbContext db, Integration integration, Guid siteId, IReadOnlyList<ArpEntry> entries, CancellationToken ct)
    {
        var tag = $"OPNsense {integration.Name}";
        db.IpAddresses.RemoveRange(await db.IpAddresses.Where(a => a.SiteId == siteId && a.Source == IpSource.Opnsense && a.Description == tag).ToListAsync(ct));

        var seen = new HashSet<string>();
        var now = DateTime.UtcNow;
        foreach (var entry in entries.Take(5000))
        {
            if (!IPAddress.TryParse(entry.Ip, out var ip) || !seen.Add(ip.ToString()))
            {
                continue;
            }

            db.IpAddresses.Add(new IpAddressEntry
            {
                SiteId = siteId,
                Address = ip.ToString(),
                Source = IpSource.Opnsense,
                MacAddress = entry.Mac is { Length: <= 32 } mac ? mac.ToLowerInvariant() : null,
                Hostname = entry.Hostname is { Length: > 0 } h ? (h.Length > 200 ? h[..200] : h) : null,
                InterfaceName = entry.Interface is { Length: > 0 } i ? (i.Length > 64 ? i[..64] : i) : null,
                Description = tag,
                SubnetId = await IpamEndpoints.SubnetForAsync(db, siteId, ip, ct),
                LastSeenAt = now,
            });
        }
    }
}
