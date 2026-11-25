using System.Net;
using AIOPatcher.Agent.Compliance;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Platforms;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Maintenance;
using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Agent.Platforms.Monitoring;
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
            NetworkInventory.Collect(), SelfMaintenance.Architecture, host().Describe(context.Policy), report, context.Policy.ToReport());
        var outcome = Json.Ok($"{info.OsName} {info.OsVersion}", description, PullJsonContext.Default.SystemDescription);
        return report is null ? outcome : outcome with { AfterAnswer = _ => { maintenance.ReportDelivered(); return Task.CompletedTask; } };
    }
}

/// <summary>updates.scan / install / reboot / change (eigene Update-Automatik) über die bestehende Job-Logik (Policy-Prüfung inklusive).</summary>
public sealed class UpdatesModule(JobExecutor executor) : IAgentModule
{
    public string Id => AgentModules.Updates;
    public bool Available => true;
    public IReadOnlyList<string> Actions { get; } = AptChangelog.Supported
        ? [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot, ModuleActions.Change, ModuleActions.Changelog]
        : [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot, ModuleActions.Change];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Changelog)
        {
            if (!AptChangelog.Supported || PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.ChangelogRequest) is not { } request)
            {
                return TaskOutcome.Rejected("Changelogs are only available with apt.");
            }

            var changelogs = await AptChangelog.FetchAsync(request.Packages, ct);
            return Json.Ok($"{changelogs.Count(c => c.Text is not null)} of {changelogs.Count} changelogs", new ChangelogResult(changelogs), PullJsonContext.Default.ChangelogResult);
        }

        if (task.Action == ModuleActions.Change)
        {
            var change = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.SystemChange);
            return NativeAutoUpdates.IsChange(change)
                ? Json.From(await executor.ExecuteAsync(new AgentJob(task.TaskId, JobType.SystemChange, PatchScope.Security, false, Change: change), context.Policy, context.Log, ct))
                : TaskOutcome.Rejected("Change does not belong to module updates.");
        }

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

/// <summary>
/// docker.inventory: alle Container mit Status; docker.packages: Paketlisten von Images (für den CVE-Abgleich);
/// docker.appupdate: einen Compose-Container aktualisieren; docker.appupdatemany: mehrere, je Compose-Projekt gebündelt;
/// docker.recipe: Update-Rezept der App ausführen (z. B. mailcow update.sh).
/// </summary>
public sealed class DockerModule : IAgentModule
{
    public string Id => AgentModules.Docker;
    public bool Available => OperatingSystem.IsLinux() && (File.Exists("/usr/bin/docker") || File.Exists("/usr/local/bin/docker"));
    public IReadOnlyList<string> Actions { get; } =
        [ModuleActions.Inventory, ModuleActions.AppUpdate, ModuleActions.AppUpdateMany, ModuleActions.Packages, ModuleActions.Recipe];

    /// <summary>Container aktualisieren gilt wie Programm-Updates: allowAppUpdates und allowPatch All.</summary>
    public static string? PolicyRejects(AgentPolicy policy) =>
        !policy.AllowAppUpdates ? "Local policy forbids program and container updates (allowAppUpdates: false)."
        : policy.AllowPatch != PatchPermission.All ? "Local policy does not allow container updates (needs allowPatch: All)."
        : null;

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action is ModuleActions.AppUpdate or ModuleActions.AppUpdateMany)
        {
            if (PolicyRejects(context.Policy) is { } rejected)
            {
                context.Log($"[agent] Rejected: {rejected}");
                return TaskOutcome.Rejected(rejected);
            }

            var names = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.AppUpdateRequest)?.Packages ?? [];
            if (task.Action == ModuleActions.AppUpdate)
            {
                names = names.Take(1).ToList();
            }

            return names.Count == 0
                ? TaskOutcome.Rejected("No container given.")
                : Json.From(await DockerUpdater.UpdateManyAsync(names, context.Log, ct));
        }

        if (task.Action == ModuleActions.Recipe)
        {
            if (DockerRecipes.PolicyRejects(context.Policy) is { } rejected)
            {
                context.Log($"[agent] Rejected: {rejected}");
                return TaskOutcome.Rejected(rejected);
            }

            return PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.RecipeUpdateRequest) is { } recipe
                ? Json.From(await DockerRecipes.RunAsync(recipe, context.Log, ct))
                : TaskOutcome.Rejected("Recipe task without parameters.");
        }

        if (task.Action == ModuleActions.Packages)
        {
            var request = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.ImagePackagesRequest);
            if (request is null || request.Images.Count == 0)
            {
                return TaskOutcome.Rejected("No images given.");
            }

            var images = await DockerImagePackages.CollectAsync(request.Images, ct);
            return Json.Ok($"{images.Count} images, {images.Sum(i => i.Packages.Count)} packages", new ImagePackagesResult(images), PullJsonContext.Default.ImagePackagesResult);
        }

        var containers = await DockerInventory.CollectAsync(ct);
        return containers is null
            ? new TaskOutcome(JobOutcome.Failed, "Docker daemon not reachable.")
            : Json.Ok($"{containers.Count} containers", new ContainerList(DockerRecipes.WithRecipes(containers, DockerRecipes.PolicyRejects(context.Policy) is null, File.Exists)), PullJsonContext.Default.ContainerList);
    }
}

/// <summary>services.list: laufende Autostart-Dienste und Container (nur lesen), für den Vergleich vor/nach einem Neustart.</summary>
public sealed class ServicesModule : IAgentModule
{
    public string Id => AgentModules.Services;
    public bool Available => RunningServicesCollector.Available;
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.List];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var running = await RunningServicesCollector.CollectAsync(ct);
        var summary = $"{running.Services.Count} services" + (running.Containers is { } c ? $", {c.Count} containers" : "");
        return Json.Ok(summary, running, PullJsonContext.Default.RunningServices);
    }
}

/// <summary>metrics.sample: CPU, RAM, Datenträger und Container-Auslastung (nur lesen).</summary>
public sealed class MetricsModule : IAgentModule
{
    public string Id => AgentModules.Metrics;
    public bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Sample];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var metrics = await MetricsCollector.CollectAsync(ct);
        var memory = metrics.MemoryTotal > 0 ? 100.0 * metrics.MemoryUsed / metrics.MemoryTotal : 0;
        return Json.Ok($"CPU {metrics.CpuPercent:0}%, RAM {memory:0}%", metrics, PullJsonContext.Default.HostMetrics);
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

/// <summary>
/// monitoring.status: installierte Wazuh-/Checkmk-Clients (immer erlaubt); monitoring.change: installieren, umstellen,
/// deinstallieren. Ändern braucht policy.json "allowMonitoringChanges".
/// </summary>
public sealed class MonitoringModule(AgentState state) : IAgentModule
{
    public string Id => AgentModules.Monitoring;
    public bool Available => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Status, ModuleActions.Change];

    public static string? PolicyRejects(AgentPolicy policy) => policy.AllowMonitoringChanges
        ? null
        : "Local policy forbids changing monitoring clients. To allow it on this host: aiopatcher-agent policy set allowMonitoringChanges true";

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Status)
        {
            var clients = new List<MonitoringClient>();
            foreach (var detect in new Func<CancellationToken, Task<MonitoringClient?>>[] { WazuhClient.DetectAsync, CheckmkClient.DetectAsync })
            {
                try
                {
                    if (await detect(ct) is { } client)
                    {
                        clients.Add(client);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    context.Log($"[agent] Detecting a monitoring client failed: {e.Message}");
                }
            }

            var summary = clients.Count == 0 ? "no monitoring clients" : string.Join(", ", clients.Select(c => $"{c.Product} {c.Version}".Trim()));
            return Json.Ok(summary, new MonitoringStatus(clients, context.Policy.AllowMonitoringChanges), PullJsonContext.Default.MonitoringStatus);
        }

        var change = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.SystemChange);
        if (change?.Monitoring is not { } spec || AgentModules.ForChange(change.Kind) != Id)
        {
            return TaskOutcome.Rejected("Change does not belong to module monitoring.");
        }

        if (PolicyRejects(context.Policy) is { } rejected)
        {
            context.Log($"[agent] Rejected: {rejected}");
            return TaskOutcome.Rejected(rejected);
        }

        context.Log($"[agent] {change.Kind}: {spec.Product}");
        var result = (change.Kind, spec.Product) switch
        {
            (SystemChangeKind.MonitoringConfigure, MonitoringProducts.Wazuh) => await WazuhClient.ConfigureAsync(spec, change.Secret, state.Directory, context.Log, ct),
            (SystemChangeKind.MonitoringConfigure, MonitoringProducts.Checkmk) => await CheckmkClient.ConfigureAsync(spec, change.Secret, state.Directory, context.Log, ct),
            (SystemChangeKind.MonitoringInstall, _) => await MonitoringInstaller.InstallAsync(state, spec, change.Secret, context.Log, ct),
            (SystemChangeKind.MonitoringUninstall, _) => await MonitoringInstaller.UninstallAsync(spec.Product, state.Directory, context.Log, ct),
            _ => new JobExecutionResult(JobOutcome.Rejected, $"Unknown monitoring product {spec.Product}."),
        };
        return Json.From(result);
    }
}

/// <summary>
/// serverupdate.update: den AIOPatcher-Server auf diesem Host mit aiopatcher-update aktualisieren (aus dem Setup: neuer Code
/// bzw. neues Image, Sicherung, Neustart, Rückfall bei Fehlern). Nur verfügbar, wo das Setup den Befehl eingerichtet hat.
/// Braucht policy.json "allowServerUpdate". Während des Neustarts ist der Server kurz weg; das Ergebnis wird nachgeliefert.
/// </summary>
public sealed class ServerUpdateModule : IAgentModule
{
    public const string Command = "/usr/local/bin/aiopatcher-update";
    public const string Compose = "/opt/aiopatcher/docker-compose.yml";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(45);

    public string Id => AgentModules.ServerUpdate;
    public bool Available => OperatingSystem.IsLinux() && File.Exists(Command) && File.Exists(Compose);
    public IReadOnlyList<string> Actions { get; } = [ModuleActions.Update];

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        if (!context.Policy.AllowServerUpdate)
        {
            const string rejected = "Local policy forbids updating the AIOPatcher server from the dashboard. To allow it on this host: aiopatcher-agent policy set allowServerUpdate true";
            context.Log($"[agent] Rejected: {rejected}");
            return TaskOutcome.Rejected(rejected);
        }

        context.Log($"[agent] Running {Command} (the server restarts; the result follows once it is back).");
        string? done = null;
        var code = await ProcessRunner.RunStreamingAsync(Command, [], Timeout, line =>
        {
            var clean = StripAnsi(line);
            if (clean.Contains("Done: AIOPatcher", StringComparison.Ordinal))
            {
                done = clean.Trim().TrimStart('=', '>', ' ');
            }

            context.Log(clean);
        }, ct);

        return code == 0
            ? new TaskOutcome(JobOutcome.Succeeded, done ?? "AIOPatcher updated.")
            : new TaskOutcome(JobOutcome.Failed, $"aiopatcher-update failed (exit code {code}); see the log. If the new version did not start, the previous one was restored.");
    }

    public static string StripAnsi(string line) => System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "");
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
