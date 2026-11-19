using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Patchpausen, zurückgehaltene Updates und Tags. Lesen alle, ändern Operator und Admin.</summary>
public static class PatchRuleEndpoints
{
    public sealed record FreezeDto(Guid Id, string Name, Guid? SiteId, string? SiteName, DateTime StartsAt, DateTime EndsAt, string? Reason, string CreatedBy, bool Active);
    public sealed record FreezeRequest(string Name, Guid? SiteId, DateTime StartsAt, DateTime EndsAt, string? Reason);

    public sealed record HoldDto(Guid Id, string Pattern, Guid? SiteId, string? SiteName, Guid? HostId, string? HostName, string? Reason, DateTime? ExpiresAt, string CreatedBy, DateTime CreatedAt);
    public sealed record HoldRequest(string Pattern, Guid? SiteId, Guid? HostId, string? Reason, DateTime? ExpiresAt);

    public sealed record BulkTagsRequest(IReadOnlyList<Guid> HostIds, IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove);
    public sealed record HostTagsRequest(IReadOnlyList<string> Tags);

    public static void MapPatchRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);

        api.MapGet("/patch-freezes", ListFreezes);
        op.MapPost("/patch-freezes", CreateFreeze);
        op.MapDelete("/patch-freezes/{id:guid}", DeleteFreeze);

        api.MapGet("/package-holds", ListHolds);
        op.MapPost("/package-holds", CreateHold);
        op.MapDelete("/package-holds/{id:guid}", DeleteHold);

        api.MapGet("/tags", ListTags);
        op.MapPut("/hosts/{id:guid}/tags", SetHostTags);
        op.MapPut("/hosts/tags", BulkTags);
    }

    // --- Patchpausen --------------------------------------------------------------------------------

    /// <summary>Mit siteId: Pausen dieser Site und Pausen für alle Sites.</summary>
    public static async Task<List<FreezeDto>> ListFreezes(Guid? siteId, AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Abgelaufene Pausen der letzten 30 Tage bleiben zur Nachvollziehbarkeit sichtbar.
        var since = now.AddDays(-30);
        return await db.PatchFreezes
            .Where(f => f.EndsAt > since && (siteId == null || f.SiteId == null || f.SiteId == siteId))
            .OrderByDescending(f => f.EndsAt)
            .Select(f => new FreezeDto(f.Id, f.Name, f.SiteId, f.Site != null ? f.Site.Name : null, f.StartsAt, f.EndsAt, f.Reason, f.CreatedBy, f.StartsAt <= now && f.EndsAt > now))
            .ToListAsync(ct);
    }

    private static async Task<IResult> CreateFreeze(FreezeRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        var starts = DateTime.SpecifyKind(request.StartsAt.ToUniversalTime(), DateTimeKind.Utc);
        var ends = DateTime.SpecifyKind(request.EndsAt.ToUniversalTime(), DateTimeKind.Utc);
        if (ends <= starts || ends <= DateTime.UtcNow)
        {
            errors["endsAt"] = [L.T("Das Ende muss nach dem Beginn und in der Zukunft liegen.", "The end must be after the start and in the future.")];
        }
        else if (ends - starts > TimeSpan.FromDays(366))
        {
            errors["endsAt"] = [L.T("Höchstens ein Jahr.", "At most one year.")];
        }

        if (request.SiteId is { } siteId && !await db.Sites.AnyAsync(s => s.Id == siteId, ct))
        {
            errors["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var freeze = new PatchFreeze
        {
            Name = name,
            SiteId = request.SiteId,
            StartsAt = starts,
            EndsAt = ends,
            Reason = Trim(request.Reason, 500),
            CreatedBy = user.Identity?.Name ?? "?",
        };
        db.PatchFreezes.Add(freeze);
        audit.Add("patch-freeze.created", $"{freeze.Name}: {freeze.StartsAt:u} – {freeze.EndsAt:u}", siteId: freeze.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(freeze.Id);
    }

    private static async Task<IResult> DeleteFreeze(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var freeze = await db.PatchFreezes.FindAsync([id], ct);
        if (freeze is null)
        {
            return Results.NotFound();
        }

        db.PatchFreezes.Remove(freeze);
        audit.Add("patch-freeze.deleted", freeze.Name, siteId: freeze.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- Zurückgehaltene Updates --------------------------------------------------------------------

    /// <summary>Mit siteId: zurückgehaltene Pakete dieser Site, aller Sites und von Hosts dieser Site.</summary>
    public static async Task<List<HoldDto>> ListHolds(Guid? hostId, Guid? siteId, AppDbContext db, CancellationToken ct)
    {
        var query = db.PackageHolds.AsQueryable();
        if (hostId is not null)
        {
            var hostSite = await db.Hosts.Where(h => h.Id == hostId).Select(h => h.SiteId).FirstOrDefaultAsync(ct);
            query = query.Where(h => h.HostId == hostId || (h.HostId == null && (h.SiteId == null || h.SiteId == hostSite)));
        }

        if (siteId is not null)
        {
            query = query.Where(h => h.HostId != null ? h.Host!.SiteId == siteId : h.SiteId == null || h.SiteId == siteId);
        }

        return await query
            .OrderBy(h => h.Pattern)
            .Select(h => new HoldDto(h.Id, h.Pattern, h.SiteId, h.Site != null ? h.Site.Name : null, h.HostId, h.Host != null ? h.Host.Name : null,
                h.Reason, h.ExpiresAt, h.CreatedBy, h.CreatedAt))
            .ToListAsync(ct);
    }

    private static async Task<IResult> CreateHold(HoldRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var pattern = request.Pattern?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (pattern.Length is < 1 or > HoldPatterns.MaxPatternLength || pattern.Trim('*', '?').Length == 0)
        {
            errors["pattern"] = [L.T("Muster angeben, z. B. linux-image-* oder KB5034441. Nur * allein ist nicht erlaubt.",
                "Enter a pattern, e.g. linux-image-* or KB5034441. A bare * is not allowed.")];
        }

        Guid? siteId = request.SiteId;
        if (request.HostId is { } hostId)
        {
            var host = await db.Hosts.FindAsync([hostId], ct);
            if (host is null)
            {
                errors["hostId"] = [L.T("Host existiert nicht.", "Host does not exist.")];
            }

            siteId = null; // Host-Regel: Site ergibt sich aus dem Host
        }
        else if (siteId is { } s && !await db.Sites.AnyAsync(x => x.Id == s, ct))
        {
            errors["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")];
        }

        if (request.ExpiresAt is { } expires && expires.ToUniversalTime() <= DateTime.UtcNow)
        {
            errors["expiresAt"] = [L.T("Ablaufdatum muss in der Zukunft liegen.", "Expiry must be in the future.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var hold = new PackageHold
        {
            Pattern = pattern,
            SiteId = siteId,
            HostId = request.HostId,
            Reason = Trim(request.Reason, 500),
            ExpiresAt = request.ExpiresAt is { } e ? DateTime.SpecifyKind(e.ToUniversalTime(), DateTimeKind.Utc) : null,
            CreatedBy = user.Identity?.Name ?? "?",
        };
        db.PackageHolds.Add(hold);
        audit.Add("package-hold.created", pattern, siteId: siteId, hostId: request.HostId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(hold.Id);
    }

    private static async Task<IResult> DeleteHold(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var hold = await db.PackageHolds.FindAsync([id], ct);
        if (hold is null)
        {
            return Results.NotFound();
        }

        db.PackageHolds.Remove(hold);
        audit.Add("package-hold.deleted", hold.Pattern, siteId: hold.SiteId, hostId: hold.HostId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- Tags ---------------------------------------------------------------------------------------

    private static async Task<List<string>> ListTags(AppDbContext db, CancellationToken ct) =>
        (await db.Hosts.Where(h => h.Tags != null).Select(h => h.Tags).ToListAsync(ct))
        .SelectMany(PatchRules.SplitTags)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    private static async Task<IResult> SetHostTags(Guid id, HostTagsRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        host.Tags = PatchRules.NormalizeTags(request.Tags);
        audit.Add("host.tags", $"{host.Name}: {host.Tags}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(PatchRules.SplitTags(host.Tags));
    }

    private static async Task<IResult> BulkTags(BulkTagsRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var ids = (request.HostIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > JobPlanner.MaxHostsPerRequest)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hostIds"] = [L.T($"1 bis {JobPlanner.MaxHostsPerRequest} Hosts auswählen.", $"Select 1 to {JobPlanner.MaxHostsPerRequest} hosts.")],
            });
        }

        var add = PatchRules.SplitTags(PatchRules.NormalizeTags(request.Add));
        var remove = PatchRules.SplitTags(PatchRules.NormalizeTags(request.Remove)).ToHashSet();
        var hosts = await db.Hosts.Where(h => ids.Contains(h.Id)).ToListAsync(ct);
        foreach (var host in hosts)
        {
            host.Tags = PatchRules.NormalizeTags(PatchRules.SplitTags(host.Tags).Where(t => !remove.Contains(t)).Concat(add));
        }

        audit.Add("host.tags.bulk", $"{hosts.Count} hosts: +[{string.Join(',', add)}] -[{string.Join(',', remove)}]");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { updated = hosts.Count });
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
