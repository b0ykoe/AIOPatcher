using System.Text.Json;
using System.Security.Cryptography;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
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
        bool Mapped);

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


    public sealed record ServerInfoDto(string Version, string TimeZone, DateTime NowUtc, string? LatestVersion, string? ReleaseUrl, bool UpdateAvailable);

    public sealed record UpdateDto(string Name, string? CurrentVersion, string? AvailableVersion, bool IsSecurity, string? Classification, DateTime FirstSeenAt, bool IsHeld,
        string? Cves, bool IsKev, double? MaxEpss);

    public sealed record ContainerDto(string Name, string Image, string? State, bool UpdateAvailable, DateTime? CheckedAt, string? CheckError, bool Pinned);
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
        RebootCheckResult? RebootCheck = null);

    /// <summary>Modul des Agents: vorhanden, lokal (policy.json) oder im Dashboard abgeschaltet. Required = nicht abschaltbar.</summary>
    public sealed record HostModuleDto(string Id, bool Available, bool DisabledLocally, bool DisabledInDashboard, bool Required, IReadOnlyList<string> Actions);

    public sealed record ModuleToggleRequest(bool Enabled);

    /// <summary>Available: Server hat eine neuere Agent-Version. Blocked: diese ist hier gescheitert und wird nicht erneut angeboten.</summary>
    public sealed record AgentUpdateDto(
        string LatestVersion,
        bool Available,
        bool Blocked,
        AgentUpdateOutcome? LastOutcome,
        string? FromVersion,
        string? ToVersion,
        string? Message,
        DateTime? At);

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
        int ContainerUpdates);

    public sealed record FreezeInfoDto(Guid Id, string Name, Guid? SiteId, DateTime EndsAt);

    public sealed record CreateEnrollmentTokenRequest(Guid SiteId, string? Label);

    public sealed record EnrollmentTokenCreatedDto(
        Guid Id,
        string Token,
        DateTime ExpiresAt,
        string ServerUrl,
        string CaFingerprintSha256,
        string? WindowsInstallScriptSha256);

    public sealed record EnrollmentTokenDto(Guid Id, Guid SiteId, string? Label, DateTime CreatedAt, DateTime ExpiresAt);

    public static void MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);

        api.MapGet("/overview", GetOverview);
        api.MapGet("/hosts", GetHosts);
        api.MapGet("/hosts/{id:guid}", GetHost);
        op.MapPut("/hosts/{id:guid}/auto-patch", UpdateAutoPatch);
        op.MapPut("/hosts/auto-patch", UpdateAutoPatchBulk);
        api.MapGet("/server-info", (ServerClock clock, ReleaseCheck releases) =>
            new ServerInfoDto(AgentDownloads.AgentVersion, clock.ZoneName, DateTime.UtcNow,
                releases.Latest?.Version, releases.Latest?.Url, releases.UpdateAvailable));
        op.MapPost("/hosts/{id:guid}/revoke", RevokeHost);
        op.MapPost("/hosts/{id:guid}/agent-update/retry", RetryAgentUpdate);
        op.MapDelete("/hosts/{id:guid}/reboot-check", DismissRebootCheck);
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
            new AutoPatchDto(h.AutoPatchEnabled, h.AutoPatchScope, h.AutoRebootAllowed, (int)h.MaintenanceDays, h.MaintenanceStart, h.MaintenanceDurationMinutes, null, null, h.LastAutoPatchWindowStart, h.LastAutoPatchNote, false),
            h.RunbookMemberships.Where(m => m.Wave!.Runbook!.ScheduleEnabled).Select(m => m.Wave!.Runbook!.Name).FirstOrDefault(),
            h.Tags,
            h.PendingUpdates.Count(u => u.IsKev),
            h.Containers.Count(c => c.LocalDigest != null && images.Any(i => i.Reference == c.Reference && i.RemoteDigest != null && i.RemoteDigest != c.LocalDigest)),
            h.IntegrationId != null && h.ExternalId != null));

    /// <summary>Nächstes Wartungsfenster lässt sich nicht in SQL berechnen: nach dem Laden ergänzen.</summary>
    private static HostSummaryDto WithNextWindow(HostSummaryDto summary, ServerClock clock)
    {
        var a = summary.AutoPatch;
        if (!a.Enabled)
        {
            return summary;
        }

        var now = DateTime.UtcNow;
        var days = (MaintenanceDays)a.Days;
        var next = MaintenanceWindow.Next(days, a.Start, a.DurationMinutes, now, clock.Zone);
        var open = MaintenanceWindow.Current(days, a.Start, a.DurationMinutes, now, clock.Zone) is not null;
        return summary with { AutoPatch = a with { NextWindowStart = next?.StartUtc, NextWindowEnd = next?.EndUtc, WindowOpenNow = open } };
    }

    private static async Task<IResult> GetOverview(AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var hosts = await ProjectSummaries(db.Hosts.Where(h => !h.IsRevoked), db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct);
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
            ContainerUpdates: hosts.Sum(h => h.ContainerUpdates)));
    }

    private static async Task<IResult> GetHosts(Guid? siteId, AppDbContext db, AIOPatcherOptions options, ServerClock clock, CancellationToken ct)
    {
        var query = db.Hosts.AsQueryable();
        if (siteId is not null)
        {
            query = query.Where(h => h.SiteId == siteId);
        }

        var hosts = await ProjectSummaries(query, db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct);
        return Results.Ok(hosts
            .Select(h => WithNextWindow(h, clock))
            .OrderBy(h => h.SiteName).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetHost(Guid id, AppDbContext db, AIOPatcherOptions options, ServerClock clock, FeatureFlags features, CancellationToken ct)
    {
        var summary = await ProjectSummaries(db.Hosts.Where(h => h.Id == id), db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).SingleOrDefaultAsync(ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        summary = WithNextWindow(summary, clock);

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
        var agentUpdate = new AgentUpdateDto(latest, host.AgentVersion is not null && !host.AgentVersion.EndsWith("-lite", StringComparison.Ordinal) && AgentDownloads.IsNewer(latest, host.AgentVersion),
            string.Equals(host.AgentUpdateBlockedVersion, latest, StringComparison.Ordinal),
            host.AgentUpdateOutcome, host.AgentUpdateFromVersion, host.AgentUpdateToVersion, host.AgentUpdateMessage, host.AgentUpdateAt);

        // Uptime ab Systemstart weiterzählen statt den Wert der letzten Meldung zu zeigen
        var uptime = host.BootedAt is { } booted ? DateTime.UtcNow - booted : host.Uptime;
        return Results.Ok(new HostDetailDto(summary, host.KernelVersion, uptime, host.EnrolledAt, host.CertificateExpiresAt, updates, addresses,
            host.IntegrationId, host.Integration?.Name, host.ExternalId, await ContainersAsync(db, id, ct), agentUpdate,
            host.ProtocolVersion, ModulesOf(host), BrokenSourcesOf(host), features.IsOn(Feature.AptSourceFixes),
            features.IsOn(Feature.RebootCheck) ? RebootCheck.Result(host) : null));
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

    private static async Task<List<ContainerDto>> ContainersAsync(AppDbContext db, Guid hostId, CancellationToken ct)
    {
        var containers = await db.Containers.Where(c => c.HostId == hostId).OrderBy(c => c.Name).ToListAsync(ct);
        var references = containers.Select(c => c.Reference).OfType<string>().Distinct().ToList();
        var checks = await db.ImageChecks.Where(i => references.Contains(i.Reference)).ToDictionaryAsync(i => i.Reference, ct);
        return containers.Select(c =>
        {
            var check = c.Reference is null ? null : checks.GetValueOrDefault(c.Reference);
            var update = check?.RemoteDigest is { } remote && c.LocalDigest is not null && remote != c.LocalDigest;
            return new ContainerDto(c.Name, c.Image, c.State, update, check?.CheckedAt, check?.Error, c.Reference is null);
        }).ToList();
    }

    private static async Task<IResult> UpdateAutoPatch(Guid id, AutoPatchRequest request, AppDbContext db, AIOPatcherOptions options, ServerClock clock, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var errors = AutoPatchSettings.Validate(request, out var start);
        if (AutoPatchSettings.CannotApply(host, request) is { } reason)
        {
            errors["enabled"] = [reason];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        AutoPatchSettings.Apply(host, request, start);
        audit.Add("auto-patch.updated", AutoPatchSettings.Describe(host, request, start), siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);

        var summary = await ProjectSummaries(db.Hosts.Where(h => h.Id == id), db.ImageChecks, DateTime.UtcNow - options.OfflineAfter).SingleAsync(ct);
        return Results.Ok(WithNextWindow(summary, clock));
    }

    public sealed record BulkAutoPatchRequest(Guid[] HostIds, AutoPatchRequest Settings);

    public sealed record BulkAutoPatchResult(int Updated, IReadOnlyList<JobPlanner.Skip> Skipped);

    /// <summary>Gleiche Auto-Patch-Einstellung für mehrere Hosts. Gesperrte Hosts werden übersprungen.</summary>
    private static async Task<IResult> UpdateAutoPatchBulk(BulkAutoPatchRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (request.HostIds is null || request.HostIds.Length == 0 || request.HostIds.Length > JobPlanner.MaxHostsPerRequest || request.Settings is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostIds"] = [L.T($"1 bis {JobPlanner.MaxHostsPerRequest} Hosts auswählen.", $"Select 1 to {JobPlanner.MaxHostsPerRequest} hosts.")] });
        }

        var errors = AutoPatchSettings.Validate(request.Settings, out var start);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var wanted = request.HostIds.ToHashSet();
        var hosts = (await db.Hosts.ToListAsync(ct)).Where(h => wanted.Contains(h.Id)).ToList();
        var skipped = wanted.Where(id => hosts.All(h => h.Id != id)).Select(id => new JobPlanner.Skip(id, id.ToString(), JobPlanner.HostNotFound)).ToList();
        var updated = 0;

        foreach (var host in hosts)
        {
            if (AutoPatchSettings.CannotApply(host, request.Settings) is { } reason)
            {
                skipped.Add(new JobPlanner.Skip(host.Id, host.Name, reason));
                continue;
            }

            AutoPatchSettings.Apply(host, request.Settings, start);
            audit.Add("auto-patch.updated", AutoPatchSettings.Describe(host, request.Settings, start) + " (bulk)", siteId: host.SiteId, hostId: host.Id);
            updated++;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new BulkAutoPatchResult(updated, skipped));
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
        return Results.Ok(new EnrollmentTokenCreatedDto(
            entity.Id, token, entity.ExpiresAt, options.AgentApiUrl, ca.RootFingerprintSha256,
            windowsScript is null ? null : downloads.Sha256(windowsScript).ToUpperInvariant()));
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
