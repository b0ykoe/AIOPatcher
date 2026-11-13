using System.Security.Cryptography;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
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
        int SecurityUpdates);

    public sealed record UpdateDto(string Name, string? CurrentVersion, string? AvailableVersion, bool IsSecurity, string? Classification, DateTime FirstSeenAt);
    public sealed record AddressDto(string Address, int? PrefixLength, string? InterfaceName, string? SubnetName);

    public sealed record HostDetailDto(
        HostSummaryDto Summary,
        string? KernelVersion,
        TimeSpan? Uptime,
        DateTime EnrolledAt,
        DateTime CertificateExpiresAt,
        IReadOnlyList<UpdateDto> Updates,
        IReadOnlyList<AddressDto> Addresses);

    public sealed record OverviewDto(
        int Sites,
        int Hosts,
        int Online,
        int Offline,
        int PendingUpdates,
        int SecurityUpdates,
        int HostsWithSecurityUpdates,
        int RebootRequired,
        int NativeAutoUpdateConflicts);

    public sealed record CreateEnrollmentTokenRequest(Guid SiteId, string? Label);

    public sealed record EnrollmentTokenCreatedDto(
        Guid Id,
        string Token,
        DateTime ExpiresAt,
        string ServerUrl,
        string CaFingerprintSha256);

    public sealed record EnrollmentTokenDto(Guid Id, Guid SiteId, string? Label, DateTime CreatedAt, DateTime ExpiresAt);

    public static void MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/overview", GetOverview);
        api.MapGet("/hosts", GetHosts);
        api.MapGet("/hosts/{id:guid}", GetHost);
        api.MapPost("/hosts/{id:guid}/revoke", RevokeHost);
        api.MapDelete("/hosts/{id:guid}", DeleteHost);

        api.MapGet("/enrollment-tokens", GetEnrollmentTokens);
        api.MapPost("/enrollment-tokens", CreateEnrollmentToken);
        api.MapDelete("/enrollment-tokens/{id:guid}", DeleteEnrollmentToken);
    }

    private static IQueryable<HostSummaryDto> ProjectSummaries(IQueryable<ManagedHost> hosts, DateTime onlineSince) =>
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
            h.PendingUpdates.Count(u => u.IsSecurity)));

    private static async Task<IResult> GetOverview(AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var hosts = await ProjectSummaries(db.Hosts.Where(h => !h.IsRevoked), DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct);
        return Results.Ok(new OverviewDto(
            Sites: await db.Sites.CountAsync(ct),
            Hosts: hosts.Count,
            Online: hosts.Count(h => h.Status == HostStatus.Online),
            Offline: hosts.Count(h => h.Status == HostStatus.Offline),
            PendingUpdates: hosts.Sum(h => h.PendingUpdates),
            SecurityUpdates: hosts.Sum(h => h.SecurityUpdates),
            HostsWithSecurityUpdates: hosts.Count(h => h.SecurityUpdates > 0),
            RebootRequired: hosts.Count(h => h.RebootRequired),
            NativeAutoUpdateConflicts: hosts.Count(h => h.NativeAutoUpdatesEnabled)));
    }

    private static async Task<IResult> GetHosts(Guid? siteId, AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var query = db.Hosts.AsQueryable();
        if (siteId is not null)
        {
            query = query.Where(h => h.SiteId == siteId);
        }

        var hosts = await ProjectSummaries(query, DateTime.UtcNow - options.OfflineAfter).ToListAsync(ct);
        return Results.Ok(hosts.OrderBy(h => h.SiteName).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetHost(Guid id, AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var summary = await ProjectSummaries(db.Hosts.Where(h => h.Id == id), DateTime.UtcNow - options.OfflineAfter).SingleOrDefaultAsync(ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        var host = await db.Hosts.AsNoTracking().SingleAsync(h => h.Id == id, ct);
        var updates = await db.PendingUpdates.Where(u => u.HostId == id)
            .OrderByDescending(u => u.IsSecurity).ThenBy(u => u.Name)
            .Select(u => new UpdateDto(u.Name, u.CurrentVersion, u.AvailableVersion, u.IsSecurity, u.Classification, u.FirstSeenAt))
            .ToListAsync(ct);
        var addresses = await db.IpAddresses.Where(a => a.HostId == id)
            .OrderBy(a => a.InterfaceName).ThenBy(a => a.Address)
            .Select(a => new AddressDto(a.Address, a.PrefixLength, a.InterfaceName, a.Subnet != null ? a.Subnet.Name : null))
            .ToListAsync(ct);

        return Results.Ok(new HostDetailDto(summary, host.KernelVersion, host.Uptime, host.EnrolledAt, host.CertificateExpiresAt, updates, addresses));
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
        AuditLog audit,
        CancellationToken ct)
    {
        var site = await db.Sites.FindAsync([request.SiteId], ct);
        if (site is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = ["Site existiert nicht."] });
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

        return Results.Ok(new EnrollmentTokenCreatedDto(entity.Id, token, entity.ExpiresAt, options.AgentApiUrl, ca.RootFingerprintSha256));
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
