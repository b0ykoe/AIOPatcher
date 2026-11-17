using System.Net;
using AIOPatcher.Agent.Compliance;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Maintenance;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Modules;

// Die Module des Agents. Jedes übersetzt einen Auftrag in vorhandene Plattform-Logik (Inventar, Patcher,
// Compliance-Provider, Self-Update) und liefert die Antwort zurück.

file static class Json
{
    public static TaskOutcome Ok<T>(string summary, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        new(JobOutcome.Succeeded, summary, PullJsonContext.ToElement(payload, type));

    public static TaskOutcome From(JobExecutionResult result) =>
        new(result.Outcome, result.Summary,
            result.ScannedUpdates is { } updates ? PullJsonContext.ToElement(new UpdateScanResult(updates, result.BrokenSources), PullJsonContext.Default.UpdateScanResult) : null,
            result.RebootRequested);
}

/// <summary>system.describe: Systemdaten, IPs, Module und Ergebnis des letzten Self-Updates.</summary>
public sealed class SystemModule(IPlatformInventory inventory, SelfMaintenance maintenance, Func<ModuleHost> host) : IAgentModule
{
    public string Id => AgentModules.System;
    public bool Available => true;
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Describe];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var info = await inventory.GetSystemInfoAsync(ct);
        var report = maintenance.ReportForDescribe();
        var description = new SystemDescription(
            AgentState.AgentVersion, Dns.GetHostName(), PlatformInfo.OsFamily, info.OsName, info.OsVersion, info.KernelVersion,
            TimeSpan.FromMilliseconds(Environment.TickCount64), info.RebootRequired, info.NativeAutoUpdatesEnabled,
            NetworkInventory.Collect(), SelfMaintenance.Architecture, host().Describe(context.Policy), report);
        var outcome = Json.Ok($"{info.OsName} {info.OsVersion}", description, PullJsonContext.Default.SystemDescription);
        return report is null ? outcome : outcome with { AfterAnswer = _ => { maintenance.ReportDelivered(); return Task.CompletedTask; } };
    }
}

/// <summary>updates.scan / install / reboot über die bestehende Job-Logik (Policy-Prüfung inklusive).</summary>
public sealed class UpdatesModule(JobExecutor executor) : IAgentModule
{
    public string Id => AgentModules.Updates;
    public bool Available => true;
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var job = task.Action switch
        {
            ModuleActions.Scan => new AgentJob(task.TaskId, JobType.Scan, PatchScope.Security, false),
            ModuleActions.Reboot => new AgentJob(task.TaskId, JobType.Reboot, PatchScope.Security, true),
            _ => PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.InstallRequest) is { } install
                ? new AgentJob(task.TaskId, JobType.Patch, install.Scope, install.AllowReboot, install.Holds, FixBrokenSources: install.FixBrokenSources)
                : null,
        };

        return job is null
            ? TaskOutcome.Rejected("Install task without parameters.")
            : Json.From(await executor.ExecuteAsync(job, context.Policy, context.Log, ct));
    }
}

/// <summary>software.inventory / appupdate (winget).</summary>
public sealed class SoftwareModule(IPlatformInventory inventory, JobExecutor executor) : IAgentModule
{
    public string Id => AgentModules.Software;
    public bool Available => true;
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Inventory, ModuleActions.AppUpdate];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action == ModuleActions.AppUpdate)
        {
            var request = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.AppUpdateRequest);
            return Json.From(await executor.ExecuteAsync(
                new AgentJob(task.TaskId, JobType.AppUpdate, PatchScope.All, false, Packages: request?.Packages ?? []), context.Policy, context.Log, ct));
        }

        var software = await inventory.ScanSoftwareAsync(ct);
        return software is null
            ? TaskOutcome.Rejected("Software inventory is not available on this platform.")
            : Json.Ok($"{software.Count} programs/packages", new SoftwareList(software), PullJsonContext.Default.SoftwareList);
    }
}

/// <summary>docker.inventory: laufende Container (nur lesen).</summary>
public sealed class DockerModule : IAgentModule
{
    public string Id => AgentModules.Docker;
    public bool Available => OperatingSystem.IsLinux() && (File.Exists("/usr/bin/docker") || File.Exists("/usr/local/bin/docker"));
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Inventory];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var containers = await DockerInventory.CollectAsync(ct);
        return containers is null
            ? new TaskOutcome(JobOutcome.Failed, "Docker daemon not reachable.")
            : Json.Ok($"{containers.Count} containers", new ContainerList(containers), PullJsonContext.Default.ContainerList);
    }
}

/// <summary>Gemeinsame Basis für firewall, remoteaccess und users: lesen + Systemänderungen über den JobExecutor.</summary>
public abstract class ComplianceModule(IComplianceProvider provider, JobExecutor executor) : IAgentModule
{
    public abstract string Id { get; }
    public bool Available => true;
    public abstract IReadOnlyList<string> Actions { get; }
    protected IComplianceProvider Provider => provider;

    protected abstract Task<TaskOutcome> ReadAsync(CancellationToken ct);

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action != ModuleActions.Change)
        {
            return await ReadAsync(ct);
        }

        var change = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.SystemChange);
        if (change is null || AgentModules.ForChange(change.Kind) != Id)
        {
            return TaskOutcome.Rejected($"Change does not belong to module {Id}.");
        }

        return Json.From(await executor.ExecuteAsync(new AgentJob(task.TaskId, JobType.SystemChange, PatchScope.Security, false, Change: change), context.Policy, context.Log, ct));
    }
}

public sealed class FirewallModule(IComplianceProvider provider, JobExecutor executor) : ComplianceModule(provider, executor)
{
    public override string Id => AgentModules.Firewall;
    public override IReadOnlyList<string> Actions { get; } = [ModuleActions.Status, ModuleActions.Change];

    protected override async Task<TaskOutcome> ReadAsync(CancellationToken ct)
    {
        var status = await Provider.FirewallAsync(ct);
        return Json.Ok($"{status.Product} {(status.Enabled ? "on" : "off")}", status, PullJsonContext.Default.FirewallStatus);
    }
}

public sealed class RemoteAccessModule(IComplianceProvider provider, JobExecutor executor) : ComplianceModule(provider, executor)
{
    public override string Id => AgentModules.RemoteAccess;
    public override IReadOnlyList<string> Actions { get; } = [ModuleActions.Status, ModuleActions.Change];

    protected override async Task<TaskOutcome> ReadAsync(CancellationToken ct)
    {
        var settings = await Provider.RemoteAccessAsync(ct);
        return Json.Ok($"{settings.Count(s => !s.Compliant)} findings", new RemoteAccessList(settings), PullJsonContext.Default.RemoteAccessList);
    }
}

public sealed class UsersModule(IComplianceProvider provider, JobExecutor executor) : ComplianceModule(provider, executor)
{
    public override string Id => AgentModules.Users;
    public override IReadOnlyList<string> Actions { get; } = [ModuleActions.List, ModuleActions.Change];

    protected override async Task<TaskOutcome> ReadAsync(CancellationToken ct)
    {
        var users = await Provider.UsersAsync(ct);
        return Json.Ok($"{users.Count} users", new UserList(users), PullJsonContext.Default.UserList);
    }
}

/// <summary>agent.update (Self-Update) und agent.renew (Client-Zertifikat). Neustart erst nach der Antwort.</summary>
public sealed class AgentSelfModule(SelfMaintenance maintenance, AgentState state) : IAgentModule
{
    public string Id => AgentModules.Agent;
    public bool Available => true;
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Update, ModuleActions.Renew];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Renew)
        {
            var (key, csr) = SelfMaintenance.CreateRenewal();
            return new TaskOutcome(JobOutcome.Succeeded, "Certificate signing request created.",
                PullJsonContext.ToElement(new RenewAnswer(csr), PullJsonContext.Default.RenewAnswer),
                AfterAnswer: response =>
                {
                    using (key)
                    {
                        if (PullJsonContext.FromElement(response.Payload, PullJsonContext.Default.RenewResponse) is { } renewed)
                        {
                            maintenance.CompleteRenewal(renewed.ClientCertificatePem, key);
                        }
                    }

                    return Task.CompletedTask;
                });
        }

        if (PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.AgentUpdateOffer) is not { } offer)
        {
            return TaskOutcome.Rejected("Update task without offer.");
        }

        using var client = ServerClient.FromState(state); // nur für den Download (GET)
        var (installed, message) = await maintenance.TryUpdateAsync(client, offer, ct);
        context.Log($"[agent] {message}");
        return installed
            ? new TaskOutcome(JobOutcome.Succeeded, message, AfterAnswer: _ => { maintenance.RestartAfterUpdate(); return Task.CompletedTask; })
            : new TaskOutcome(JobOutcome.Rejected, message);
    }
}
