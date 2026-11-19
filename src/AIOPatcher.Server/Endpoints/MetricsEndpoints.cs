using System.Collections.Concurrent;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Modules;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Auslastung: Übersicht aller Hosts und Proxmox-Gäste, Verlauf je Host.</summary>
public static class MetricsEndpoints
{
    public sealed record MetricPoint(DateTime At, float Cpu, float Memory, float? Disk);

    public sealed record HostUtilizationDto(
        Guid Id, string Name, string SiteName, OsFamily OsFamily, DateTime? LastSeenAt, DateTime? At,
        double? Cpu, int? Cores, long? MemoryUsed, long? MemoryTotal, float? Disk, string? DiskMount, int? Containers,
        IReadOnlyList<MetricPoint> Recent);

    /// <summary>Gast oder Knoten einer Proxmox-Integration, mit Zuordnung zum AIOPatcher-Host (falls eingetragen).</summary>
    public sealed record ProxmoxResourceDto(ProxmoxResource Resource, Guid? HostId, string? HostName);

    public sealed record ProxmoxUtilizationDto(Guid IntegrationId, string Name, DateTime? At, string? Error, IReadOnlyList<ProxmoxResourceDto> Resources);

    public sealed record UtilizationDto(bool Enabled, IReadOnlyList<HostUtilizationDto> Hosts, IReadOnlyList<ProxmoxUtilizationDto> Proxmox);

    public sealed record HostMetricsDto(DateTime? At, HostMetrics? Latest, IReadOnlyList<MetricPoint> Series);

    private static readonly TimeSpan ProxmoxCacheFor = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, (DateTime At, IReadOnlyList<ProxmoxResource>? Resources, string? Error)> ProxmoxCache = new();

    public static void MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/utilization", Overview);
        api.MapGet("/hosts/{id:guid}/metrics", ForHost);
    }

    /// <summary>
    /// Mit siteId: nur Hosts dieser Site. Proxmox: Cluster der Site ganz, Cluster ohne Site nur mit Gästen, die Hosts der Site sind,
    /// Cluster anderer Sites gar nicht.
    /// </summary>
    private static async Task<IResult> Overview(Guid? siteId, AppDbContext db, FeatureFlags features, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        if (!features.IsOn(Feature.Metrics))
        {
            return Results.Ok(new UtilizationDto(false, [], []));
        }

        var now = DateTime.UtcNow;
        var allHosts = await db.Hosts.Include(h => h.Site).Where(h => !h.IsRevoked).OrderBy(h => h.Name).ToListAsync(ct);
        var hosts = siteId is null ? allHosts : allHosts.Where(h => h.SiteId == siteId).ToList();
        var since = now.AddHours(-24);
        var samples = (await db.MetricSamples.Where(s => s.At >= since).OrderBy(s => s.At).ToListAsync(ct))
            .GroupBy(s => s.HostId)
            .ToDictionary(g => g.Key, g => Downsample(g, since, now, 48));

        var hostDtos = hosts.Select(h =>
        {
            var m = MetricsHistory.Latest(h);
            var fullest = m?.Disks.OrderByDescending(d => MetricsHistory.Percent(d.Used, d.Total)).FirstOrDefault();
            return new HostUtilizationDto(h.Id, h.Name, h.Site?.Name ?? "", h.OsFamily, h.LastSeenAt, h.MetricsAt,
                m?.CpuPercent, m?.CpuCores, m?.MemoryUsed, m?.MemoryTotal, m is null ? null : MetricsHistory.FullestDisk(m), fullest?.Mount, m?.Containers?.Count,
                samples.TryGetValue(h.Id, out var recent) ? recent : []);
        }).ToList();

        // Proxmox: live (kurz zwischengespeichert), auch Gäste ohne Agent
        var byExternal = allHosts.Where(h => h.IntegrationId is not null && h.ExternalId is not null)
            .ToDictionary(h => (h.IntegrationId!.Value, h.ExternalId!), h => h);
        var proxmox = new List<ProxmoxUtilizationDto>();
        foreach (var integration in await db.Integrations
                     .Where(i => i.Type == IntegrationType.Proxmox && i.Enabled && (siteId == null || i.SiteId == null || i.SiteId == siteId))
                     .OrderBy(i => i.Name).ToListAsync(ct))
        {
            var (at, resources, error) = await ResourcesAsync(integration, providers, now, ct);
            var items = (resources ?? []).Select(r => (Resource: r, Host: r.ExternalId is { } ext && byExternal.TryGetValue((integration.Id, ext), out var host) ? host : null));
            if (siteId is not null && integration.SiteId is null)
            {
                items = items.Where(x => x.Host?.SiteId == siteId).ToList();
                if (!items.Any())
                {
                    continue;
                }
            }

            proxmox.Add(new ProxmoxUtilizationDto(integration.Id, integration.Name, at, error,
                items.Select(x => new ProxmoxResourceDto(x.Resource, x.Host?.Id, x.Host?.Name)).ToList()));
        }

        return Results.Ok(new UtilizationDto(true, hostDtos, proxmox));
    }

    private static async Task<(DateTime? At, IReadOnlyList<ProxmoxResource>? Resources, string? Error)> ResourcesAsync(
        Integration integration, VirtualizationProviderFactory providers, DateTime now, CancellationToken ct)
    {
        if (ProxmoxCache.TryGetValue(integration.Id, out var cached) && now - cached.At < ProxmoxCacheFor)
        {
            return cached;
        }

        (DateTime At, IReadOnlyList<ProxmoxResource>? Resources, string? Error) result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            result = (now, await ((ProxmoxProvider)providers.For(integration)!).ListResourcesAsync(timeout.Token), null);
        }
        catch (Exception ex) when (ex is IntegrationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            result = (now, null, ex is OperationCanceledException ? L.T("Proxmox antwortet nicht (Zeitüberschreitung).", "Proxmox does not answer (timeout).") : ex.Message);
        }

        ProxmoxCache[integration.Id] = result;
        return result;
    }

    private static async Task<IResult> ForHost(Guid id, int? hours, AppDbContext db, CancellationToken ct)
    {
        var host = await db.Hosts.SingleOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        var span = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, (int)MetricsHistory.Keep.TotalHours));
        var since = now - span;
        var samples = await db.MetricSamples.Where(s => s.HostId == id && s.At >= since).OrderBy(s => s.At).ToListAsync(ct);
        return Results.Ok(new HostMetricsDto(host.MetricsAt, MetricsHistory.Latest(host), Downsample(samples, since, now, 288)));
    }

    /// <summary>Höchstens points Punkte: Mittel je Zeitabschnitt (Datenträger: Höchstwert). Lücken bleiben Lücken.</summary>
    public static List<MetricPoint> Downsample(IEnumerable<HostMetricSample> samples, DateTime from, DateTime to, int points)
    {
        var bucket = Math.Max((to - from).Ticks / points, TimeSpan.FromMinutes(5).Ticks);
        return samples
            .GroupBy(s => (s.At - from).Ticks / bucket)
            .OrderBy(g => g.Key)
            .Select(g => new MetricPoint(
                g.Max(s => s.At),
                (float)Math.Round(g.Average(s => s.Cpu), 1),
                (float)Math.Round(g.Average(s => s.Memory), 1),
                g.Max(s => s.Disk)))
            .ToList();
    }
}
