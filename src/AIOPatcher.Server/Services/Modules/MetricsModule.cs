using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Modules;

/// <summary>Wie oft gemessen und wie lange der Verlauf aufbewahrt wird.</summary>
public static class MetricsHistory
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    public static HostMetrics? Latest(ManagedHost host)
    {
        if (string.IsNullOrEmpty(host.Metrics))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(host.Metrics, PullJsonContext.Default.HostMetrics);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static float Percent(long used, long total) => total > 0 ? (float)Math.Round(100.0 * used / total, 1) : 0;

    /// <summary>Vollster Datenträger in Prozent (null = keiner).</summary>
    public static float? FullestDisk(HostMetrics metrics) =>
        metrics.Disks.Count == 0 ? null : metrics.Disks.Max(d => Percent(d.Used, d.Total));
}

/// <summary>metrics.sample alle 5 Minuten: letzte Messung am Host, Verlauf als Zeile in MetricSamples.</summary>
public sealed class MetricsServerModule : ServerModule
{
    public override string Id => AgentModules.Metrics;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion) =>
        context.Features.IsOn(Feature.Metrics) && context.Older(Id, ModuleActions.Sample, context.Timing(Timings.Metrics))
            ? new PlannedTask(ModuleActions.Sample)
            : null;

    public override Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (!context.Features.IsOn(Feature.Metrics) || PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.HostMetrics) is not { } metrics)
        {
            return Task.FromResult<JsonElement?>(null);
        }

        // Begrenzen: ein Agent soll die Datenbank nicht mit Listen füllen können
        metrics = metrics with
        {
            Disks = metrics.Disks.Take(32).Select(d => d with { Mount = AgentEndpointsTruncate(d.Mount, 200), FileSystem = d.FileSystem is null ? null : AgentEndpointsTruncate(d.FileSystem, 20) }).ToList(),
            Containers = metrics.Containers?.Take(300).Select(c => c with { Name = AgentEndpointsTruncate(c.Name, 200) }).ToList(),
            Load = metrics.Load?.Take(3).ToList(),
        };
        host.Metrics = JsonSerializer.Serialize(metrics, PullJsonContext.Default.HostMetrics);
        host.MetricsAt = context.Now;
        context.Db.MetricSamples.Add(new HostMetricSample
        {
            HostId = host.Id,
            At = context.Now,
            Cpu = (float)Math.Clamp(metrics.CpuPercent, 0, 100),
            Memory = MetricsHistory.Percent(metrics.MemoryUsed, metrics.MemoryTotal),
            Disk = MetricsHistory.FullestDisk(metrics),
        });
        return Task.FromResult<JsonElement?>(null);
    }

    private static string AgentEndpointsTruncate(string value, int max) => Endpoints.AgentEndpoints.Truncate(value, max)!;
}
