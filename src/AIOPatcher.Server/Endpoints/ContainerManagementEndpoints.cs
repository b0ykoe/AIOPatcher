using System.Security.Claims;
using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Mini-Docker-Manager: Container starten/stoppen/löschen, Platz freigeben, Compose bearbeiten und Stacks anlegen, jeweils als
/// Job mit Live-Log (docker.manage). Nur Admins, nur mit Funktionsschalter <see cref="Feature.ContainerManagement"/>; der Host
/// muss es zusätzlich in seiner policy.json erlauben (das setzt nur jemand auf dem Host selbst, nie der Server).
/// </summary>
public static class ContainerManagementEndpoints
{
    /// <summary>Was der Containers-Reiter für die Verwaltung braucht.</summary>
    /// <param name="Allowed">policy.json allowContainerManagement laut Agent; null = nicht gemeldet (älterer Agent).</param>
    /// <param name="Disk">"docker system df" der letzten Inventur (für "Platz freigeben").</param>
    public sealed record ManagementDto(bool AgentSupports, bool? Allowed, bool? ComposeEdit, string? ComposeRoot, IReadOnlyList<DockerDiskUsage>? Disk);

    /// <param name="Status">Status des Lese-Jobs; File nur einmal, danach ist er auf dem Server gelöscht.</param>
    public sealed record ComposeReadDto(JobStatus Status, string? Summary, ComposeFileResult? File);

    public static void MapContainerManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/hosts/{id:guid}/containers").RequireAuthorization(UserAccounts.AdminPolicy);
        admin.MapPost("/manage", ManageAsync);
        admin.MapGet("/compose/{jobId:guid}", ComposeAsync);
    }

    public static ManagementDto Management(ManagedHost host)
    {
        var policy = PolicyReport.Parse(host.ReportedPolicy);
        List<DockerDiskUsage>? disk = null;
        if (host.DockerDisk is { } json)
        {
            try
            {
                disk = JsonSerializer.Deserialize(json, PullJsonContext.Default.ListDockerDiskUsage);
            }
            catch (JsonException)
            {
            }
        }

        return new ManagementDto(ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.Manage), policy?.AllowContainerManagement, policy?.AllowComposeEdit,
            policy?.ComposeRoot, disk);
    }

    /// <summary>Warum der Auftrag für diesen Host nicht geht (Schalter, Agent, gemeldete Policy); null = geht.</summary>
    public static string? Blocked(ManagedHost host, ContainerManageRequest request, bool featureOn)
    {
        if (!featureOn)
        {
            return FeatureFlags.DisabledMessage(Feature.ContainerManagement);
        }

        if (!ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.Manage))
        {
            return L.T("Der Agent auf diesem Host kann noch keine Container verwalten; er aktualisiert sich gleich selbst.",
                "The agent on this host cannot manage containers yet; it will update itself shortly.");
        }

        var policy = PolicyReport.Parse(host.ReportedPolicy);
        if (policy?.AllowContainerManagement == false)
        {
            return L.T("Auf dem Host nicht freigegeben. Dort einmal ausführen: sudo aiopatcher-agent policy set allowContainerManagement true",
                "Not allowed on the host. Run there once: sudo aiopatcher-agent policy set allowContainerManagement true");
        }

        if (ContainerOps.NeedsComposeEdit(request.Op) && policy?.AllowComposeEdit == false)
        {
            return L.T("Compose bearbeiten ist auf dem Host nicht freigegeben. Dort einmal ausführen: sudo aiopatcher-agent policy set allowComposeEdit true",
                "Editing compose files is not allowed on the host. Run there once: sudo aiopatcher-agent policy set allowComposeEdit true");
        }

        return null;
    }

    /// <summary>Kurztext für Job und Audit (ohne Datei-Inhalt).</summary>
    public static string Describe(ContainerManageRequest r)
    {
        var targets = r.Containers is { Count: > 0 } names ? string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" +{names.Count - 5}" : "") : r.Project ?? "";
        var volumes = r.Volumes ? L.T(" (mit Volumes)", " (with volumes)") : "";
        return r.Op switch
        {
            ContainerOps.Start => L.T($"Starten: {targets}", $"Start: {targets}"),
            ContainerOps.Stop => L.T($"Stoppen: {targets}", $"Stop: {targets}"),
            ContainerOps.Restart => L.T($"Neu starten: {targets}", $"Restart: {targets}"),
            ContainerOps.Remove => L.T($"Löschen: {targets}{volumes}", $"Delete: {targets}{volumes}"),
            ContainerOps.Down => L.T($"Stack entfernen (down): {targets}{volumes}", $"Take stack down: {targets}{volumes}"),
            ContainerOps.Prune => L.T($"Platz freigeben{volumes}", $"Reclaim space{volumes}"),
            ContainerOps.ReadCompose => L.T($"Compose-Datei lesen: {targets}", $"Read compose file: {targets}"),
            ContainerOps.WriteCompose => L.T($"Compose-Datei speichern: {targets}{(r.Up ? " + up -d" : "")}", $"Save compose file: {targets}{(r.Up ? " + up -d" : "")}"),
            _ => L.T($"Neuer Stack: {targets}{(r.Up ? " + up -d" : "")}", $"New stack: {targets}{(r.Up ? " + up -d" : "")}"),
        };
    }

    private static async Task<IResult> ManageAsync(Guid id, ContainerManageRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit,
        FeatureFlags features, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null || host.IsRevoked)
        {
            return Results.NotFound();
        }

        if (ContainerOps.Invalid(request) is { } invalid)
        {
            return Results.Problem(invalid, statusCode: StatusCodes.Status400BadRequest);
        }

        if (Blocked(host, request, features.IsOn(Feature.ContainerManagement)) is { } blocked)
        {
            return Results.Problem(blocked, statusCode: features.IsOn(Feature.ContainerManagement) ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden);
        }

        // Nur, was der Host gemeldet hat (ein neuer Stack darf es dagegen noch nicht geben)
        var known = await db.Containers.Where(c => c.HostId == id).Select(c => new { c.Name, c.ComposeProject }).ToListAsync(ct);
        if (ContainerOps.OnContainers(request.Op) && request.Containers!.FirstOrDefault(n => known.All(k => k.Name != n)) is { } unknown)
        {
            return Results.Problem(L.T($"Container {unknown} ist auf diesem Host nicht bekannt.", $"Container {unknown} is not known on this host."), statusCode: StatusCodes.Status404NotFound);
        }

        if (request.Op is ContainerOps.Down or ContainerOps.ReadCompose or ContainerOps.WriteCompose && known.All(k => k.ComposeProject != request.Project))
        {
            return Results.Problem(L.T($"Stack {request.Project} ist auf diesem Host nicht bekannt.", $"Stack {request.Project} is not known on this host."), statusCode: StatusCodes.Status404NotFound);
        }

        if (request.Op == ContainerOps.Create && known.Any(k => k.ComposeProject == request.Project))
        {
            return Results.Problem(L.T($"Einen Stack {request.Project} gibt es schon.", $"A stack {request.Project} already exists."), statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var clean = request with { Containers = request.Containers?.Distinct().ToList() };
        var job = new PatchJob
        {
            HostId = id,
            Type = JobType.ContainerManage,
            Scope = PatchScope.All,
            RequestedBy = $"admin:{user.Identity?.Name}",
            Packages = AgentEndpoints.Truncate(clean.Containers is { Count: > 0 } names ? string.Join(',', names) : clean.Project ?? "", 4000),
            Payload = JsonSerializer.Serialize(clean, PullJsonContext.Default.ContainerManageRequest),
            Summary = Describe(clean),
        };
        db.Jobs.Add(job);
        // Audit ohne Datei-Inhalt
        audit.Add("container.manage", $"{host.Name}: {clean.Op} {job.Packages}{(clean.Volumes ? " volumes" : "")}{(clean.Up ? " up" : "")}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { job.Id });
    }

    /// <summary>Ergebnis eines Lese-Jobs; den Inhalt gibt es genau einmal (danach gelöscht, er kann Passwörter enthalten).</summary>
    private static async Task<IResult> ComposeAsync(Guid id, Guid jobId, AppDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.HostId == id && j.Type == JobType.ContainerManage, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        ComposeFileResult? file = null;
        if (job.Result is { } json)
        {
            try
            {
                file = JsonSerializer.Deserialize(json, PullJsonContext.Default.ComposeFileResult);
            }
            catch (JsonException)
            {
            }

            job.Result = null;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new ComposeReadDto(job.Status, job.Summary, file));
    }

    /// <summary>
    /// Auftrag eines Jobs für den Agent; der Inhalt einer Compose-Datei verschwindet dabei aus dem Job (er steht danach nur
    /// noch auf dem Host). null bei kaputtem Payload.
    /// </summary>
    public static ContainerManageRequest? TakeRequest(PatchJob job)
    {
        if (job.Type != JobType.ContainerManage || string.IsNullOrEmpty(job.Payload))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize(job.Payload, PullJsonContext.Default.ContainerManageRequest);
            if (request?.Content is not null)
            {
                job.Payload = JsonSerializer.Serialize(request with { Content = null }, PullJsonContext.Default.ContainerManageRequest);
            }

            return request;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
