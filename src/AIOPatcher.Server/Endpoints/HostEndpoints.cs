using System.Text.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Dashboard-API für Hosts, Enrollment-Tokens und die Übersicht.</summary>
public static class HostEndpoints
{
    public enum HostStatus
    {
        Online,
        Offline,
        Pending, // registriert, aber noch kein Check-in
        Revoked,
    }

    public sealed record HostSummaryDto(
        Guid Id,
        Guid SiteId,
        string SiteName,
        string Name,
        string Hostname,
        OsFamily OsFamily,
        string? OsName,
        string? OsVersion,
        string? AgentVersion,
        DateTime? LastSeenAt,
        DateTime? LastScanAt,
        HostStatus Status,
        bool RebootRequired,
        bool NativeAutoUpdatesEnabled,
        int PendingUpdates,
        int SecurityUpdates,
        AutoPatchDto AutoPatch,
        string? ScheduledRunbook,
        string? Tags,
        int KevUpdates,
        int ContainerUpdates,
        bool Mapped,
        Guid? PlanId = null,
        string? PlanGroup = null,
        PlanVia? PlanVia = null,
        UsageDto? Usage = null,
        string? Channel = null,
        string? AgentUpdate = null);

    /// <summary>Letzte Auslastung (für Listen): CPU %, RAM %, vollster Datenträger %.</summary>
    public sealed record UsageDto(DateTime At, double Cpu, float Memory, float? Disk);

    /// <summary>Host einem Patch-Plan (und optional einer Gruppe) zuordnen; PlanId null = aus allen Plänen nehmen (Regeln bleiben).</summary>
    public sealed record PlanAssignmentRequest(Guid? PlanId, Guid? GroupId);

    public sealed record BulkPlanAssignmentRequest(Guid[] HostIds, Guid? PlanId, Guid? GroupId);

    /// <summary>Days: Bitmaske Mo=1, Di=2, Mi=4, Do=8, Fr=16, Sa=32, So=64. Start in Ortszeit des Servers.</summary>
    public sealed record AutoPatchDto(
        bool Enabled,
        PatchScope Scope,
        bool AllowReboot,
        int Days,
        TimeOnly Start,
        int DurationMinutes,
        DateTime? NextWindowStart = null,
        DateTime? NextWindowEnd = null,
        DateTime? LastWindowStart = null,
        string? LastWindowNote = null,
        bool WindowOpenNow = false);


/// <param name="UpdateHosts">Hosts, deren Agent den AIOPatcher-Server aktualisieren kann (Setup mit aiopatcher-update).</param>
    /// <param name="RunningUpdateJob">Laufender bzw. wartender Server-Update-Job.</param>
    public sealed record ServerInfoDto(string Version, string TimeZone, DateTime NowUtc, string? LatestVersion, string? ReleaseUrl, bool UpdateAvailable,
        IReadOnlyList<ServerUpdateHostDto>? UpdateHosts = null, Guid? RunningUpdateJob = null);

    public sealed record ServerUpdateHostDto(Guid Id, string Name);

    public sealed record ServerUpdateRequest(Guid HostId);

    public sealed record UpdateDto(string Name, string? CurrentVersion, string? AvailableVersion, bool IsSecurity, string? Classification, DateTime FirstSeenAt, bool IsHeld,
        string? Cves, bool IsKev, double? MaxEpss);

    /// <param name="UpdateBlocker">Warum "Aktualisieren" nicht geht: "notCompose", "portainer", "noFile", "agent" (Agent zu alt); null = geht.</param>
    /// <param name="Recipe">Update-Rezept des Compose-Projekts (z. B. mailcow update.sh): ersetzt "Aktualisieren" durch das Skript der App.</param>
    /// <param name="LocalImage">Image aus keiner Registry (selbst gebaut); ohne <paramref name="BaseImage"/> nicht prüfbar.</param>
    /// <param name="BaseImage">Selbst gebaut per Compose: Basis-Image; UpdateAvailable bezieht sich dann darauf (Update = neu bauen).</param>
    public sealed record ContainerDto(string Name, string Image, string? State, bool UpdateAvailable, DateTime? CheckedAt, string? CheckError, bool Pinned, DateTime? SeenAt = null,
        string? Compose = null, string? UpdateBlocker = null, int? ExitCode = null, DateTime? StartedAt = null, DateTime? FinishedAt = null, string? Health = null,
        string? RestartPolicy = null, bool LocalImage = false, string? BaseImage = null, ContainerVulnsDto? Vulns = null, ContainerRecipeDto? Recipe = null);

    /// <param name="Allowed">Lokale Policy des Hosts erlaubt es (allowUpdateRecipes).</param>
    /// <param name="Note">Hinweis für den Bestätigungsdialog in der Sprache der Anfrage.</param>
    public sealed record ContainerRecipeDto(string Id, string Name, string App, string? Docs, string? Note, bool Allowed);

    /// <param name="State">"ok" (abgeglichen), "pending" (Paketliste oder Abgleich steht aus), "unsupported" (Image ohne
    /// prüfbare Paketdatenbank, Error sagt warum), "agent" (Agent zu alt), "off" (CVE-Abgleich ausgeschaltet).</param>
    public sealed record ContainerVulnsDto(string State, int Count, int Kev, double? MaxEpss, string? Os, int Packages, DateTime? ScannedAt, string? Error);

    public sealed record ContainerVulnDto(string Cve, string Package, string? InstalledVersion, bool IsKev, string? KevName, double? Epss, double? EpssPercentile);

    public sealed record ContainerVulnListDto(string Name, string Image, string? Os, int Packages, DateTime? ScannedAt, IReadOnlyList<ContainerVulnDto> Items);
    public sealed record AddressDto(string Address, int? PrefixLength, string? InterfaceName, string? SubnetName);

    public sealed record HostDetailDto(
        HostSummaryDto Summary,
        string? KernelVersion,
        TimeSpan? Uptime,
        DateTime EnrolledAt,
        DateTime CertificateExpiresAt,
        IReadOnlyList<UpdateDto> Updates,
        IReadOnlyList<AddressDto> Addresses,
        Guid? IntegrationId,
        string? IntegrationName,
        string? ExternalId,
        IReadOnlyList<ContainerDto> Containers,
        AgentUpdateDto? AgentUpdate = null,
        int AgentProtocol = 0,
        IReadOnlyList<HostModuleDto>? Modules = null,
        IReadOnlyList<BrokenAptSource>? BrokenSources = null,
        bool AutoFixSources = false,
        RebootCheckResult? RebootCheck = null,
        int? UpdateScanHours = null,
        int? ImageCheckHours = null,
        DateTime? LastScanAt = null);

    /// <summary>Modul des Agents: vorhanden, lokal (policy.json) oder im Dashboard abgeschaltet. Required = nicht abschaltbar.</summary>
    public sealed record HostModuleDto(string Id, bool Available, bool DisabledLocally, bool DisabledInDashboard, bool Required, IReadOnlyList<string> Actions);

    public sealed record ModuleToggleRequest(bool Enabled);

    /// <summary>
    /// Available: Server hat eine neuere Agent-Version. Blocked: diese ist hier gescheitert und wird nicht erneut angeboten.
    /// State/Reason/NextOfferAt: siehe <see cref="AgentUpdateStatus"/>.
    /// </summary>
    public sealed record AgentUpdateDto(
        string LatestVersion,
        bool Available,
        bool Blocked,
        AgentUpdateOutcome? LastOutcome,
        string? FromVersion,
        string? ToVersion,
        string? Message,
        DateTime? At,
        string State = AgentUpdateStatus.Current,
        string? Reason = null,
        DateTime? NextOfferAt = null);

    public sealed record OverviewDto(
        int Sites,
        int Hosts,
        int Online,
        int Offline,
        int PendingUpdates,
        int SecurityUpdates,
        int HostsWithSecurityUpdates,
        int RebootRequired,
        int NativeAutoUpdateConflicts,
        int AutoPatchHosts,
        int RunningJobs,
        FreezeInfoDto? ActiveFreeze,
        int KevHosts,
        int KevUpdates,
        int ContainerUpdates,
        int HostsWithoutPlan = 0);

    public sealed record FreezeInfoDto(Guid Id, string Name, Guid? SiteId, DateTime EndsAt);

    public sealed record CreateEnrollmentTokenRequest(Guid SiteId, string? Label);

    public sealed record EnrollmentTokenCreatedDto(
        Guid Id,
        string Token,
        DateTime ExpiresAt,
        string ServerUrl,
        string CaFingerprintSha256,
        string? WindowsInstallScriptSha256,
        string? PublicUrl = null,
        string? LinuxInstallScriptSha256 = null,
        string? AgentSha256 = null);

    public sealed record EnrollmentTokenDto(Guid Id, Guid SiteId, string? Label, DateTime CreatedAt, DateTime ExpiresAt);

    public static void MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);

        api.MapGet("/overview", GetOverview);
        api.MapGet("/hosts", GetHosts);
        api.MapGet("/hosts/{id:guid}", GetHost);
        op.MapPut("/hosts/{id:guid}/plan", (Guid id, PlanAssignmentRequest request, AppDbContext db, AuditLog audit, CancellationToken ct) =>
            AssignPlanAsync([id], request.PlanId, request.GroupId, db, audit, ct));
        op.MapPut("/hosts/plan", (BulkPlanAssignmentRequest request, AppDbContext db, AuditLog audit, CancellationToken ct) =>
            AssignPlanAsync(request.HostIds ?? [], request.PlanId, request.GroupId, db, audit, ct));
        api.MapGet("/server-info", async (ServerClock clock, ReleaseCheck releases, AppDbContext db, CancellationToken ct) =>
        {
            var hosts = (await db.Hosts.AsNoTracking().Where(h => !h.IsRevoked && h.ReportedModules != null && h.ReportedModules.Contains(AgentModules.ServerUpdate))
                    .OrderBy(h => h.Name).ToListAsync(ct))
                .Where(h => ModuleJson.Usable(h, AgentModules.ServerUpdate) && ModuleJson.Supports(h, AgentModules.ServerUpdate, ModuleActions.Update))
                .Select(h => new ServerUpdateHostDto(h.Id, h.Name))
                .ToList();
            var running = await db.Jobs.AsNoTracking()
                .Where(j => j.Type == JobType.ServerUpdate && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);
            return new ServerInfoDto(AgentDownloads.AgentVersion, clock.ZoneName, DateTime.UtcNow,
                releases.Latest?.Version, releases.Latest?.Url, releases.UpdateAvailable, hosts, running);
        });
        api.MapPost("/server/update", StartServerUpdate).RequireAuthorization(UserAccounts.AdminPolicy);
        op.MapPost("/hosts/{id:guid}/revoke", RevokeHost);
        op.MapPost("/hosts/{id:guid}/agent-update/retry", RetryAgentUpdate);
        op.MapPost("/hosts/{id:guid}/agent-update/now", UpdateAgentNow);
        op.MapPost("/hosts/agent-update/now", UpdateAllAgentsNow);
        op.MapDelete("/hosts/{id:guid}/reboot-check", DismissRebootCheck);
        op.MapPost("/hosts/{id:guid}/containers/recheck", RecheckContainers);
        api.MapGet("/hosts/{id:guid}/containers/{name}/vulnerabilities", ContainerVulnerabilities);
        op.MapPost("/hosts/{id:guid}/containers/{name}/update", UpdateContainer);
        op.MapPut("/hosts/{id:guid}/modules/{module}", ToggleModule);
        op.MapDelete("/hosts/{id:guid}", DeleteHost).RequireAuthorization(UserAccounts.AdminPolicy);

        api.MapGet("/enrollment-tokens", GetEnrollmentTokens);
        op.MapPost("/enrollment-tokens", CreateEnrollmentToken);
        op.MapDelete("/enrollment-tokens/{id:guid}", DeleteEnrollmentToken);
    }

    private static IQueryable<HostSummaryDto> ProjectSummaries(IQueryable<ManagedHost> hosts, IQueryable<ImageCheck> images, DateTime onlineSince) =>
        hosts.Select(h => new HostSummaryDto(
            h.Id,
            h.SiteId,
            h.Site!.Name,
            h.Name,
            h.Hostname,
            h.OsFamily,
            h.OsName,
            h.OsVersion,
            h.AgentVersion,
            h.LastSeenAt,
            h.LastScanAt,
            h.IsRevoked ? HostStatus.Revoked
                : h.LastSeenAt == null ? HostStatus.Pending
                : h.LastSeenAt >= onlineSince ? HostStatus.Online
                : HostStatus.Offline,
            h.RebootRequired,
            h.NativeAutoUpdatesEnabled,
            h.PendingUpdates.Count,
            h.PendingUpdates.Count(u => u.IsSecurity),
            new AutoPatchDto(false, PatchScope.Security, false, 0, new TimeOnly(3, 0), 0), // aus dem Patch-Plan, siehe EnrichAsync
            null,
            h.Tags,
            h.PendingUpdates.Count(u => u.IsKev),
            h.Containers.Count(c => c.LocalDigest != null && images.Any(i => i.Reference == c.Reference && i.RemoteDigest != null && !c.LocalDigest.Contains(i.RemoteDigest))),
            h.IntegrationId != null && h.ExternalId != null));

    /// <summary>
    /// Nach dem Laden ergänzen, was sich nicht in SQL rechnen lässt: Patch-Plan des Hosts (ausgewählt, per Regel oder
    /// "alle übrigen") samt nächstem Fenster, und die letzte Auslastung.
    /// </summary>
    public static async Task<List<HostSummaryDto>> EnrichAsync(AppDbContext db, IReadOnlyList<HostSummaryDto> summaries, ServerClock clock, CancellationToken ct)
    {
        var membership = await PatchPlans.ResolveAsync(db, ct);
        var planIds = membership.Values.Select(m => m.PlanId).Distinct().ToList();
        var plans = await db.Runbooks.AsNoTracking().Include(r => r.Waves).Where(r => planIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var ids = summaries.Select(s => s.Id).ToList();
        var metrics = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id) && h.MetricsAt != null)
            .Select(h => new { h.Id, h.Metrics, h.MetricsAt }).ToDictionaryAsync(h => h.Id, ct);
        var channels = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id) && h.LastChannel != null)
            .Select(h => new { h.Id, h.LastChannel }).ToDictionaryAsync(h => h.Id, h => h.LastChannel, ct);
        var latestAgent = AgentDownloads.AgentVersion;
        var blockedAgents = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id) && h.AgentUpdateBlockedVersion == latestAgent)
            .Select(h => h.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;

        return summaries.Select(s =>
        {
            // Weg des Agents: direkt (intern) oder über den Reverse Proxy
            s = s with { Channel = channels.GetValueOrDefault(s.Id) };
            if (s.AgentVersion is { } v && !v.EndsWith("-lite", StringComparison.Ordinal) && AgentDownloads.IsNewer(latestAgent, v))
            {
                s = s with { AgentUpdate = blockedAgents.Contains(s.Id) ? AgentUpdateStatus.Failed : "outdated" };
            }
            if (metrics.TryGetValue(s.Id, out var m) && m.Metrics is not null
                && System.Text.Json.JsonSerializer.Deserialize(m.Metrics, PullJsonContext.Default.HostMetrics) is { } latest)
            {
                s = s with { Usage = new UsageDto(m.MetricsAt!.Value, latest.CpuPercent, MetricsHistory.Percent(latest.MemoryUsed, latest.MemoryTotal), MetricsHistory.FullestDisk(latest)) };
            }

            if (!membership.TryGetValue(s.Id, out var member) || !plans.TryGetValue(member.PlanId, out var plan))
            {
                return s;
            }

            var group = plan.Waves.FirstOrDefault(w => w.Id == member.GroupId);
            var enabled = plan.ScheduleEnabled && plan.ScheduleDays != MaintenanceDays.None;
            var next = enabled ? MaintenanceWindow.Next(plan.ScheduleDays, plan.ScheduleStart, plan.ScheduleDurationMinutes, now, clock.Zone) : null;
            var open = enabled && MaintenanceWindow.Current(plan.ScheduleDays, plan.ScheduleStart, plan.ScheduleDurationMinutes, now, clock.Zone) is not null;
            return s with
            {
                AutoPatch = new AutoPatchDto(enabled, group?.Scope ?? PatchScope.Security, group?.AllowReboot ?? false, (int)plan.ScheduleDays, plan.ScheduleStart,
                    plan.ScheduleDurationMinutes, next?.StartUtc, next?.EndUtc, plan.LastWindowStart, plan.LastWindowNote, open),
                ScheduledRunbook = plan.Name,
                PlanId = plan.Id,
                PlanGroup = member.GroupName,
                PlanVia = member.Via,
            };
        }).ToList();
    }

    private static async Task<IResult> GetOverview(AppDbContext db, AIOPatcherOptions options, ServerClock clock, CancellationToken ct)
    {
        var hosts = await EnrichAsync(db, await ProjectSummaries(db.Hosts.Where(h => !h.IsRevoked), db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct), clock, ct);
        return Results.Ok(new OverviewDto(
            Sites: await db.Sites.CountAsync(ct),
            Hosts: hosts.Count,
            Online: hosts.Count(h => h.Status == HostStatus.Online),
            Offline: hosts.Count(h => h.Status == HostStatus.Offline),
            PendingUpdates: hosts.Sum(h => h.PendingUpdates),
            SecurityUpdates: hosts.Sum(h => h.SecurityUpdates),
            HostsWithSecurityUpdates: hosts.Count(h => h.SecurityUpdates > 0),
            RebootRequired: hosts.Count(h => h.RebootRequired),
            NativeAutoUpdateConflicts: hosts.Count(h => h.NativeAutoUpdatesEnabled),
            AutoPatchHosts: hosts.Count(h => h.AutoPatch.Enabled),
            RunningJobs: await db.Jobs.CountAsync(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued, ct),
            ActiveFreeze: await db.PatchFreezes
                .Where(f => f.StartsAt <= DateTime.UtcNow && f.EndsAt > DateTime.UtcNow)
                .OrderBy(f => f.SiteId != null).ThenByDescending(f => f.EndsAt)
                .Select(f => new FreezeInfoDto(f.Id, f.Name, f.SiteId, f.EndsAt))
                .FirstOrDefaultAsync(ct),
            KevHosts: hosts.Count(h => h.KevUpdates > 0),
            KevUpdates: hosts.Sum(h => h.KevUpdates),
            ContainerUpdates: hosts.Sum(h => h.ContainerUpdates),
            HostsWithoutPlan: hosts.Count(h => h.PlanId is null)));
    }

    private static async Task<IResult> GetHosts(Guid? siteId, AppDbContext db, AIOPatcherOptions options, ServerClock clock, CancellationToken ct)
    {
        var query = db.Hosts.AsQueryable();
        if (siteId is not null)
        {
            query = query.Where(h => h.SiteId == siteId);
        }

        var hosts = await EnrichAsync(db, await ProjectSummaries(query, db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct), clock, ct);
        return Results.Ok(hosts.OrderBy(h => h.SiteName).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetHost(Guid id, AppDbContext db, AIOPatcherOptions options, ServerClock clock, FeatureFlags features, AgentDownloads downloads,
        ScanSchedule schedule, CancellationToken ct)
    {
        var summary = await ProjectSummaries(db.Hosts.Where(h => h.Id == id), db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).SingleOrDefaultAsync(ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        summary = (await EnrichAsync(db, [summary], clock, ct))[0];

        var host = await db.Hosts.AsNoTracking().Include(h => h.Integration).SingleAsync(h => h.Id == id, ct);
        var updates = await db.PendingUpdates.Where(u => u.HostId == id)
            .OrderByDescending(u => u.IsSecurity).ThenBy(u => u.Name)
            .Select(u => new UpdateDto(u.Name, u.CurrentVersion, u.AvailableVersion, u.IsSecurity, u.Classification, u.FirstSeenAt, false, u.Cves, u.IsKev, u.MaxEpss))
            .ToListAsync(ct);
        var holds = await PatchRules.HoldsForHostAsync(db, host, DateTime.UtcNow, ct);
        if (holds.Count > 0)
        {
            updates = updates.Select(u => u with { IsHeld = HoldPatterns.IsHeld(holds, u.Name, PatchRules.KbOf(u.Name)) }).ToList();
        }
        var addresses = await db.IpAddresses.Where(a => a.HostId == id)
            .OrderBy(a => a.InterfaceName).ThenBy(a => a.Address)
            .Select(a => new AddressDto(a.Address, a.PrefixLength, a.InterfaceName, a.Subnet != null ? a.Subnet.Name : null))
            .ToListAsync(ct);

        var latest = AgentDownloads.AgentVersion;
        var lastScanAt = await db.AgentTasks.AsNoTracking()
            .Where(t => t.HostId == id && t.Module == AgentModules.Updates && t.Action == ModuleActions.Scan && t.Status == JobStatus.Succeeded)
            .MaxAsync(t => (DateTime?)t.FinishedAt, ct);
        var lastUpdateTask = await db.AgentTasks.AsNoTracking()
            .Where(t => t.HostId == id && t.Module == AgentModules.Agent && t.Action == ModuleActions.Update)
            .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        var status = AgentUpdateStatus.Evaluate(host, downloads, features, lastUpdateTask, DateTime.UtcNow, schedule.AgentUpdateInterval);
        var agentUpdate = new AgentUpdateDto(latest, host.AgentVersion is not null && !host.AgentVersion.EndsWith("-lite", StringComparison.Ordinal) && AgentDownloads.IsNewer(latest, host.AgentVersion),
            string.Equals(host.AgentUpdateBlockedVersion, latest, StringComparison.Ordinal),
            host.AgentUpdateOutcome, host.AgentUpdateFromVersion, host.AgentUpdateToVersion, host.AgentUpdateMessage, host.AgentUpdateAt,
            status.State, status.Reason, status.NextOfferAt);

        // Uptime ab Systemstart weiterzählen statt den Wert der letzten Meldung zu zeigen
        var uptime = host.BootedAt is { } booted ? DateTime.UtcNow - booted : host.Uptime;
        return Results.Ok(new HostDetailDto(summary, host.KernelVersion, uptime, host.EnrolledAt, host.CertificateExpiresAt, updates, addresses,
            host.IntegrationId, host.Integration?.Name, host.ExternalId, await ContainersAsync(db, id, ct, features), agentUpdate,
            host.ProtocolVersion, ModulesOf(host), BrokenSourcesOf(host), features.IsOn(Feature.AptSourceFixes),
            features.IsOn(Feature.RebootCheck) ? RebootCheck.Result(host) : null,
            host.UpdateScanHours, host.ImageCheckHours, lastScanAt));
    }

    private static List<BrokenAptSource>? BrokenSourcesOf(ManagedHost host)
    {
        try
        {
            return host.BrokenAptSources is null ? null : JsonSerializer.Deserialize(host.BrokenAptSources, PullJsonContext.Default.ListBrokenAptSource);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<HostModuleDto> ModulesOf(ManagedHost host)
    {
        var disabled = AIOPatcher.Server.Services.Modules.ModuleJson.DisabledInDashboard(host);
        var reported = AIOPatcher.Server.Services.Modules.ModuleJson.Reported(host).ToDictionary(m => m.Id);
        return AgentModules.All.Select(id => reported.TryGetValue(id, out var m)
                ? new HostModuleDto(id, m.Available, m.DisabledLocally, disabled.Contains(id), AgentModules.Required.Contains(id), m.Actions)
                : new HostModuleDto(id, false, false, disabled.Contains(id), AgentModules.Required.Contains(id), []))
            .Where(m => reported.Count == 0 || reported.ContainsKey(m.Id) || m.DisabledInDashboard)
            .ToList();
    }

    /// <summary>Modul für diesen Host an/aus. Aus = der Server vergibt keine Aufträge dafür (Daten und Jobs).</summary>
    private static async Task<IResult> ToggleModule(Guid id, string module, ModuleToggleRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (!AgentModules.All.Contains(module) || AgentModules.Required.Contains(module))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["module"] = [L.T("Dieses Modul lässt sich nicht abschalten.", "This module cannot be turned off.")] });
        }

        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var disabled = AIOPatcher.Server.Services.Modules.ModuleJson.DisabledInDashboard(host);
        if (request.Enabled ? disabled.Remove(module) : disabled.Add(module))
        {
            host.DisabledModules = disabled.Count == 0 ? null : string.Join(',', disabled.Order());
            audit.Add(request.Enabled ? "host.module.enabled" : "host.module.disabled", $"{host.Name}: {module}", siteId: host.SiteId, hostId: host.Id);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    /// <summary>Gescheiterte Agent-Version wieder anbieten (z. B. nachdem die Ursache behoben ist).</summary>
    /// <summary>
    /// "Jetzt aktualisieren": Sperre nach einem gescheiterten Versuch aufheben und das Update beim nächsten Abruf anbieten,
    /// statt bis zu 6 Stunden zu warten. Geht nicht, wenn gar kein Update möglich ist (Grund in der Antwort).
    /// </summary>
    private static async Task<IResult> UpdateAgentNow(Guid id, AppDbContext db, AuditLog audit, AgentDownloads downloads, FeatureFlags features, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        var blocked = host.AgentUpdateBlockedVersion;
        host.AgentUpdateBlockedVersion = null; // zuerst lösen, damit die Prüfung unten den echten Grund liefert
        var status = AgentUpdateStatus.Evaluate(host, downloads, features, null, now);
        if (status.State == AgentUpdateStatus.Current)
        {
            host.AgentUpdateBlockedVersion = blocked;
            return Results.Problem(L.T("Der Agent ist schon aktuell.", "The agent is already up to date."), statusCode: StatusCodes.Status409Conflict);
        }

        if (status.State == AgentUpdateStatus.Unavailable)
        {
            host.AgentUpdateBlockedVersion = blocked;
            return Results.Problem(status.Reason switch
            {
                "lite" => L.T("Der Lite-Agent aktualisiert sich nicht selbst: Installationsbefehl auf dem Host erneut ausführen.",
                    "The lite agent does not update itself: run the install command on the host again."),
                "featureOff" => FeatureFlags.DisabledMessage(Feature.AgentSelfUpdate),
                "noBuild" => L.T("Für dieses System bzw. diese Architektur liegt kein Agent-Build auf dem Server.",
                    "The server has no agent build for this system or architecture."),
                "unsigned" => L.T("Der Server kann Agent-Updates nicht signieren (kein Update-Schlüssel); Agent per Installationsbefehl aktualisieren.",
                    "The server cannot sign agent updates (no update key); update the agent with the install command."),
                "agentNoUpdate" => L.T("Dieser Agent ist zu alt für Self-Updates: Installationsbefehl auf dem Host erneut ausführen.",
                    "This agent is too old for self-updates: run the install command on the host again."),
                _ => L.T("Der Agent hat sich noch nicht gemeldet.", "The agent has not reported yet."),
            }, statusCode: StatusCodes.Status409Conflict);
        }

        host.AgentUpdateRequestedAt = now;
        audit.Add("agent.update.requested", $"{host.Name}: {host.AgentVersion} → {AgentDownloads.AgentVersion}{(blocked is null ? "" : $" (unblocked {blocked})")}",
            siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <param name="Requested">Hosts, denen das Update beim nächsten Abruf angeboten wird.</param>
    /// <param name="Unavailable">Veraltete Agents, bei denen es so nicht geht, je Grund (lite, featureOff, noBuild, unsigned, agentNoUpdate, unknown).</param>
    public sealed record BulkAgentUpdateDto(int Requested, IReadOnlyDictionary<string, int> Unavailable);

    /// <summary>"Agents aktualisieren" in der Hostliste: allen veralteten Agents das Update beim nächsten Abruf anbieten.</summary>
    private static async Task<IResult> UpdateAllAgentsNow(AppDbContext db, AuditLog audit, AgentDownloads downloads, FeatureFlags features, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var requested = new List<string>();
        var unavailable = new Dictionary<string, int>();
        foreach (var host in await db.Hosts.Where(h => !h.IsRevoked && h.AgentVersion != null).ToListAsync(ct))
        {
            var blocked = host.AgentUpdateBlockedVersion;
            host.AgentUpdateBlockedVersion = null;
            var status = AgentUpdateStatus.Evaluate(host, downloads, features, null, now);
            if (status.State is AgentUpdateStatus.Current or AgentUpdateStatus.Unavailable)
            {
                host.AgentUpdateBlockedVersion = blocked;
                if (status.Reason is { } reason)
                {
                    unavailable[reason] = unavailable.GetValueOrDefault(reason) + 1;
                }

                continue;
            }

            host.AgentUpdateRequestedAt = now;
            requested.Add(host.Name);
        }

        if (requested.Count > 0)
        {
            audit.Add("agent.update.requested",
                $"{requested.Count} hosts -> {AgentDownloads.AgentVersion}: {string.Join(", ", requested.Take(20))}{(requested.Count > 20 ? " ..." : "")}");
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new BulkAgentUpdateDto(requested.Count, unavailable));
    }

    private static async Task<IResult> RetryAgentUpdate(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        if (host.AgentUpdateBlockedVersion is { } blocked)
        {
            host.AgentUpdateBlockedVersion = null;
            audit.Add("agent.update.retry", $"{host.Name}: {blocked}", siteId: host.SiteId, hostId: host.Id);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Einen Compose-Container aktualisieren: Job, der auf dem Host das Image zieht und nur diesen Dienst neu erstellt.
    /// Wie Programm-Updates: Schalter "Programm-Updates" und lokal allowAppUpdates + allowPatch All.
    /// </summary>
    /// <summary>
    /// "Jetzt aktualisieren": Job an den Agent auf dem Server-Host; der startet aiopatcher-update (Sicherung, Neustart,
    /// Rückfall). Der Agent prüft zusätzlich policy.json allowServerUpdate.
    /// </summary>
    private static async Task<IResult> StartServerUpdate(ServerUpdateRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([request.HostId], ct);
        if (host is null || host.IsRevoked)
        {
            return Results.NotFound();
        }

        if (!ModuleJson.Usable(host, AgentModules.ServerUpdate) || !ModuleJson.Supports(host, AgentModules.ServerUpdate, ModuleActions.Update))
        {
            return Results.Problem(L.T("Auf diesem Host meldet der Agent kein aiopatcher-update (Setup) oder ist zu alt.",
                "The agent on this host does not report aiopatcher-update (setup) or is too old."), statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == host.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var job = new PatchJob
        {
            HostId = host.Id,
            Type = JobType.ServerUpdate,
            Scope = PatchScope.All,
            RequestedBy = $"admin:{user.Identity?.Name}",
            Summary = L.T($"AIOPatcher-Server aktualisieren (von {AgentDownloads.AgentVersion})", $"Update AIOPatcher server (from {AgentDownloads.AgentVersion})"),
        };
        db.Jobs.Add(job);
        audit.Add("server.update.requested", $"via {host.Name}, from {AgentDownloads.AgentVersion}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { jobId = job.Id });
    }

    private static async Task<IResult> UpdateContainer(Guid id, string name, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.AppUpdates))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.AppUpdates), statusCode: StatusCodes.Status403Forbidden);
        }

        var host = await db.Hosts.FindAsync([id], ct);
        var container = host is null ? null : await db.Containers.FirstOrDefaultAsync(c => c.HostId == id && c.Name == name, ct);
        if (host is null || container is null)
        {
            return Results.NotFound();
        }

        if (!ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.AppUpdate))
        {
            return Results.Problem(L.T("Der Agent auf diesem Host ist zu alt für Container-Updates; er aktualisiert sich gleich selbst.",
                "The agent on this host is too old for container updates; it will update itself shortly."), statusCode: StatusCodes.Status409Conflict);
        }

        // App mit eigenem Update-Skript (z. B. mailcow): das Rezept hat Vorrang vor pull/up des einzelnen Containers
        if (container.UpdateRecipe is { } recipeId)
        {
            return await UpdateViaRecipe(host, container, recipeId, user, db, audit, ct);
        }

        if (container.UpdateBlocker is { } blocker)
        {
            return Results.Problem(blocker switch
            {
                "portainer" => L.T("Der Container gehört zu einem Portainer-Stack. Bitte in Portainer „Pull and redeploy“ verwenden.",
                    "The container belongs to a Portainer stack. Please use \"Pull and redeploy\" in Portainer."),
                "noFile" => L.T("Die Compose-Datei des Containers liegt nicht (mehr) auf dem Host.", "The container's compose file is no longer on the host."),
                _ => L.T("Der Container wurde nicht per Docker Compose gestartet; bitte mit dem Werkzeug aktualisieren, das ihn angelegt hat.",
                    "The container was not started with Docker Compose; please update it with the tool that created it."),
            }, statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var job = new PatchJob
        {
            HostId = id,
            Type = JobType.ContainerUpdate,
            Scope = PatchScope.All,
            RequestedBy = $"admin:{user.Identity?.Name}",
            Packages = container.Name,
        };
        db.Jobs.Add(job);
        audit.Add("job.created", $"ContainerUpdate {container.Name} ({container.Image}) for {host.Name}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { job.Id });
    }

    /// <summary>
    /// Update-Rezept für das Compose-Projekt des Containers als Job (docker.recipe). Der Job nennt nur Rezept-Id und
    /// Containername; Ordner und Befehl bestimmt der Agent selbst aus seinem eingebauten Rezept.
    /// </summary>
    private static async Task<IResult> UpdateViaRecipe(ManagedHost host, ContainerInfo container, string recipeId, ClaimsPrincipal user, AppDbContext db,
        AuditLog audit, CancellationToken ct)
    {
        var recipe = UpdateRecipeCatalog.Find(recipeId);
        var label = recipe?.Name ?? recipeId;
        if (!ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.Recipe))
        {
            return Results.Problem(L.T("Der Agent auf diesem Host ist zu alt für Update-Rezepte; er aktualisiert sich gleich selbst.",
                "The agent on this host is too old for update recipes; it will update itself shortly."), statusCode: StatusCodes.Status409Conflict);
        }

        if (!container.UpdateRecipeAllowed)
        {
            return Results.Problem(L.T($"Auf dem Host nicht freigegeben. Dort einmal ausführen: sudo aiopatcher-agent policy set {RecipePolicyKey} true",
                $"Not allowed on the host. Run there once: sudo aiopatcher-agent policy set {RecipePolicyKey} true"), statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == host.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var job = new PatchJob
        {
            HostId = host.Id,
            Type = JobType.ContainerUpdate,
            Scope = PatchScope.All,
            RequestedBy = $"admin:{user.Identity?.Name}",
            Packages = container.Name,
            Payload = JsonSerializer.Serialize(new RecipeUpdateRequest(recipeId, container.Name), PullJsonContext.Default.RecipeUpdateRequest),
            Summary = L.T($"Update über {label} ({container.ComposeProject})", $"Update via {label} ({container.ComposeProject})"),
        };
        db.Jobs.Add(job);
        audit.Add("job.created", $"ContainerUpdate via recipe {recipeId}, project {container.ComposeProject} ({container.Name}) for {host.Name}",
            siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { job.Id });
    }

    public const string RecipePolicyKey = "allowUpdateRecipes";

    /// <summary>Rezept-Auftrag eines Container-Jobs (Payload); null = generisches pull/up.</summary>
    public static RecipeUpdateRequest? RecipeOf(PatchJob job)
    {
        if (job.Type != JobType.ContainerUpdate || string.IsNullOrEmpty(job.Payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(job.Payload, PullJsonContext.Default.RecipeUpdateRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Container neu prüfen: Images sofort bei der Registry abfragen, Agent meldet seine Container beim nächsten Abruf.</summary>
    private static async Task<IResult> RecheckContainers(Guid id, AppDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        host.ContainersRequestedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (services.GetServices<IHostedService>().OfType<ContainerUpdateService>().FirstOrDefault() is { } checker)
        {
            await checker.RecheckHostAsync(db, id, DateTime.UtcNow, ct);
        }

        return Results.NoContent();
    }

    /// <summary>Hinweis "nach dem Neustart fehlt …" ausblenden (z. B. Dienst absichtlich entfernt). Der nächste Neustart prüft neu.</summary>
    private static async Task<IResult> DismissRebootCheck(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        if (RebootCheck.Result(host) is { } result)
        {
            host.RebootCheck = null;
            audit.Add("host.rebootcheck.dismissed", $"{host.Name}: {string.Join(", ", result.Missing.Select(m => m.Name).Take(20))}", siteId: host.SiteId, hostId: host.Id);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<List<ContainerDto>> ContainersAsync(AppDbContext db, Guid hostId, CancellationToken ct, FeatureFlags? features = null)
    {
        var containers = await db.Containers.Where(c => c.HostId == hostId).OrderBy(c => c.Name).ToListAsync(ct);
        // Ältere Agents melden keine Compose-Herkunft und können nicht aktualisieren
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hostId, ct);
        var agentCan = host is not null && ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.AppUpdate);
        var agentReadsPackages = host is not null && ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.Packages);
        var references = containers.Select(c => c.Reference).OfType<string>().Distinct().ToList();
        var checks = await db.ImageChecks.Where(i => references.Contains(i.Reference)).ToDictionaryAsync(i => i.Reference, ct);

        // Lücken je Image (relevante: KEV oder EPSS ab 5 %)
        var imageIds = containers.Select(c => c.ImageId).OfType<string>().Distinct().ToList();
        var sets = await db.ImagePackageSets.AsNoTracking().Where(s => imageIds.Contains(s.ImageId))
            .Select(s => new { s.ImageId, s.OsId, s.OsVersion, s.Ecosystem, s.PackageCount, s.ScannedAt, s.Error })
            .ToDictionaryAsync(s => s.ImageId, ct);
        var vulns = (await db.ImageVulnerabilities.AsNoTracking().Where(v => imageIds.Contains(v.ImageId))
                .GroupJoin(db.CveInfos, v => v.CveId, c => c.Id, (v, c) => new { v.ImageId, v.CveId, Info = c })
                .SelectMany(x => x.Info.DefaultIfEmpty(), (x, c) => new { x.ImageId, x.CveId, IsKev = c != null && c.IsKev, Epss = c == null ? null : c.Epss })
                .ToListAsync(ct))
            .GroupBy(v => v.ImageId)
            .ToDictionary(g => g.Key, g => g.DistinctBy(v => v.CveId).ToList());
        var cveOn = features?.IsOn(Feature.CveLookup) ?? true;

        ContainerVulnsDto Vulns(ContainerInfo c)
        {
            if (!cveOn)
            {
                return new ContainerVulnsDto("off", 0, 0, null, null, 0, null, null);
            }

            if (c.ImageId is null || !sets.TryGetValue(c.ImageId, out var set))
            {
                return new ContainerVulnsDto(agentReadsPackages ? "pending" : "agent", 0, 0, null, null, 0, null, null);
            }

            var os = set.OsId is null ? null : $"{set.OsId} {set.OsVersion}".Trim();
            if (set.Ecosystem is null || set.PackageCount == 0)
            {
                return new ContainerVulnsDto("unsupported", 0, 0, null, os, set.PackageCount, null,
                    set.Error ?? L.T($"Für {os ?? "dieses Image"} gibt es keinen CVE-Abgleich (unterstützt: Debian, Ubuntu, Alpine).",
                        $"No CVE matching for {os ?? "this image"} (supported: Debian, Ubuntu, Alpine)."));
            }

            if (set.ScannedAt is null)
            {
                return new ContainerVulnsDto("pending", 0, 0, null, os, set.PackageCount, null, null);
            }

            var list = vulns.GetValueOrDefault(c.ImageId) ?? [];
            return new ContainerVulnsDto("ok", list.Count, list.Count(v => v.IsKev), list.Max(v => v.Epss), os, set.PackageCount, set.ScannedAt, null);
        }

        ContainerRecipeDto? Recipe(ContainerInfo c)
        {
            if (c.UpdateRecipe is not { } id)
            {
                return null;
            }

            // Unbekannt (Agent neuer als Server): Id statt Name anzeigen
            var recipe = UpdateRecipeCatalog.Find(id);
            return new ContainerRecipeDto(id, recipe?.Name ?? id, recipe?.App ?? id, recipe?.Docs, recipe?.NoteFor(L.Language), c.UpdateRecipeAllowed);
        }

        return containers.Select(c =>
        {
            var check = c.Reference is null ? null : checks.GetValueOrDefault(c.Reference);
            var update = check?.RemoteDigest is { } remote && c.LocalDigest is not null && !c.HasDigest(remote);
            return new ContainerDto(c.Name, c.Image, c.State, update, check?.CheckedAt, check?.Error, c.Reference is null && !c.LocalImage, c.SeenAt,
                c.ComposeProject is null ? null : $"{c.ComposeProject}/{c.ComposeService}", agentCan ? c.UpdateBlocker : "agent",
                c.ExitCode, c.StartedAt, c.FinishedAt, c.Health, c.RestartPolicy, c.LocalImage, c.BaseImage, Vulns(c), Recipe(c));
        }).ToList();
    }

    /// <summary>Relevante Lücken im Image eines Containers (KEV oder EPSS ab 5 %), schlimmste zuerst.</summary>
    private static async Task<IResult> ContainerVulnerabilities(Guid id, string name, AppDbContext db, CveService cves, CancellationToken ct)
    {
        var container = await db.Containers.AsNoTracking().FirstOrDefaultAsync(c => c.HostId == id && c.Name == name, ct);
        if (container is null)
        {
            return Results.NotFound();
        }

        var set = container.ImageId is null ? null : await db.ImagePackageSets.AsNoTracking().FirstOrDefaultAsync(s => s.ImageId == container.ImageId, ct);
        var rows = container.ImageId is null ? [] : await db.ImageVulnerabilities.AsNoTracking().Where(v => v.ImageId == container.ImageId).ToListAsync(ct);
        var ids = rows.Select(r => r.CveId).Distinct().ToList();
        var infos = await db.CveInfos.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var items = rows
            .Select(r =>
            {
                var info = infos.GetValueOrDefault(r.CveId);
                var epss = info?.Epss is { } e ? (e, info.EpssPercentile) : cves.EpssOf(r.CveId) is { } x ? ((double)x.Epss, (double?)x.Percentile) : ((double?)null, (double?)null);
                return new ContainerVulnDto(r.CveId, r.Package, r.InstalledVersion, info?.IsKev == true, info?.KevName, epss.Item1, epss.Item2);
            })
            .OrderByDescending(v => v.IsKev).ThenByDescending(v => v.Epss ?? 0).ThenBy(v => v.Cve)
            .ToList();
        return Results.Ok(new ContainerVulnListDto(container.Name, container.Image, set?.OsId is null ? null : $"{set.OsId} {set.OsVersion}".Trim(),
            set?.PackageCount ?? 0, set?.ScannedAt, items));
    }

    /// <summary>
    /// Hosts einem Plan zuordnen: aus allen Plänen nehmen (nur ausgewählte Mitgliedschaft) und, wenn PlanId gesetzt, in die
    /// gewünschte bzw. erste Gruppe dieses Plans legen. Ein Host ist so immer nur in einem Plan ausgewählt.
    /// </summary>
    private static async Task<IResult> AssignPlanAsync(Guid[] hostIds, Guid? planId, Guid? groupId, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (hostIds.Length is 0 or > JobPlanner.MaxHostsPerRequest)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostIds"] = [L.T($"1 bis {JobPlanner.MaxHostsPerRequest} Hosts auswählen.", $"Select 1 to {JobPlanner.MaxHostsPerRequest} hosts.")] });
        }

        RunbookWave? group = null;
        if (planId is not null)
        {
            var plan = await db.Runbooks.Include(r => r.Waves).SingleOrDefaultAsync(r => r.Id == planId, ct);
            group = plan?.Waves.OrderBy(w => w.Order).FirstOrDefault(w => groupId is null || w.Id == groupId);
            if (plan is null || group is null)
            {
                return Results.NotFound();
            }
        }

        var hosts = await db.Hosts.Where(h => hostIds.Contains(h.Id)).ToListAsync(ct);
        if (hosts.Count == 0)
        {
            return Results.NotFound();
        }

        var ids = hosts.Select(h => h.Id).ToList();
        db.RunbookWaveHosts.RemoveRange(await db.RunbookWaveHosts.Where(m => ids.Contains(m.HostId)).ToListAsync(ct));
        if (group is not null)
        {
            db.RunbookWaveHosts.AddRange(ids.Select(id => new RunbookWaveHost { WaveId = group.Id, HostId = id }));
        }

        foreach (var host in hosts)
        {
            audit.Add("patch-plan.assigned", group is null ? $"{host.Name}: removed from patch plans" : $"{host.Name}: plan group {group.Name}", siteId: host.SiteId, hostId: host.Id);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeHost(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        host.IsRevoked = true;
        audit.Add("host.revoked", host.Name, siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteHost(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        // Mit dem Datensatz verschwindet auch die Zertifikats-Identität: der Agent kommt nicht mehr rein.
        db.Hosts.Remove(host);
        audit.Add("host.deleted", host.Name, siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetEnrollmentTokens(Guid? siteId, AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.EnrollmentTokens.Where(t => t.UsedAt == null && t.ExpiresAt > now);
        if (siteId is not null)
        {
            query = query.Where(t => t.SiteId == siteId);
        }

        return Results.Ok(await query.OrderByDescending(t => t.CreatedAt)
            .Select(t => new EnrollmentTokenDto(t.Id, t.SiteId, t.Label, t.CreatedAt, t.ExpiresAt))
            .ToListAsync(ct));
    }

    private static async Task<IResult> CreateEnrollmentToken(
        CreateEnrollmentTokenRequest request,
        AppDbContext db,
        AIOPatcherOptions options,
        CertificateAuthority ca,
        AgentDownloads downloads,
        AuditLog audit,
        PublicAccess publicAccess,
        CancellationToken ct)
    {
        var site = await db.Sites.FindAsync([request.SiteId], ct);
        if (site is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")] });
        }

        // Kurz genug zum Abtippen ist nicht nötig (Copy & Paste), lang genug gegen Raten: 160 Bit.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        var entity = new EnrollmentToken
        {
            SiteId = site.Id,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim()[..Math.Min(request.Label.Trim().Length, 100)],
            TokenHash = AgentEndpoints.HashEnrollmentToken(token),
            ExpiresAt = DateTime.UtcNow.AddMinutes(options.EnrollmentTokenLifetimeMinutes),
        };
        db.EnrollmentTokens.Add(entity);
        audit.Add("enrollment-token.created", entity.Label, siteId: site.Id);
        await db.SaveChangesAsync(ct);

        var windowsScript = AgentDownloads.FindScript("install.ps1");
        var linuxScript = AgentDownloads.FindScript("install.sh");
        // Für Hosts außerhalb: Über den Reverse Proxy prüft der Host Skript und Agent gegen diese Werte (nicht gegen TLS)
        var agentHashes = string.Join(',', AgentDownloads.RuntimeIds
            .Select(rid => downloads.Find(rid) is { } file ? $"{rid}={downloads.Sha256(file).ToUpperInvariant()}" : null)
            .OfType<string>());
        return Results.Ok(new EnrollmentTokenCreatedDto(
            entity.Id, token, entity.ExpiresAt, options.AgentApiUrl, ca.RootFingerprintSha256,
            windowsScript is null ? null : downloads.Sha256(windowsScript).ToUpperInvariant(),
            publicAccess.AgentUrl,
            linuxScript is null ? null : downloads.Sha256(linuxScript).ToUpperInvariant(),
            agentHashes.Length == 0 ? null : agentHashes));
    }

    private static async Task<IResult> DeleteEnrollmentToken(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var token = await db.EnrollmentTokens.FindAsync([id], ct);
        if (token is null)
        {
            return Results.NotFound();
        }

        db.EnrollmentTokens.Remove(token);
        audit.Add("enrollment-token.deleted", token.Label, siteId: token.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
