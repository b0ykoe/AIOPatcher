using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Federation;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Site-Sharing, Seite A im Dashboard: Instanzname, Einladungen (Link + Code), Freigaben ansehen und beenden.
/// Nur Admins, nur per Browser-Login (nicht per API-Token). Freigaben lassen sich nicht ändern, nur beenden.
/// </summary>
public static class SharingEndpoints
{
    public sealed record ShareDto(
        Guid Id, Guid SiteId, string SiteName, Guid PeerId, string PeerName, string? PeerVersion, ShareLevel Level, string[] Data,
        DateTime? ExpiresAt, DateTime CreatedAt, string CreatedBy, DateTime? LastAccessAt, DateTime? PeerLastSeenAt, string? PeerLastSeenIp,
        DateTime? EndedAt, string? EndedBy, bool PeerRevoked, string Status);

    public sealed record InviteDto(
        Guid Id, Guid SiteId, string SiteName, ShareLevel Level, string[] Data, int? ShareDays, DateTime ExpiresAt, int FailedAttempts,
        int MaxFailedAttempts, string Status, DateTime CreatedAt, string CreatedBy, string? UsedByPeerName);

    /// <param name="Ready">Schalter an und externe Dashboard-Adresse eingerichtet: Teilen möglich.</param>
    public sealed record SharingDto(
        Guid InstanceId, string InstanceName, bool FeatureOn, string? DashboardUrl, bool Ready, string CaFingerprint,
        IReadOnlyList<ShareDto> Shares, IReadOnlyList<InviteDto> Invites);

    public sealed record IdentityRequest(string Name);

    public sealed record CreateInviteRequest(Guid SiteId, ShareLevel Level, string[]? Data, int? InviteDays, int? ShareDays);

    public sealed record InviteCreatedDto(InviteDto Invite, string Link, string Code);

    public static void MapSharingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sharing")
            .RequireAuthorization(UserAccounts.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.User.HasClaim(c => c.Type == "aiopatcher:token") ? Results.Forbid() : await next(context));
        group.MapGet("", Get);
        group.MapPut("/identity", SaveIdentity);
        group.MapPost("/invites", CreateInvite);
        group.MapDelete("/invites/{id:guid}", RevokeInvite);
        group.MapPost("/shares/{id:guid}/end", EndShare);
        group.MapPost("/peers/{id:guid}/revoke", RevokePeer);
    }

    public static string[] DataNames(ShareData data) =>
        Enum.GetValues<ShareData>().Where(f => f != ShareData.None && data.HasFlag(f)).Select(f => f.ToString()).ToArray();

    public static ShareData ParseData(string[]? names) =>
        (names ?? []).Aggregate(ShareData.None, (all, n) => Enum.TryParse<ShareData>(n, true, out var f) ? all | f : all);

    private static string InviteStatus(ShareInvite i, DateTime now) =>
        i.UsedAt is not null ? "Used" : i.BurnedAt is not null ? "Burned" : i.RevokedAt is not null ? "Revoked" : i.ExpiresAt <= now ? "Expired" : "Open";

    private static string ShareStatus(SiteShare s, DateTime now) =>
        s.Peer!.RevokedAt is not null || s.EndedAt is not null ? "Ended" : s.ExpiresAt <= now ? "Expired" : "Active";

    private static async Task<SharingDto> Get(AppDbContext db, ServerIdentity identity, FeatureFlags features, PublicAccess access, CertificateAuthority ca, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-30);
        var shares = await db.SiteShares.Include(s => s.Site).Include(s => s.Peer)
            .Where(s => (s.EndedAt == null && s.Peer!.RevokedAt == null) || s.EndedAt > since || s.Peer!.RevokedAt > since)
            .ToListAsync(ct);
        var invites = await db.ShareInvites.Include(i => i.Site).Where(i => i.CreatedAt > since || (i.UsedAt == null && i.ExpiresAt > now)).ToListAsync(ct);
        var peerNames = await db.FederationPeers.ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return new SharingDto(identity.InstanceId, identity.Name, features.IsOn(Feature.SiteSharing), access.Current.DashboardUrl,
            FederationGate.IsAvailable(features, access), ca.RootFingerprintSha256,
            shares.OrderBy(s => ShareStatus(s, now) != "Active").ThenBy(s => s.Site!.Name).ThenBy(s => s.Peer!.Name)
                .Select(s => ToDto(s, now)).ToList(),
            invites.OrderByDescending(i => i.CreatedAt).Select(i => ToDto(i, now, i.UsedByPeerId is { } p ? peerNames.GetValueOrDefault(p) : null)).ToList());
    }

    private static ShareDto ToDto(SiteShare s, DateTime now) =>
        new(s.Id, s.SiteId, s.Site!.Name, s.PeerId, s.Peer!.Name, s.Peer.Version, s.Level, DataNames(s.Data), s.ExpiresAt, s.CreatedAt, s.CreatedBy,
            s.LastAccessAt, s.Peer.LastSeenAt, s.Peer.LastSeenIp, s.EndedAt ?? s.Peer.RevokedAt, s.EndedBy ?? s.Peer.RevokedBy, s.Peer.RevokedAt is not null,
            ShareStatus(s, now));

    private static InviteDto ToDto(ShareInvite i, DateTime now, string? usedBy) =>
        new(i.Id, i.SiteId, i.Site!.Name, i.Level, DataNames(i.Data), i.ShareDays, i.ExpiresAt, i.FailedAttempts, FederationProtocol.MaxFailedCodes,
            InviteStatus(i, now), i.CreatedAt, i.CreatedBy, usedBy);

    private static async Task<IResult> SaveIdentity(IdentityRequest request, AppDbContext db, ServerIdentity identity, AuditLog audit, CancellationToken ct)
    {
        if (ServerIdentity.ValidateName(request.Name) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [error] });
        }

        var record = await db.ServerIdentity.SingleAsync(r => r.Id == 1, ct);
        record.Name = request.Name.Trim();
        audit.Add("instance.renamed", record.Name);
        await db.SaveChangesAsync(ct);
        identity.Use(record);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateInvite(CreateInviteRequest request, ClaimsPrincipal user, AppDbContext db, SharingService sharing,
        FeatureFlags features, PublicAccess access, CertificateAuthority ca, CancellationToken ct)
    {
        if (!FederationGate.IsAvailable(features, access))
        {
            return Results.Problem(L.T("Teilen braucht den Schalter „Sites teilen“ und eine externe Dashboard-Adresse (Einstellungen → Öffentlicher Zugang).",
                "Sharing needs the “Share sites” switch and an external dashboard address (Settings → Public access)."), statusCode: StatusCodes.Status409Conflict);
        }

        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(request.Level))
        {
            errors["level"] = [L.T("Unbekannte Stufe.", "Unknown level.")];
        }

        var inviteDays = request.InviteDays ?? FederationProtocol.DefaultInviteDays;
        if (inviteDays is < 1 or > FederationProtocol.MaxInviteDays)
        {
            errors["inviteDays"] = [L.T($"Einladung: 1 bis {FederationProtocol.MaxInviteDays} Tage.", $"Invite: 1 to {FederationProtocol.MaxInviteDays} days.")];
        }

        if (request.ShareDays is < 1 or > 3650)
        {
            errors["shareDays"] = [L.T("Freigabe: 1 bis 3650 Tage oder unbegrenzt.", "Share: 1 to 3650 days or unlimited.")];
        }

        var site = await db.Sites.FindAsync([request.SiteId], ct);
        if (site is null)
        {
            errors["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var now = DateTime.UtcNow;
        var created = sharing.CreateInvite(site!, request.Level, ParseData(request.Data), inviteDays, request.ShareDays, $"admin:{user.Identity!.Name}", now);
        await db.SaveChangesAsync(ct);
        created.Invite.Site = site;
        return Results.Ok(new InviteCreatedDto(ToDto(created.Invite, now, null),
            FederationProtocol.InviteLink(access.Current.DashboardUrl!, created.Invite.Id, ca.RootFingerprintSha256), created.Code));
    }

    private static async Task<IResult> RevokeInvite(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var invite = await db.ShareInvites.Include(i => i.Site).SingleOrDefaultAsync(i => i.Id == id, ct);
        if (invite is null)
        {
            return Results.NotFound();
        }

        if (invite.UsedAt is null && invite.RevokedAt is null)
        {
            invite.RevokedAt = DateTime.UtcNow;
            audit.Add("federation.invite.revoked", invite.Site?.Name, siteId: invite.SiteId);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> EndShare(Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var share = await db.SiteShares.Include(s => s.Site).Include(s => s.Peer).SingleOrDefaultAsync(s => s.Id == id, ct);
        if (share is null)
        {
            return Results.NotFound();
        }

        if (share.EndedAt is null)
        {
            share.EndedAt = DateTime.UtcNow;
            share.EndedBy = $"admin:{user.Identity!.Name}";
            audit.Add("federation.share.ended", $"{share.Site?.Name} for {share.Peer?.Name}", siteId: share.SiteId);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RevokePeer(Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var peer = await db.FederationPeers.FindAsync([id], ct);
        if (peer is null)
        {
            return Results.NotFound();
        }

        if (peer.RevokedAt is null)
        {
            peer.RevokedAt = DateTime.UtcNow;
            peer.RevokedBy = $"admin:{user.Identity!.Name}";
            audit.Add("federation.peer.revoked", peer.Name);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }
}
