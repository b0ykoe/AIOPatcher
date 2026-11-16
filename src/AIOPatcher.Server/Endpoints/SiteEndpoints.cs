using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

public static class SiteEndpoints
{
    public sealed record SiteDto(Guid Id, string Name, string? Description, string? AllowedAgentCidrs, int HostCount, DateTime CreatedAt);
    public sealed record SiteRequest(string Name, string? Description, string? AllowedAgentCidrs);

    public static void MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup("/api/sites").RequireAuthorization();
        var admin = sites.MapGroup("").RequireAuthorization(UserAccounts.AdminPolicy);

        sites.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await db.Sites
                .OrderBy(s => s.Name)
                .Select(s => new SiteDto(s.Id, s.Name, s.Description, s.AllowedAgentCidrs, s.Hosts.Count, s.CreatedAt))
                .ToListAsync(ct));

        admin.MapPost("/", async (SiteRequest request, AppDbContext db, AuditLog audit, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var name = request.Name.Trim();
            if (await db.Sites.AnyAsync(s => s.Name == name, ct))
            {
                return Results.Problem(L.T($"Site '{name}' existiert bereits.", $"Site '{name}' already exists."),statusCode: StatusCodes.Status409Conflict);
            }

            var site = new Site { Name = name, Description = request.Description?.Trim(), AllowedAgentCidrs = NormalizeCidrs(request.AllowedAgentCidrs) };
            db.Sites.Add(site);
            audit.Add("site.created", site.Name, siteId: site.Id);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/sites/{site.Id}", new SiteDto(site.Id, site.Name, site.Description, site.AllowedAgentCidrs, 0, site.CreatedAt));
        });

        admin.MapPut("/{id:guid}", async (Guid id, SiteRequest request, AppDbContext db, AuditLog audit, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var site = await db.Sites.FindAsync([id], ct);
            if (site is null)
            {
                return Results.NotFound();
            }

            var name = request.Name.Trim();
            if (await db.Sites.AnyAsync(s => s.Name == name && s.Id != id, ct))
            {
                return Results.Problem(L.T($"Site '{name}' existiert bereits.", $"Site '{name}' already exists."),statusCode: StatusCodes.Status409Conflict);
            }

            site.Name = name;
            site.Description = request.Description?.Trim();
            site.AllowedAgentCidrs = NormalizeCidrs(request.AllowedAgentCidrs);
            audit.Add("site.updated", site.Name, siteId: site.Id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, AuditLog audit, CancellationToken ct) =>
        {
            var site = await db.Sites.Include(s => s.Hosts).SingleOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }

            if (site.Hosts.Count > 0)
            {
                return Results.Problem(L.T("Site enthält noch Hosts und kann nicht gelöscht werden.", "The site still contains hosts and cannot be deleted."),statusCode: StatusCodes.Status409Conflict);
            }

            db.Sites.Remove(site);
            audit.Add("site.deleted", site.Name, siteId: site.Id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static IResult? Validate(SiteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        if (!string.IsNullOrWhiteSpace(request.AllowedAgentCidrs))
        {
            var invalid = SplitCidrs(request.AllowedAgentCidrs).Where(c => !System.Net.IPNetwork.TryParse(c, out _)).ToList();
            if (invalid.Count > 0)
            {
                errors["allowedAgentCidrs"] = [L.T($"Ungültige CIDRs: {string.Join(", ", invalid)}", $"Invalid CIDRs: {string.Join(", ", invalid)}")];
            }
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static IEnumerable<string> SplitCidrs(string value) =>
        value.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NormalizeCidrs(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : string.Join(",", SplitCidrs(value).Select(c => System.Net.IPNetwork.Parse(c).ToString()));
}
