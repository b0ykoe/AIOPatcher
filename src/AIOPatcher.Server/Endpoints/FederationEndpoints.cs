using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Federation;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Security;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Site-Sharing, Seite A: Schnittstelle für andere AIOPatcher-Instanzen (/federation/v1). Erreichbar nur über den
/// Dashboard-Port und nur durch <see cref="FederationGate"/> (Schalter, externe Adresse, Signatur der Gegenstelle).
/// </summary>
public static class FederationEndpoints
{
    public static void MapFederationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(FederationProtocol.BasePath);
        group.MapGet("/info", Info);
        group.MapPost("/join", Join).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        group.MapPost("/renew", Renew);
        group.MapPost("/leave", Leave);
        group.MapGet("/shares", Shares);
        group.MapGet("/sites/{siteId:guid}/snapshot", Snapshot);
        group.MapGet("/sites/{siteId:guid}/hosts/{hostId:guid}", HostDetail);
        group.MapGet("/sites/{siteId:guid}/jobs/{jobId:guid}", JobDetail);
        group.MapPost("/sites/{siteId:guid}/hosts/{hostId:guid}/actions", Act);
        group.MapGet("/sites/{siteId:guid}/views/{view}", View);
    }

    private static IResult Json<T>(T value) => Results.Json(value, FederationProtocol.Json);

    private static IResult Fail(int status, string? error) => Results.Json(new { error }, FederationProtocol.Json, statusCode: status);

    private static IResult From<T>(FederationResult<T> result) => result.Ok ? Json(result.Value) : Fail(result.Status, result.Error);

    /// <summary>Vom Gate geprüfte Gegenstelle; ohne (sollte nie vorkommen) 401.</summary>
    private static bool TryPeer(HttpContext http, out FederationPeer peer)
    {
        peer = FederationGate.Peer(http)!;
        return peer is not null;
    }

    private static IResult Info(ServerIdentity identity, CertificateAuthority ca) =>
        Json(new FederationInfo("AIOPatcher", AgentDownloads.AgentVersion, [FederationProtocol.Version], identity.InstanceId, identity.Name,
            ca.RootCertificatePem, FederationProtocol.Capabilities));

    private static async Task<IResult> Join(JoinRequest request, HttpContext http, SharingService sharing, CancellationToken ct) =>
        From(await sharing.JoinAsync(request, FederationGate.Peer(http), DateTime.UtcNow, ct));

    private static async Task<IResult> Renew(RenewRequest request, HttpContext http, SharingService sharing, CancellationToken ct) =>
        TryPeer(http, out var peer) ? From(await sharing.RenewAsync(peer, request, ct)) : Results.Unauthorized();

    private static async Task<IResult> Leave(LeaveRequest request, HttpContext http, SharingService sharing, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        await sharing.LeaveAsync(peer, request.ShareId, DateTime.UtcNow, ct);
        return Json(new { ok = true });
    }

    private static async Task<IResult> Shares(HttpContext http, SharingService sharing, ServerIdentity identity, CancellationToken ct) =>
        TryPeer(http, out var peer) ? Json(await sharing.SharesAsync(peer, identity.Name, DateTime.UtcNow, ct)) : Results.Unauthorized();

    private static async Task<IResult> Snapshot(Guid siteId, HttpContext http, SharingService sharing, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        var now = DateTime.UtcNow;
        return await sharing.FindShareAsync(peer, siteId, now, ct) is { } share
            ? Json(await sharing.SnapshotAsync(share, now, ct))
            : Fail(StatusCodes.Status404NotFound, "Not shared.");
    }

    private static async Task<IResult> HostDetail(Guid siteId, Guid hostId, HttpContext http, SharingService sharing, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        if (await sharing.FindShareAsync(peer, siteId, DateTime.UtcNow, ct) is not { } share)
        {
            return Fail(StatusCodes.Status404NotFound, "Not shared.");
        }

        return await sharing.HostDetailAsync(share, hostId, ct) is { } detail ? Json(detail) : Fail(StatusCodes.Status404NotFound, "Host not found.");
    }

    private static async Task<IResult> JobDetail(Guid siteId, Guid jobId, long? afterId, HttpContext http, SharingService sharing, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        if (await sharing.FindShareAsync(peer, siteId, DateTime.UtcNow, ct) is not { } share)
        {
            return Fail(StatusCodes.Status404NotFound, "Not shared.");
        }

        return await sharing.JobDetailAsync(share, jobId, afterId ?? 0, ct) is { } detail ? Json(detail) : Fail(StatusCodes.Status404NotFound, "Job not found.");
    }

    /// <summary>
    /// Weitere Seiten nur lesend, wenn die Freigabe den Datenumfang „Pages“ hat. Liefert dieselben Daten wie die API des
    /// Dashboards, aber fest auf die geteilte Site eingeschränkt.
    /// </summary>
    private static async Task<IResult> View(Guid siteId, string view, bool? onlyUpdates, string? source, HttpContext http, SharingService sharing,
        AppDbContext db, FeatureFlags features, VirtualizationProviderFactory providers, CveService cves, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        return await sharing.FindShareAsync(peer, siteId, DateTime.UtcNow, ct) is { } share
            ? await ViewAsync(share, view, onlyUpdates, source, db, features, providers, cves, ct)
            : Fail(StatusCodes.Status404NotFound, "Not shared.");
    }

    /// <summary>Daten einer weiteren Seite, fest auf die Site der Freigabe eingeschränkt; 403 ohne Datenumfang „Pages“.</summary>
    public static async Task<IResult> ViewAsync(SiteShare share, string view, bool? onlyUpdates, string? source,
        AppDbContext db, FeatureFlags features, VirtualizationProviderFactory providers, CveService cves, CancellationToken ct)
    {
        if (!share.Data.HasFlag(ShareData.Pages))
        {
            return Fail(StatusCodes.Status403Forbidden, "Pages are not shared.");
        }

        return view switch
        {
            "utilization" => await MetricsEndpoints.UtilizationAsync(share.SiteId, db, features, providers, ct),
            "vulnerabilities" => Results.Json(await VulnerabilityEndpoints.ListAsync(share.SiteId, db, cves, ct), FederationProtocol.Json),
            "software" => Results.Json(await SoftwareEndpoints.Summary(onlyUpdates, source, share.SiteId, db, ct), FederationProtocol.Json),
            _ => Fail(StatusCodes.Status404NotFound, "Unknown view."),
        };
    }

    private static async Task<IResult> Act(Guid siteId, Guid hostId, ActionRequest request, HttpContext http, SharingService sharing, CancellationToken ct)
    {
        if (!TryPeer(http, out var peer))
        {
            return Results.Unauthorized();
        }

        var now = DateTime.UtcNow;
        if (await sharing.FindShareAsync(peer, siteId, now, ct) is not { } share)
        {
            return Fail(StatusCodes.Status404NotFound, "Not shared.");
        }

        return From(await sharing.ActAsync(peer, share, hostId, request, now, ct));
    }
}
