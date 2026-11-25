using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Inventar → Stacks: Docker-Stacks aus Portainer lesen und per "Pull and redeploy" aktualisieren, dazu die Container der
/// Docker-Hosts mit Agent (Stacks = Compose-Projekte, aktualisiert über den Agent wie im Containers-Reiter).
/// </summary>
public static class PortainerEndpoints
{
    /// <param name="UpdatesEnabled">Schalter "Programm- und Container-Updates" (gilt auch für Stacks).</param>
    /// <param name="Hosts">Docker-Hosts mit Agent (Container aus der Inventur), gruppiert wird im Web wie im Containers-Reiter.</param>
    public sealed record StacksOverviewDto(bool UpdatesEnabled, bool ImageChecksEnabled, IReadOnlyList<PortainerServerDto> Servers,
        IReadOnlyList<DockerHostDto>? Hosts = null);

    public sealed record DockerHostDto(Guid Id, string Name, string? SiteName, IReadOnlyList<HostEndpoints.ContainerDto> Containers,
        ContainerManagementEndpoints.ManagementDto? ContainerManagement = null);

    public sealed record PortainerServerDto(Guid Id, string Name, string? SiteName, string? BaseUrl, bool Enabled, DateTime? LastCheckAt, string? LastError,
        string? Version, HostEndpoints.FreezeInfoDto? Freeze, IReadOnlyList<PortainerEnvironment> Environments, IReadOnlyList<StackDto> Stacks,
        IReadOnlyList<StackJobDto> Jobs);

    /// <param name="UpdateState">"outdated", "current" oder "unknown" (Images nicht prüfbar oder noch nicht geprüft).</param>
    /// <param name="Blocker">Warum kein Update geht: "kubernetes" oder "offline" (Umgebung nicht erreichbar); sonst null.</param>
    public sealed record StackDto(int Id, string Name, string Type, int EnvironmentId, string? EnvironmentName, bool Active, bool Git, string? GitRef,
        DateTime? UpdatedAt, string UpdateState, int OutdatedImages, IReadOnlyList<StackContainerDto> Containers, StackJobDto? LastJob, string? Blocker);

    /// <param name="ImageState">"outdated", "current", "local" (ohne Registry-Digest) oder "unknown".</param>
    public sealed record StackContainerDto(string Name, string? Service, string Image, string? State, string ImageState);

    public sealed record StackJobDto(Guid Id, int StackId, string StackName, JobStatus Status, string RequestedBy, DateTime CreatedAt, DateTime? StartedAt,
        DateTime? FinishedAt, string? Summary, string? Log);

    public static void MapPortainerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stacks", Overview).RequireAuthorization();
        var operators = app.MapGroup("/api/integrations/{id:guid}/portainer").RequireAuthorization(UserAccounts.OperatorPolicy);
        operators.MapPost("/refresh", Refresh);
        operators.MapPost("/stacks/{stackId:int}/update", UpdateStack);
    }

    private static StackJobDto Dto(StackUpdateJob j) =>
        new(j.Id, j.StackId, j.StackName, j.Status, j.RequestedBy, j.CreatedAt, j.StartedAt, j.FinishedAt, j.Summary, j.Log);

    /// <summary>Mit siteId: Portainer-Instanzen dieser Site und solche ohne Site; Docker-Hosts nur dieser Site.</summary>
    private static async Task<StacksOverviewDto> Overview(Guid? siteId, AppDbContext db, FeatureFlags features, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var integrations = await db.Integrations.AsNoTracking().Include(i => i.Site)
            .Where(i => i.Type == IntegrationType.Portainer)
            .Where(i => siteId == null || i.SiteId == null || i.SiteId == siteId)
            .OrderBy(i => i.Name).ToListAsync(ct);
        var ids = integrations.Select(i => i.Id).ToList();
        var jobs = (await db.StackUpdateJobs.AsNoTracking().Where(j => ids.Contains(j.IntegrationId)).ToListAsync(ct))
            .GroupBy(j => j.IntegrationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).Take(20).ToList());
        var freezes = await db.PatchFreezes.AsNoTracking().Where(f => f.StartsAt <= now && f.EndsAt > now).ToListAsync(ct);

        var statuses = integrations.ToDictionary(i => i.Id, i => PortainerMonitor.Parse(i.StatusJson));
        var references = statuses.Values.OfType<PortainerStatus>().SelectMany(s => s.Stacks).SelectMany(s => s.Containers)
            .Select(c => c.Reference).OfType<string>().Distinct().ToList();
        var checks = await PortainerMonitor.ChecksAsync(db, references, ct);

        var servers = integrations.Select(i =>
        {
            var status = statuses[i.Id];
            var history = jobs.GetValueOrDefault(i.Id) ?? [];
            var freeze = freezes.Where(f => f.SiteId == null || f.SiteId == i.SiteId).OrderByDescending(f => f.EndsAt).FirstOrDefault();
            return new PortainerServerDto(i.Id, i.Name, i.Site?.Name, i.BaseUrl, i.Enabled, i.LastCheckAt, i.LastError, status?.Version,
                freeze is null ? null : new HostEndpoints.FreezeInfoDto(freeze.Id, freeze.Name, freeze.SiteId, freeze.EndsAt),
                status?.Environments ?? [],
                status is null ? [] : BuildStacks(status, checks, history),
                history.Select(Dto).ToList());
        }).ToList();

        // Docker-Hosts mit gemeldeten Containern (wie die Host-Liste: nicht widerrufen, Site-Filter)
        var dockerHosts = await db.Hosts.AsNoTracking().Include(h => h.Site)
            .Where(h => !h.IsRevoked && (siteId == null || h.SiteId == siteId) && db.Containers.Any(c => c.HostId == h.Id))
            .OrderBy(h => h.Name).ToListAsync(ct);
        var hosts = new List<DockerHostDto>();
        foreach (var h in dockerHosts)
        {
            hosts.Add(new DockerHostDto(h.Id, h.Name, h.Site?.Name, await HostEndpoints.ContainersAsync(db, h.Id, ct, features), ContainerManagementEndpoints.Management(h)));
        }

        return new StacksOverviewDto(features.IsOn(Feature.AppUpdates), features.IsOn(Feature.ContainerChecks), servers, hosts);
    }

    /// <summary>Stack-Zeilen: veraltete zuerst, dann nach Name.</summary>
    public static IReadOnlyList<StackDto> BuildStacks(PortainerStatus status, IReadOnlyDictionary<string, ImageCheck> checks, IReadOnlyList<StackUpdateJob> jobs)
    {
        var environments = status.Environments.ToDictionary(e => e.Id);
        return status.Stacks.Select(s =>
            {
                var containers = s.Containers.Select(c => new StackContainerDto(c.Name, c.Service, c.Image, c.State,
                    c.Reference is null ? "local" : PortainerClient.IsOutdated(c, checks) switch { true => "outdated", false => "current", null => "unknown" })).ToList();
                var outdated = containers.Count(c => c.ImageState == "outdated");
                var state = outdated > 0 ? "outdated" : containers.Count > 0 && containers.All(c => c.ImageState is "current" or "local") && containers.Any(c => c.ImageState == "current")
                    ? "current" : "unknown";
                var env = environments.GetValueOrDefault(s.EnvironmentId);
                var blocker = s.Type == PortainerClient.KubernetesStack ? "kubernetes" : env is { Status: 2 } ? "offline" : null;
                var last = jobs.Where(j => j.StackId == s.Id).MaxBy(j => j.CreatedAt);
                return new StackDto(s.Id, s.Name, s.Type switch { PortainerClient.SwarmStack => "swarm", PortainerClient.KubernetesStack => "kubernetes", _ => "compose" },
                    s.EnvironmentId, env?.Name, s.Status != 2, s.Git, s.GitRef, s.UpdatedAt, state, outdated, containers, last is null ? null : Dto(last), blocker);
            })
            .OrderBy(s => s.UpdateState == "outdated" ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"Neu einlesen": Portainer abfragen und die Images der Stacks sofort bei der Registry prüfen.</summary>
    private static async Task<IResult> Refresh(Guid id, AppDbContext db, PortainerMonitor monitor, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is not { Type: IntegrationType.Portainer })
        {
            return Results.NotFound();
        }

        await monitor.RefreshAsync(db, integration, ct, forceImageCheck: true);
        return integration.LastError is { } error ? Results.Problem(error, statusCode: StatusCodes.Status502BadGateway) : Results.NoContent();
    }

    /// <summary>Stack mit neuen Images neu ausrollen (als Job). Gesperrt bei Patchpause, abgeschaltetem Schalter oder laufendem Job.</summary>
    private static async Task<IResult> UpdateStack(Guid id, int stackId, ClaimsPrincipal user, AppDbContext db, FeatureFlags features, AuditLog audit,
        PortainerStackUpdates updates, CancellationToken ct)
    {
        if (!features.IsOn(Feature.AppUpdates))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.AppUpdates), statusCode: StatusCodes.Status403Forbidden);
        }

        var integration = await db.Integrations.FindAsync([id], ct);
        var stack = integration is { Type: IntegrationType.Portainer } ? PortainerMonitor.Parse(integration.StatusJson)?.Stacks.FirstOrDefault(s => s.Id == stackId) : null;
        if (integration is null || stack is null)
        {
            return Results.NotFound();
        }

        if (!integration.Enabled)
        {
            return Results.Problem(L.T("Die Integration ist deaktiviert.", "The integration is disabled."), statusCode: StatusCodes.Status409Conflict);
        }

        if (stack.Type == PortainerClient.KubernetesStack)
        {
            return Results.Problem(L.T("Kubernetes-Stacks werden nicht unterstützt.", "Kubernetes stacks are not supported."), statusCode: StatusCodes.Status409Conflict);
        }

        var now = DateTime.UtcNow;
        if (await PatchRules.ActiveFreezeAsync(db, integration.SiteId is { } siteId ? [siteId] : [], now, ct) is { } freeze)
        {
            return Results.Problem(PatchRules.FreezeNote(freeze), statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.StackUpdateJobs.AnyAsync(j => j.IntegrationId == id && j.StackId == stackId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Für diesen Stack läuft bereits ein Update.", "An update for this stack is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var job = new StackUpdateJob
        {
            IntegrationId = id,
            StackId = stack.Id,
            EnvironmentId = stack.EnvironmentId,
            StackName = stack.Name.Length > 200 ? stack.Name[..200] : stack.Name,
            RequestedBy = user.Identity?.Name ?? "?",
        };
        db.StackUpdateJobs.Add(job);
        audit.Add("portainer.stack-update", $"{integration.Name}: {stack.Name} (stack {stack.Id}, environment {stack.EnvironmentId})", siteId: integration.SiteId);
        await db.SaveChangesAsync(ct);
        updates.Enqueue(job.Id);
        return Results.Ok(new { job.Id });
    }
}
