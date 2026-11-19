using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Federation;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Site-Sharing, Seite B im Dashboard: geteilte Sites anderer Instanzen. Hinzufügen und Entfernen nur Admins; ansehen
/// und Aktionen alle angemeldeten Benutzer (was die Freigabe erlaubt, jede Aktion wird mit Benutzer protokolliert).
/// Nicht per API-Token: Skripte sollen nicht über diese Instanz auf fremden Hosts handeln.
/// </summary>
public static class RemoteSiteEndpoints
{
    public sealed record RemoteSiteDto(
        Guid Id, string Name, string RemoteName, string? LocalName, Guid InstanceId, string InstanceName, string BaseUrl, string? InstanceVersion,
        ShareLevel Level, string[] Data, DateTime? ExpiresAt, DateTime? SnapshotAt, DateTime? LastSyncAt, DateTime? LastSuccessAt, string? LastError,
        DateTime? EndedAt, string Status, int HostCount, int PendingUpdates, int SecurityUpdates, int RebootRequired, int OfflineHosts,
        DateTime CreatedAt, string CreatedBy);

    public sealed record RemoteSiteDetailDto(RemoteSiteDto Site, SiteSnapshot? Snapshot);

    public sealed record LinkRequest(string Link);

    public sealed record AddRequest(string Link, string Code);

    public sealed record RenameRequest(string? LocalName);

    public sealed record ActRequest(ShareAction Action, PatchScope Scope = PatchScope.Security);

    public static void MapRemoteSiteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/remote-sites")
            .RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.User.HasClaim(c => c.Type == "aiopatcher:token") ? Results.Forbid() : await next(context));
        var admin = group.MapGroup("").RequireAuthorization(UserAccounts.AdminPolicy);

        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/refresh", Refresh);
        group.MapGet("/{id:guid}/hosts/{hostId:guid}", HostDetail);
        group.MapGet("/{id:guid}/jobs/{jobId:guid}", JobDetail);
        group.MapPost("/{id:guid}/hosts/{hostId:guid}/actions", Act);
        admin.MapPost("/preview", Preview);
        admin.MapPost("", Add);
        admin.MapPut("/{id:guid}", Rename);
        admin.MapDelete("/{id:guid}", Remove);
    }

    private static IResult Problem(FederationException ex) =>
        Results.Problem(ex.Message, statusCode: ex.Status switch
        {
            400 or 403 or 404 or 409 or 429 => ex.Status,
            _ => StatusCodes.Status502BadGateway,
        });

    public static RemoteSiteDto ToDto(RemoteSite s, DateTime now)
    {
        var snapshot = RemoteSites.Snapshot(s);
        var hosts = snapshot?.Hosts ?? [];
        var i = s.Instance!;
        return new RemoteSiteDto(s.Id, RemoteSites.DisplayName(s), s.Name, s.LocalName, i.InstanceId, i.Name, i.BaseUrl, i.Version,
            s.Level, SharingEndpoints.DataNames(s.Data), s.ExpiresAt, s.SnapshotAt, i.LastSyncAt, i.LastSuccessAt, i.LastError,
            s.EndedAt, RemoteSites.Status(s, now), hosts.Count, hosts.Sum(h => h.PendingUpdates), hosts.Sum(h => h.SecurityUpdates),
            hosts.Count(h => h.RebootRequired), hosts.Count(h => h.Offline), s.CreatedAt, s.CreatedBy);
    }

    private static async Task<RemoteSite?> FindAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.RemoteSites.Include(s => s.Instance).ThenInclude(i => i!.Sites).SingleOrDefaultAsync(s => s.Id == id, ct);

    private static async Task<List<RemoteSiteDto>> List(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sites = await db.RemoteSites.Include(s => s.Instance).ToListAsync(ct);
        return sites.Select(s => ToDto(s, now)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        await FindAsync(db, id, ct) is { } site
            ? Results.Ok(new RemoteSiteDetailDto(ToDto(site, DateTime.UtcNow), RemoteSites.Snapshot(site)))
            : Results.NotFound();

    private static async Task<IResult> Refresh(Guid id, AppDbContext db, RemoteSites remote, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        await remote.SyncAsync(site.Instance!, now, ct);
        return Results.Ok(new RemoteSiteDetailDto(ToDto(site, now), RemoteSites.Snapshot(site)));
    }

    private static async Task<IResult> Preview(LinkRequest request, RemoteSites remote, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await remote.PreviewAsync(request.Link, ct));
        }
        catch (FederationException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> Add(AddRequest request, ClaimsPrincipal user, RemoteSites remote, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = [L.T("Code eingeben.", "Enter the code.")] });
        }

        try
        {
            var site = await remote.AddAsync(request.Link, request.Code, $"admin:{user.Identity!.Name}", DateTime.UtcNow, ct);
            return Results.Ok(ToDto(site, DateTime.UtcNow));
        }
        catch (FederationException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> Rename(Guid id, RenameRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        var name = string.IsNullOrWhiteSpace(request.LocalName) ? null : request.LocalName.Trim();
        if (name is { Length: > 100 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["localName"] = [L.T("Höchstens 100 Zeichen.", "At most 100 characters.")] });
        }

        site.LocalName = name;
        audit.Add("remote-site.renamed", $"{site.Name} → {name ?? site.Name}");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Remove(Guid id, AppDbContext db, RemoteSites remote, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        await remote.RemoveAsync(site, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> HostDetail(Guid id, Guid hostId, AppDbContext db, RemoteSites remote, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Ok(await remote.HostAsync(site, hostId, ct));
        }
        catch (FederationException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> JobDetail(Guid id, Guid jobId, long? afterId, AppDbContext db, RemoteSites remote, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Ok(await remote.JobAsync(site, jobId, afterId ?? 0, ct));
        }
        catch (FederationException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> Act(Guid id, Guid hostId, ActRequest request, ClaimsPrincipal user, AppDbContext db, RemoteSites remote, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not { } site)
        {
            return Results.NotFound();
        }

        if (RemoteSites.Status(site, DateTime.UtcNow) == "Ended")
        {
            return Results.Problem(L.T("Die Freigabe ist beendet.", "The share has ended."), statusCode: StatusCodes.Status409Conflict);
        }

        if (!FederationProtocol.Allows(site.Level, request.Action))
        {
            return Results.Problem(L.T("Diese Aktion erlaubt die Freigabe nicht.", "The share does not allow this action."), statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            return Results.Ok(await remote.ActAsync(site, hostId, request.Action, request.Scope, user.Identity!.Name ?? "?", ct));
        }
        catch (FederationException ex)
        {
            return Problem(ex);
        }
    }
}
