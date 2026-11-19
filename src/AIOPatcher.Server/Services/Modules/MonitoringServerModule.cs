using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Monitoring;

namespace AIOPatcher.Server.Services.Modules;

/// <summary>monitoring.status stündlich und nach jeder Änderung: Wazuh-/Checkmk-Clients des Hosts (nur lesen).</summary>
public sealed class MonitoringServerModule : ServerModule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public override string Id => AgentModules.Monitoring;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        if (!ModuleJson.Supports(host, Id, ModuleActions.Status))
        {
            return null;
        }

        var last = context.LastRequested(Id, ModuleActions.Status);
        return last is null || context.Now - last >= context.Timing(Timings.Monitoring) || context.LastRequested(Id, ModuleActions.Change) > last
            ? new PlannedTask(ModuleActions.Status)
            : null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Status && PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.MonitoringStatus) is { } status)
        {
            await MonitoringService.StoreAsync(context.Db, host, status, context.Now, ct, context.Notifier);
        }

        return null;
    }
}
