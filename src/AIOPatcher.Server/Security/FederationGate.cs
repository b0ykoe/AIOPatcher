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

        await PublicChannelGate.RunSignedAsync(ctx, next, ca);
    }

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
