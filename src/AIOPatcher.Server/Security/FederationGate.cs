using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Federation;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Site-Sharing (Seite A): /federation/* auf dem Dashboard-Port. Geht nur, wenn der Schalter an und die externe
/// Dashboard-Adresse eingerichtet ist, sonst 404 (verrät nichts). Außer info und join muss jede Anfrage mit dem
/// Zertifikat einer aktiven Gegenstelle signiert sein (wie der öffentliche Agent-Weg); jede Antwort signiert der Server.
/// </summary>
public static class FederationGate
{
    /// <summary>Geprüfte Gegenstelle dieser Anfrage (<see cref="FederationPeer"/>).</summary>
    public const string PeerItem = "aiop.federation.peer";

    public static bool IsFederationPath(PathString path) => path.StartsWithSegments("/federation", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnonymous(PathString path) =>
        path.Equals(FederationProtocol.InfoPath, StringComparison.OrdinalIgnoreCase) || path.Equals(FederationProtocol.JoinPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable(FeatureFlags features, PublicAccess access) =>
        features.IsOn(Feature.SiteSharing) && !string.IsNullOrWhiteSpace(access.Current.DashboardUrl);

    public static async Task HandleAsync(HttpContext ctx, Func<Task> next)
    {
        var services = ctx.RequestServices;
        var access = services.GetRequiredService<PublicAccess>();
        if (!IsAvailable(services.GetRequiredService<FeatureFlags>(), access))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ca = services.GetRequiredService<CertificateAuthority>();
        var signed = ctx.Request.Headers.ContainsKey(PublicChannel.SignatureHeader);
        if (signed || !IsAnonymous(ctx.Request.Path))
        {
            var (peer, error) = await AuthenticateAsync(ctx, ca, access, services.GetRequiredService<AppDbContext>());
            if (peer is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync(error);
                return;
            }

            ctx.Items[PeerItem] = peer;
        }

        await PublicChannelGate.RunSignedAsync(ctx, async () =>
        {
            // Gespiegelte Dashboard-API: auf die echte API-Adresse umschreiben (vor dem Routing), sonst wie bisher.
            if (Peer(ctx) is { } mirrorPeer && MirrorPattern.IsMatch(ctx.Request.Path.Value ?? ""))
            {
                var (status, error) = await PrepareMirrorAsync(ctx, mirrorPeer, services.GetRequiredService<SharingService>(),
                    services.GetRequiredService<AppDbContext>(), DateTime.UtcNow);
                if (status != StatusCodes.Status200OK)
                {
                    ctx.Response.StatusCode = status;
                    await ctx.Response.WriteAsJsonAsync(new { error }, FederationProtocol.Json);
                    return;
                }
            }

            await next();
        }, ca);
    }

    private static readonly System.Text.RegularExpressions.Regex MirrorPattern = new(
        @"^/federation/v1/sites/(?<site>[0-9a-fA-F-]{36})/mirror(?<api>/api/.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Anfrage der Gegenstelle an die gespiegelte API prüfen und umschreiben: nur GET, nur mit Datenumfang „Pages“, nur
    /// Adressen aus FederationProtocol.MatchMirror; Listen bekommen fest die siteId der Freigabe, Host- und Job-Details
    /// nur, wenn sie zur Site gehören. Danach läuft die Anfrage als lesender Benutzer „peer:Name“ durch die normale API.
    /// </summary>
    public static async Task<(int Status, string? Error)> PrepareMirrorAsync(HttpContext ctx, FederationPeer peer, SharingService sharing, AppDbContext db, DateTime now)
    {
        var match = MirrorPattern.Match(ctx.Request.Path.Value ?? "");
        if (!match.Success || !Guid.TryParse(match.Groups["site"].Value, out var siteId))
        {
            return (StatusCodes.Status404NotFound, "Not found.");
        }

        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            return (StatusCodes.Status405MethodNotAllowed, "Read-only.");
        }

        if (await sharing.FindShareAsync(peer, siteId, now, ctx.RequestAborted) is not { } share)
        {
            return (StatusCodes.Status404NotFound, "Not shared.");
        }

        if (!share.Data.HasFlag(ShareData.Pages))
        {
            return (StatusCodes.Status403Forbidden, "Pages are not shared.");
        }

        var api = match.Groups["api"].Value;
        if (FederationProtocol.MatchMirror(api) is not { } rule)
        {
            return (StatusCodes.Status404NotFound, "Not shared.");
        }

        var allowed = rule.Guard switch
        {
            FederationProtocol.MirrorGuard.Host => await db.Hosts.AnyAsync(h => h.Id == rule.Id && h.SiteId == share.SiteId, ctx.RequestAborted),
            FederationProtocol.MirrorGuard.Job => await db.Jobs.AnyAsync(j => j.Id == rule.Id && j.Host!.SiteId == share.SiteId, ctx.RequestAborted),
            _ => true,
        };
        if (!allowed)
        {
            return (StatusCodes.Status404NotFound, "Not found.");
        }

        // siteId nie von der Gegenstelle übernehmen; Listen fest auf die geteilte Site.
        var query = ctx.Request.Query.Where(q => !q.Key.Equals("siteId", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v))).ToList();
        if (rule.Guard == FederationProtocol.MirrorGuard.Site)
        {
            query.Add(new KeyValuePair<string, string?>("siteId", share.SiteId.ToString()));
        }

        ctx.Request.Path = api;
        ctx.Request.QueryString = QueryString.Create(query);
        ctx.SetEndpoint(null);
        ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, $"peer:{peer.Name}"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, nameof(UserRole.Viewer)),
            new System.Security.Claims.Claim(FederationClaim, peer.Id.ToString()),
        ], "Federation"));
        return (StatusCodes.Status200OK, null);
    }

    /// <summary>Kennzeichnet Anfragen einer Gegenstelle über die gespiegelte API.</summary>
    public const string FederationClaim = "aiopatcher:federation-peer";

    /// <summary>Signatur prüfen und die aktive Gegenstelle zum Zertifikat suchen (aktuelles oder erneuertes).</summary>
    public static async Task<(FederationPeer? Peer, string Error)> AuthenticateAsync(HttpContext ctx, CertificateAuthority ca, PublicAccess access, AppDbContext db)
    {
        var (certificate, error) = await PublicChannelGate.VerifyRequestAsync(ctx, ca, access, DateTimeOffset.UtcNow);
        if (certificate is null)
        {
            return (null, error);
        }

        if (CertificateAuthority.GetPeerId(certificate) is not { } peerId)
        {
            return (null, "Not a peer certificate.");
        }

        var thumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
        var peer = await db.FederationPeers.SingleOrDefaultAsync(p => p.Id == peerId, ctx.RequestAborted);
        if (peer is null || peer.RevokedAt is not null)
        {
            return (null, "Access ended.");
        }

        var now = DateTime.UtcNow;
        var changed = false;
        if (thumbprint == peer.PendingCertificateThumbprint && peer.PendingCertificateExpiresAt is { } pendingExpires)
        {
            // Erneuertes Zertifikat zum ersten Mal benutzt: ab jetzt gilt nur noch das neue.
            peer.CertificateThumbprint = thumbprint;
            peer.CertificateExpiresAt = pendingExpires;
            peer.PendingCertificateThumbprint = null;
            peer.PendingCertificateExpiresAt = null;
            changed = true;
        }
        else if (thumbprint != peer.CertificateThumbprint)
        {
            return (null, "Unknown certificate.");
        }

        if (changed || peer.LastSeenAt is null || now - peer.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            peer.LastSeenAt = now;
            peer.LastSeenIp = PublicChannelGate.TrustedClientIp(ctx, ctx.RequestServices.GetRequiredService<AIOPatcher.Server.Options.AIOPatcherOptions>())?.ToString();
            await db.SaveChangesAsync(ctx.RequestAborted);
        }

        return (peer, "");
    }

    /// <summary>Gegenstelle der Anfrage (vom Gate gesetzt), sonst null.</summary>
    public static FederationPeer? Peer(HttpContext ctx) => ctx.Items[PeerItem] as FederationPeer;
}
