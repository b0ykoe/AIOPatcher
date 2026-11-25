using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Modules;

/// <summary>Alles, was ein Server-Modul beim Planen und Auswerten braucht.</summary>
public sealed class ModuleContext(
    AppDbContext db,
    Notifier notifier,
    FeatureFlags features,
    AgentDownloads downloads,
    CertificateAuthority? ca,
    DateTime now,
    IReadOnlyDictionary<(string Module, string Action), DateTime> lastRequested,
    ScanSchedule? schedule = null,
    UpdateChangelogs? changelogs = null)
{
    public AppDbContext Db => db;
    public Notifier Notifier => notifier;
    public FeatureFlags Features => features;
    public AgentDownloads Downloads => downloads;
    public CertificateAuthority? Ca => ca;
    public DateTime Now => now;

    /// <summary>Changelogs anstehender Updates (null in Tests ohne diesen Teil).</summary>
    public UpdateChangelogs? Changelogs => changelogs;

    /// <summary>Update-Scan-Intervall des Hosts (Einstellungen bzw. je Host; ohne Schedule der feste Standard).</summary>
    public TimeSpan UpdateScanInterval(ManagedHost host) => schedule?.UpdateScanInterval(host) ?? ServerModule.InventoryInterval;

    /// <summary>Abstand zwischen Agent-Update-Angeboten (Einstellungen; ohne Schedule der feste Standard).</summary>
    public TimeSpan AgentUpdateInterval => schedule?.AgentUpdateInterval ?? ServerModule.InventoryInterval;

    /// <summary>Einstellbares Intervall (Einstellungen → Intervalle), ohne Einstellungen der Standard.</summary>
    public TimeSpan Timing(string key) => schedule?.Interval(key) ?? Timings.Default(key);

    /// <summary>Wann der Server diese Aktion bei diesem Host zuletzt angefordert hat (aus AgentTasks).</summary>
    public DateTime? LastRequested(string module, string action) =>
        lastRequested.TryGetValue((module, action), out var at) ? at : null;

    /// <summary>Länger als interval nicht angefordert (oder nie)?</summary>
    public bool Older(string module, string action, TimeSpan interval) =>
        LastRequested(module, action) is not { } at || now - at >= interval;
}

/// <summary>Geplanter Auftrag eines Moduls.</summary>
public sealed record PlannedTask(string Action, JsonElement? Payload = null);

/// <summary>
/// Server-Seite eines Agent-Moduls: entscheidet, wann Daten angefordert werden, und wertet Antworten aus.
/// Gegenstück zu den Modulen im Agent (gleiche Ids, siehe <see cref="AgentModules"/>).
/// </summary>
public abstract class ServerModule
{
    public static readonly TimeSpan InventoryInterval = TimeSpan.FromHours(6);

    public abstract string Id { get; }

    /// <summary>Daten anfordern? null = gerade nicht.</summary>
    public virtual PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion) => null;

    /// <summary>Antwort auswerten (nur bei Erfolg). Rückgabe: Daten für den Agent (z. B. neues Zertifikat).</summary>
    public virtual Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct) =>
        Task.FromResult<JsonElement?>(null);

    /// <summary>Reihenfolge = Priorität beim Anfordern.</summary>
    public static readonly IReadOnlyList<ServerModule> All =
    [
        new SystemServerModule(),
        new AgentServerModule(renewOnly: true),
        new UpdatesServerModule(),
        new SoftwareServerModule(),
        new DockerServerModule(),
        new ServicesServerModule(),
        new MetricsServerModule(),
        new ComplianceServerModule(AgentModules.Firewall, ModuleActions.Status),
        new ComplianceServerModule(AgentModules.RemoteAccess, ModuleActions.Status),
        new ComplianceServerModule(AgentModules.Users, ModuleActions.List),
        new MonitoringServerModule(),
        new AgentServerModule(renewOnly: false),
    ];

    public static ServerModule? Find(string id, string action) =>
        id == AgentModules.Agent
            ? All.OfType<AgentServerModule>().First(m => m.RenewOnly == (action == ModuleActions.Renew))
            : All.FirstOrDefault(m => m.Id == id);
}

public sealed class SystemServerModule : ServerModule
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public override string Id => AgentModules.System;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        var versionChanged = polledVersion is not null && polledVersion != host.AgentVersion;
        var restarted = host.AgentRestartedAt is { } at && !(context.LastRequested(Id, ModuleActions.Describe) >= at);
        // Update-Automatik umgestellt: Stand gleich neu melden lassen
        var autoUpdatesChanged = context.LastRequested(AgentModules.Updates, ModuleActions.Change) > context.LastRequested(Id, ModuleActions.Describe);
        return context.Older(Id, ModuleActions.Describe, context.Timing(Timings.System)) || restarted || autoUpdatesChanged || versionChanged && context.Older(Id, ModuleActions.Describe, TimeSpan.FromMinutes(1))
            ? new PlannedTask(ModuleActions.Describe)
            : null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.SystemDescription) is not { } d)
        {
            return null;
        }

        host.AgentVersion = AgentEndpoints.Truncate(d.AgentVersion, 32);
        host.Hostname = AgentEndpoints.Truncate(d.Hostname, 255);
        host.OsFamily = d.OsFamily;
        host.OsName = AgentEndpoints.Truncate(d.OsName, 200);
        host.OsVersion = AgentEndpoints.Truncate(d.OsVersion, 100);
        host.KernelVersion = d.KernelVersion is null ? null : AgentEndpoints.Truncate(d.KernelVersion, 100);
        host.Uptime = d.Uptime;
        host.BootedAt = context.Now - d.Uptime;
        host.RebootRequired = d.RebootRequired;
        host.NativeAutoUpdatesEnabled = d.NativeAutoUpdatesEnabled;
        host.Architecture = d.Architecture is "x64" or "arm64" ? d.Architecture : null;
        host.ReportedModules = JsonSerializer.Serialize(d.Modules.Take(32).ToList(), ModuleJson.Default.ListModuleInfo);
        host.ReportedPolicy = d.Policy is { } policy ? PolicyReport.Serialize(policy) : null;
        await AgentEndpoints.ReplaceAddressesAsync(context.Db, host, d.Addresses ?? [], context.Now, ct);
        if (d.UpdateReport is { } report)
        {
            AgentEndpoints.RecordUpdateReport(context.Db, context.Notifier, host, report, context.Now);
        }

        // Patchnotes nachholen, die noch fehlen (z. B. Agent kann es erst seit dem letzten Update, Server neu gestartet)
        if (context.Changelogs is { } changelogs && ModuleJson.Supports(host, AgentModules.Updates, ModuleActions.Changelog))
        {
            await changelogs.RequestMissingAsync(context.Db, host, context.Now, ct);
        }

        return null;
    }
}

public sealed class UpdatesServerModule : ServerModule
{
    public override string Id => AgentModules.Updates;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        // Nach Installation/Neustart gleich neu scannen, sonst im eingestellten Intervall (Standard 6 Stunden).
        var lastScan = context.LastRequested(Id, ModuleActions.Scan);
        var lastChange = new[] { context.LastRequested(Id, ModuleActions.Install), context.LastRequested(Id, ModuleActions.Reboot) }.Max();
        if (lastScan is null || context.Now - lastScan >= context.UpdateScanInterval(host) || lastChange > lastScan)
        {
            return new PlannedTask(ModuleActions.Scan);
        }

        // Patchnotes vorgemerkter Updates holen (nach einem Scan oder auf Klick auf "i")
        if (context.Changelogs is { } changelogs && changelogs.HasPending(host.Id) && ModuleJson.Supports(host, Id, ModuleActions.Changelog)
            && changelogs.TakePending(host.Id, 40) is { Count: > 0 } packages)
        {
            return new PlannedTask(ModuleActions.Changelog, PullJsonContext.ToElement(new ChangelogRequest(packages), PullJsonContext.Default.ChangelogRequest));
        }

        return null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Changelog)
        {
            if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.ChangelogResult) is { } changelogs)
            {
                if (context.Changelogs is { } store)
                {
                    await store.StoreAsync(context.Db, host.Id, changelogs.Changelogs.Take(100), context.Now, ct);
                }
            }

            return null;
        }

        if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.UpdateScanResult) is not { } scan)
        {
            return null;
        }

        // Patchnotes der angebotenen Versionen vormerken (nur was noch nicht da ist); der nächste Abruf holt sie
        if (context.Changelogs is { } cache && ModuleJson.Supports(host, Id, ModuleActions.Changelog))
        {
            await cache.RequestAsync(context.Db, host.Id,
                scan.Updates.Where(u => !string.IsNullOrEmpty(u.AvailableVersion)).Select(u => (u.Name, u.AvailableVersion!)), context.Now, ct);
        }

        context.Changelogs?.Touched(host.Id); // KI-Zusammenfassung des Hosts auf den neuen Stand bringen

        host.BrokenAptSources = scan.BrokenSources is { Count: > 0 } broken
            ? System.Text.Json.JsonSerializer.Serialize(broken.Take(20).ToList(), PullJsonContext.Default.ListBrokenAptSource)
            : null;
        var newSecurity = await AgentEndpoints.ReplaceUpdatesAsync(context.Db, host, scan.Updates, context.Now, ct);
        if (newSecurity.Count > 0)
        {
            context.Notifier.Publish(new Notification(NotificationEvents.NewSecurityUpdates,
                L.T($"{host.Name}: {newSecurity.Count} neue Security-Updates", $"{host.Name}: {newSecurity.Count} new security updates"),
                string.Join(", ", newSecurity.Take(10)) + (newSecurity.Count > 10 ? " …" : ""),
                NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
        }

        return null;
    }
}

public sealed class SoftwareServerModule : ServerModule
{
    public override string Id => AgentModules.Software;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        // auch nach jedem Update-Scan: installierte Versionen sind Grundlage für den CVE-Abgleich ohne Update
        var last = context.LastRequested(Id, ModuleActions.Inventory);
        return last is null || context.Now - last >= context.Timing(Timings.Inventory) || context.LastRequested(Id, ModuleActions.AppUpdate) > last
               || context.LastRequested(AgentModules.Updates, ModuleActions.Scan) > last
            ? new PlannedTask(ModuleActions.Inventory)
            : null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.SoftwareList) is { } list)
        {
            await SoftwareEndpoints.ReplaceAsync(context.Db, host, list.Software, ct);
        }

        return null;
    }
}

public sealed class DockerServerModule : ServerModule
{
    public override string Id => AgentModules.Docker;

    /// <summary>Container ändern sich oft (Update, neu erstellt): alle 30 Minuten, nach einem Scan und auf Wunsch sofort.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>Images, deren Paketliste der Agent beim nächsten Abruf lesen soll (je Host; nach einem Neustart neu ermittelt).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<ImagePackagesTarget>> PendingPackages = new();

    /// <summary>Paketliste mit Fehler (z. B. Container gerade weg) erst nach dieser Zeit erneut anfordern.</summary>
    public static readonly TimeSpan RetryPackagesAfter = TimeSpan.FromDays(1);

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        var last = context.LastRequested(Id, ModuleActions.Inventory);
        var asked = host.ContainersRequestedAt > last || context.LastRequested(AgentModules.Updates, ModuleActions.Scan) > last
                    || context.LastRequested(Id, ModuleActions.AppUpdate) > last // nach "Container aktualisieren" gleich neu melden
                    || context.LastRequested(Id, ModuleActions.AppUpdateMany) > last;
        if (context.Older(Id, ModuleActions.Inventory, context.Timing(Timings.Containers)) || asked)
        {
            return new PlannedTask(ModuleActions.Inventory);
        }

        return ModuleJson.Supports(host, Id, ModuleActions.Packages) && PendingPackages.TryRemove(host.Id, out var images) && images.Count > 0
            ? new PlannedTask(ModuleActions.Packages, PullJsonContext.ToElement(new ImagePackagesRequest(images), PullJsonContext.Default.ImagePackagesRequest))
            : null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (task.Action == ModuleActions.Packages)
        {
            if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.ImagePackagesResult) is { } packages)
            {
                await StorePackagesAsync(context.Db, packages.Images, context.Now, ct);
            }

            return null;
        }

        if (PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.ContainerList) is { } list)
        {
            await ContainerUpdateService.ReplaceContainersAsync(context.Db, host, list.Containers, context.Now, ct, context.Notifier);
            await context.Db.SaveChangesAsync(ct);
            if (context.Features.IsOn(Feature.CveLookup) && ModuleJson.Supports(host, Id, ModuleActions.Packages))
            {
                await QueueMissingPackagesAsync(context.Db, host.Id, context.Now, ct);
            }
        }

        return null;
    }

    /// <summary>Images dieses Hosts ohne Paketliste vormerken (je Image ein Container, laufend oder gestoppt).</summary>
    public static async Task<int> QueueMissingPackagesAsync(AppDbContext db, Guid hostId, DateTime now, CancellationToken ct)
    {
        var containers = await db.Containers.AsNoTracking()
            .Where(c => c.HostId == hostId && c.ImageId != null && c.ContainerId != null)
            .Select(c => new { ImageId = c.ImageId!, ContainerId = c.ContainerId!, c.State })
            .ToListAsync(ct);
        var imageIds = containers.Select(c => c.ImageId).Distinct().ToList();
        var retryBefore = now - RetryPackagesAfter;
        var known = (await db.ImagePackageSets.AsNoTracking()
                .Where(s => imageIds.Contains(s.ImageId) && (s.Error == null || s.CollectedAt > retryBefore))
                .Select(s => s.ImageId)
                .ToListAsync(ct))
            .ToHashSet();
        var targets = containers
            .Where(c => !known.Contains(c.ImageId))
            .GroupBy(c => c.ImageId)
            .Select(g => g.OrderBy(c => c.State == "running" ? 0 : 1).First())
            .Take(20)
            .Select(c => new ImagePackagesTarget(c.ContainerId, c.ImageId))
            .ToList();
        if (targets.Count > 0)
        {
            PendingPackages[hostId] = targets;
        }

        return targets.Count;
    }

    /// <summary>Paketlisten speichern (je Image-ID, für alle Hosts); der CVE-Abgleich folgt im nächsten Lauf des CVE-Dienstes.</summary>
    public static async Task StorePackagesAsync(AppDbContext db, IEnumerable<ImagePackages> images, DateTime now, CancellationToken ct)
    {
        foreach (var image in images.Where(i => !string.IsNullOrWhiteSpace(i.ImageId) && i.ImageId.Length <= 100).Take(20))
        {
            var set = await db.ImagePackageSets.FindAsync([image.ImageId], ct);
            if (set is null)
            {
                db.ImagePackageSets.Add(set = new ImagePackageSet { ImageId = image.ImageId });
            }

            var packages = image.Packages.Take(5000).ToList();
            set.OsId = Cut(image.OsId, 40);
            set.OsVersion = Cut(image.OsVersion, 40);
            set.Format = Cut(image.Format, 10);
            set.Ecosystem = CveMapping.EcosystemForImage(image.OsId, image.OsVersion);
            set.Packages = JsonSerializer.Serialize(packages, PullJsonContext.Default.IReadOnlyListImagePackage);
            set.PackageCount = packages.Count;
            set.Error = Cut(image.Error, 300);
            set.CollectedAt = now;
            set.ScannedAt = null; // neu abgleichen
        }

        await db.SaveChangesAsync(ct);

        static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
    }
}

/// <summary>firewall / remoteaccess / users: lesen nur mit Schalter "Compliance-Prüfung"; nach einer Änderung gleich neu lesen.</summary>
public sealed class ComplianceServerModule(string id, string readAction) : ServerModule
{
    public override string Id => id;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        if (!context.Features.IsOn(Feature.ComplianceChecks))
        {
            return null;
        }

        var last = context.LastRequested(Id, readAction);
        return last is null || context.Now - last >= context.Timing(Timings.Inventory) || context.LastRequested(Id, ModuleActions.Change) > last
            ? new PlannedTask(readAction)
            : null;
    }

    public override async Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (task.Action != readAction || !context.Features.IsOn(Feature.ComplianceChecks))
        {
            return null;
        }

        switch (Id)
        {
            case AgentModules.Firewall when PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.FirewallStatus) is { } firewall:
                await ComplianceEndpoints.StorePartAsync(context.Db, host, context.Now, ct, firewall: firewall);
                break;
            case AgentModules.RemoteAccess when PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.RemoteAccessList) is { } remote:
                await ComplianceEndpoints.StorePartAsync(context.Db, host, context.Now, ct, remoteAccess: remote.Settings);
                break;
            case AgentModules.Users when PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.UserList) is { } users:
                await ComplianceEndpoints.StorePartAsync(context.Db, host, context.Now, ct, users: users.Users);
                break;
        }

        return null;
    }
}

/// <summary>agent.renew (Zertifikat läuft bald ab) und agent.update (neuere Version, Schalter Self-Update).</summary>
public sealed class AgentServerModule(bool renewOnly) : ServerModule
{
    public bool RenewOnly => renewOnly;

    public override string Id => AgentModules.Agent;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        if (renewOnly)
        {
            return host.CertificateExpiresAt - context.Now < AgentEndpoints.CertificateRenewBefore && context.Older(Id, ModuleActions.Renew, TimeSpan.FromHours(12))
                ? new PlannedTask(ModuleActions.Renew)
                : null;
        }

        // Im eingestellten Abstand anbieten (Standard 6 Stunden), außer "Jetzt aktualisieren" wurde seit dem letzten Angebot gedrückt
        var asked = host.AgentUpdateRequestedAt is { } requested && !(context.LastRequested(Id, ModuleActions.Update) >= requested);
        if (!context.Features.IsOn(Feature.AgentSelfUpdate) || !context.Older(Id, ModuleActions.Update, context.AgentUpdateInterval) && !asked)
        {
            return null;
        }

        var offer = context.Downloads.OfferFor(host.OsFamily, host.Architecture, host.AgentVersion, host.AgentUpdateBlockedVersion);
        return offer is null ? null : new PlannedTask(ModuleActions.Update, PullJsonContext.ToElement(offer, PullJsonContext.Default.AgentUpdateOffer));
    }

    public override Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (task.Action != ModuleActions.Renew || context.Ca is null
            || PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.RenewAnswer) is not { } answer)
        {
            return Task.FromResult<JsonElement?>(null);
        }

        var certificate = context.Ca.IssueClientCertificate(answer.CertificateSigningRequestPem ?? "", host.Id, host.SiteId);
        host.PendingCertificateThumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
        host.PendingCertificateExpiresAt = certificate.NotAfter.ToUniversalTime();
        context.Db.AuditEntries.Add(new AuditEntry { Actor = $"agent:{host.Name}", Action = "agent.certificate.issued", SiteId = host.SiteId, HostId = host.Id, Details = $"renewal (pull), valid until {host.PendingCertificateExpiresAt:u}" });
        return Task.FromResult<JsonElement?>(PullJsonContext.ToElement(new RenewResponse(certificate.ExportCertificatePem()), PullJsonContext.Default.RenewResponse));
    }
}

/// <summary>Module eines Hosts (laut Agent) und ob der Server dafür Aufträge vergeben darf.</summary>
public static class ModuleJson
{
    public static readonly ModuleJsonContext Default = ModuleJsonContext.Default;

    public static List<ModuleInfo> Reported(ManagedHost host)
    {
        if (string.IsNullOrEmpty(host.ReportedModules))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(host.ReportedModules, ModuleJsonContext.Default.ListModuleInfo) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static HashSet<string> DisabledInDashboard(ManagedHost host) =>
        (host.DisabledModules ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    /// <summary>
    /// Kann der Agent diese Aktion? (Lite-Agents für alte Systeme melden z. B. kein agent.update.) Vor der ersten
    /// Beschreibung kennt der Server nur system.describe.
    /// </summary>
    public static bool Supports(ManagedHost host, string module, string action)
    {
        var reported = Reported(host);
        if (reported.Count == 0)
        {
            return module == AgentModules.System && action == ModuleActions.Describe;
        }

        return reported.Any(m => m.Id == module && (m.Actions.Count == 0 || m.Actions.Contains(action)));
    }

    /// <summary>Darf der Server Aufträge für dieses Modul schicken?</summary>
    public static bool Usable(ManagedHost host, string module)
    {
        if (AgentModules.Required.Contains(module))
        {
            return true;
        }

        return !DisabledInDashboard(host).Contains(module)
               && Reported(host).Any(m => m.Id == module && m.Available && !m.DisabledLocally);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ModuleInfo>))]
public sealed partial class ModuleJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
