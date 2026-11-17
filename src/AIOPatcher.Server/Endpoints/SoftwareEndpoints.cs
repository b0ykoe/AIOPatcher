using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Installierte Programme pro Host und hostübergreifend; Programm-Updates per winget (Windows).</summary>
public static class SoftwareEndpoints
{
    public sealed record SoftwareDto(long Id, string Name, string? Version, string? Publisher, string Source, string? PackageId, string? AvailableVersion,
        string? VendorUrl, string? InfoUrl);

    public sealed record SoftwareSummaryDto(string Name, string Source, string? PackageId, int Hosts, int Outdated, IReadOnlyList<string> Versions, string? AvailableVersion, string? InfoUrl);
    public sealed record AppUpdateRequest(IReadOnlyList<string> PackageIds);

    public static void MapSoftwareEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/hosts/{id:guid}/software", ForHost);
        api.MapGet("/software", Summary);
        api.MapPost("/hosts/{id:guid}/app-updates", StartAppUpdate).RequireAuthorization(UserAccounts.OperatorPolicy);
    }

    private static async Task<IResult> ForHost(Guid id, AppDbContext db, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var apps = await db.InstalledApps.Where(a => a.HostId == id).OrderBy(a => a.Name).ToListAsync(ct);
        return Results.Ok(apps.Select(a => new SoftwareDto(a.Id, a.Name, a.Version, a.Publisher, a.Source, a.PackageId, a.AvailableVersion, a.Url, InfoUrl(a.Source, a.PackageId, host.OsName))));
    }

    /// <summary>Alle Programme über alle Hosts: wie verbreitet, welche Versionen, wo veraltet.</summary>
    private static async Task<List<SoftwareSummaryDto>> Summary(bool? onlyUpdates, string? source, AppDbContext db, CancellationToken ct)
    {
        var query = db.InstalledApps.AsQueryable();
        if (!string.IsNullOrEmpty(source))
        {
            query = query.Where(a => a.Source == source);
        }

        if (onlyUpdates == true)
        {
            query = query.Where(a => a.AvailableVersion != null);
        }

        var rows = await query.Select(a => new { a.Name, a.Source, a.PackageId, a.Version, a.AvailableVersion, a.HostId, OsName = a.Host!.OsName }).ToListAsync(ct);
        return rows
            .GroupBy(r => (r.Source, Key: r.PackageId ?? r.Name))
            .Select(g => new SoftwareSummaryDto(
                g.First().Name,
                g.Key.Source,
                g.First().PackageId,
                g.Select(r => r.HostId).Distinct().Count(),
                g.Where(r => r.AvailableVersion != null).Select(r => r.HostId).Distinct().Count(),
                g.Select(r => r.Version ?? "?").Distinct().OrderDescending().Take(5).ToList(),
                g.Select(r => r.AvailableVersion).FirstOrDefault(v => v != null),
                InfoUrl(g.Key.Source, g.First().PackageId, g.First().OsName)))
            .OrderByDescending(s => s.Outdated).ThenByDescending(s => s.Hosts).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(2000)
            .ToList();
    }

    private static async Task<IResult> StartAppUpdate(Guid id, AppUpdateRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.AppUpdates))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.AppUpdates), statusCode: StatusCodes.Status403Forbidden);
        }

        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        if (host.OsFamily != OsFamily.Windows)
        {
            return Results.Problem(L.T("Programm-Updates gibt es nur für Windows (winget). Linux-Pakete laufen über normale Patch-Jobs.",
                "Program updates are only available on Windows (winget). Linux packages are updated by normal patch jobs."), statusCode: StatusCodes.Status409Conflict);
        }

        var known = await db.InstalledApps.Where(a => a.HostId == id && a.PackageId != null && a.AvailableVersion != null).Select(a => a.PackageId!).ToListAsync(ct);
        var packages = (request.PackageIds ?? []).Where(p => known.Contains(p)).Distinct().Take(100).ToList();
        if (packages.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["packageIds"] = [L.T("Keine aktualisierbaren Programme gewählt.", "No updatable programs selected.")] });
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem(L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running."), statusCode: StatusCodes.Status409Conflict);
        }

        var job = new PatchJob
        {
            HostId = id,
            Type = JobType.AppUpdate,
            Scope = PatchScope.All,
            RequestedBy = $"admin:{user.Identity?.Name}",
            Packages = string.Join(',', packages),
        };
        db.Jobs.Add(job);
        audit.Add("job.created", $"AppUpdate ({packages.Count}) for {host.Name}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { job.Id });
    }

    /// <summary>
    /// Wo es mehr Infos gibt: winget-Manifest (enthält Hersteller- und Release-Notes-Link), Debian-Tracker bzw. Launchpad.
    /// </summary>
    public static string? InfoUrl(string source, string? packageId, string? osName)
    {
        if (string.IsNullOrEmpty(packageId))
        {
            return null;
        }

        return source switch
        {
            "winget" or "registry" when packageId.Contains('.') =>
                $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{char.ToLowerInvariant(packageId[0])}/{packageId.Replace('.', '/')}",
            "dpkg" when osName?.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) == true => $"https://launchpad.net/ubuntu/+source/{Uri.EscapeDataString(packageId)}",
            "dpkg" => $"https://tracker.debian.org/pkg/{Uri.EscapeDataString(packageId)}",
            "snap" => $"https://snapcraft.io/{Uri.EscapeDataString(packageId)}",
            _ => null,
        };
    }

    /// <summary>Vom Check-in: Inventar ersetzen.</summary>
    public static async Task ReplaceAsync(AppDbContext db, ManagedHost host, IReadOnlyList<InstalledSoftware> software, CancellationToken ct)
    {
        await db.InstalledApps.Where(a => a.HostId == host.Id).ExecuteDeleteAsync(ct);
        foreach (var item in software.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Name)).Take(5000))
        {
            db.InstalledApps.Add(new InstalledApp
            {
                HostId = host.Id,
                Name = Cut(item.Name, 300)!,
                Version = Cut(item.Version, 100),
                Publisher = Cut(item.Publisher, 200),
                Source = Cut(item.Source, 16) ?? "?",
                PackageId = Cut(item.PackageId, 200),
                AvailableVersion = Cut(item.AvailableVersion, 100),
                SourceVersion = item.Source == "dpkg" ? Cut(item.SourceVersion, 100) : null,
                Url = item.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? Cut(u, 500) : null,
            });
        }

        static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
    }
}
