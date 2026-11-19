using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Monitoring;

/// <summary>Zustand eines Clients im Vergleich zum Ziel.</summary>
public static class MonitoringStates
{
    /// <summary>Ziel vorhanden, kein Client installiert.</summary>
    public const string Missing = "missing";
    public const string Ok = "ok";

    /// <summary>Client zeigt auf ein anderes Ziel.</summary>
    public const string OtherTarget = "otherTarget";

    /// <summary>Client ist älter als Manager bzw. Site.</summary>
    public const string Outdated = "outdated";

    /// <summary>Wazuh-Client ist neuer als der Manager (wird nicht unterstützt).</summary>
    public const string TooNew = "tooNew";

    /// <summary>Richtiges Ziel, aber nicht verbunden bzw. Dienst läuft nicht.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>Variante, die AIOPatcher nicht umstellen kann (z. B. Checkmk 1.x unter Windows).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Client installiert, aber kein Ziel für diesen Host.</summary>
    public const string NoTarget = "noTarget";
}

/// <summary>Monitoring-Clients: Ziele auflösen, Status speichern und bewerten, Vorgaben für den Agent bauen.</summary>
public static class MonitoringService
{
    /// <summary>Ziel für den Host: das seiner Site, sonst das allgemeine (ohne Site).</summary>
    public static MonitoringTarget? TargetFor(IEnumerable<MonitoringTarget> targets, ManagedHost host, string product)
    {
        var list = targets.Where(t => t.Product == product).ToList();
        return list.FirstOrDefault(t => t.SiteId == host.SiteId) ?? list.FirstOrDefault(t => t.SiteId is null);
    }

    /// <summary>"https://cmk.example.com/mysite" → (cmk.example.com, mysite).</summary>
    public static (string Host, string Site)? ParseSiteUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var site = uri.AbsolutePath.Trim('/').Split('/')[0];
        return site.Length == 0 ? null : (uri.Host, site);
    }

    public static IReadOnlyList<string> SplitOnlyFrom(string? value) =>
        (value ?? "").Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Vorgabe für den Agent: Ziel plus Host (Checkmk-Hostname) und ggf. Paket.</summary>
    public static MonitoringSpec Spec(MonitoringTarget target, ManagedHost host, MonitoringPackage? package = null)
    {
        if (target.Product == MonitoringProducts.Wazuh)
        {
            return new MonitoringSpec(MonitoringProducts.Wazuh, target.Address, target.Port, target.Protocol, target.Enrollment, target.EnrollmentPort,
                string.IsNullOrWhiteSpace(target.Groups) ? null : target.Groups, MonitoringVersion.Display(target.ManagerVersion), Package: package);
        }

        var site = ParseSiteUrl(target.SiteUrl);
        var onlyFrom = SplitOnlyFrom(target.OnlyFrom);
        return new MonitoringSpec(MonitoringProducts.Checkmk,
            Server: site is null ? null : target.ReceiverPort is { } port ? $"{site.Value.Host}:{port}" : site.Value.Host,
            Site: site?.Site, User: target.RegistrationUser, Hostname: host.Hostname,
            OnlyFrom: onlyFrom.Count > 0 ? onlyFrom : null, SiteVersion: MonitoringVersion.Display(target.SiteVersion), Package: package);
    }

    /// <summary>Zustand eines Clients gegenüber dem Ziel (schwerwiegendstes zuerst).</summary>
    public static string Evaluate(MonitoringTarget? target, MonitoringClientState? client)
    {
        if (client is null)
        {
            return target is null ? MonitoringStates.NoTarget : MonitoringStates.Missing;
        }

        if (!client.CanConfigure)
        {
            return MonitoringStates.Unsupported;
        }

        if (target is null)
        {
            return MonitoringStates.NoTarget;
        }

        if (!PointsTo(target, client))
        {
            return MonitoringStates.OtherTarget;
        }

        var wanted = target.Product == MonitoringProducts.Wazuh ? target.ManagerVersion : target.SiteVersion;
        if (wanted is not null && client.Version is not null)
        {
            var compare = MonitoringVersion.Compare(client.Version, wanted);
            if (compare > 0 && target.Product == MonitoringProducts.Wazuh)
            {
                return MonitoringStates.TooNew;
            }

            if (compare < 0)
            {
                return MonitoringStates.Outdated;
            }
        }

        return client.Connected == false || !client.ServiceRunning ? MonitoringStates.Disconnected : MonitoringStates.Ok;
    }

    /// <summary>Zeigt der Client auf das Ziel?</summary>
    public static bool PointsTo(MonitoringTarget target, MonitoringClientState client)
    {
        var addresses = client.AddressList;
        if (target.Product == MonitoringProducts.Wazuh)
        {
            return target.Address is { } wanted && addresses.Any(a => string.Equals(HostOf(a), wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (client.Variant == MonitoringVariants.CheckmkController)
        {
            return ParseSiteUrl(target.SiteUrl) is { } site
                   && addresses.Any(a => a.Contains('/') && string.Equals(HostOf(a[..a.LastIndexOf('/')]), site.Host, StringComparison.OrdinalIgnoreCase)
                                         && a[(a.LastIndexOf('/') + 1)..] == site.Site);
        }

        // Legacy: only_from muss genau die IPs der Site enthalten (ohne Vorgabe nicht prüfbar)
        var onlyFrom = SplitOnlyFrom(target.OnlyFrom);
        return onlyFrom.Count == 0 || onlyFrom.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(addresses);
    }

    /// <summary>Host aus "addr", "addr:port", "[v6]:port" oder "https://addr:port".</summary>
    public static string HostOf(string address)
    {
        var value = address.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.Host.Trim('[', ']');
        }

        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            return end > 0 ? value[1..end] : value;
        }

        return value.Count(c => c == ':') == 1 ? value[..value.IndexOf(':')] : value;
    }

    /// <summary>Antwort auf monitoring.status speichern; meldet Clients, die die Verbindung verloren haben.</summary>
    public static async Task StoreAsync(AppDbContext db, ManagedHost host, MonitoringStatus status, DateTime now, CancellationToken ct, Notifier? notifier = null)
    {
        host.MonitoringChangesAllowed = status.ChangesAllowed;
        host.MonitoringScannedAt = now;

        var existing = await db.MonitoringClients.Where(c => c.HostId == host.Id).ToListAsync(ct);
        foreach (var gone in existing.Where(e => status.Clients.All(c => c.Product != e.Product)))
        {
            db.MonitoringClients.Remove(gone);
        }

        foreach (var client in status.Clients.Where(c => MonitoringProducts.All.Contains(c.Product)).DistinctBy(c => c.Product))
        {
            var row = existing.FirstOrDefault(e => e.Product == client.Product);
            var wasUp = row is { Connected: not false, ServiceRunning: true };
            if (row is null)
            {
                db.MonitoringClients.Add(row = new MonitoringClientState { HostId = host.Id, Product = client.Product, Variant = client.Variant });
            }

            row.Variant = Cut(client.Variant, 30)!;
            row.Version = Cut(client.Version, 60);
            row.ServiceRunning = client.ServiceRunning;
            row.Addresses = Cut(string.Join('\n', client.Addresses.Take(20)), 2000)!;
            row.Connected = client.Connected;
            row.Detail = Cut(client.Detail, 500);
            row.CanConfigure = client.CanConfigure;
            row.Held = client.Held;
            row.SeenAt = now;

            var down = client.Connected == false || !client.ServiceRunning;
            if (!down)
            {
                row.DisconnectedSince = null;
            }
            else if (row.DisconnectedSince is null)
            {
                row.DisconnectedSince = now;
                // Nur Wechsel von verbunden zu getrennt melden (nicht jeden Abruf, nicht frisch installierte Clients)
                if (wasUp && existing.Contains(row) && notifier is not null)
                {
                    var name = client.Product == MonitoringProducts.Wazuh ? "Wazuh" : "Checkmk";
                    var reason = !client.ServiceRunning
                        ? L.T("Der Dienst läuft nicht.", "The service is not running.")
                        : L.T($"Nicht verbunden ({client.Detail ?? "?"}).", $"Not connected ({client.Detail ?? "?"}).");
                    notifier.Publish(new Notification(NotificationEvents.MonitoringDisconnected,
                        L.T($"{host.Name}: {name}-Client getrennt", $"{host.Name}: {name} client disconnected"),
                        reason, NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
                }
            }
        }
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
