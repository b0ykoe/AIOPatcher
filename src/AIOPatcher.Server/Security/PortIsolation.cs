using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Trennt Dashboard und Agent-API anhand des lokalen Ports (Socket-Ebene, nicht über den Host-Header fälschbar):
/// - Agent-Port: nur /agent/*, Client-Zertifikat Pflicht (außer Enrollment und Ping).
/// - Dashboard-Port: alles außer /agent/*; /federation/* nur signiert (siehe <see cref="FederationGate"/>).
/// - Proxy-Port: nur /agent/*, signierte Anfragen statt Client-Zertifikat (siehe <see cref="PublicChannelGate"/>).
/// </summary>
public static class PortIsolation
{
    private static readonly string[] AnonymousAgentPaths = ["/agent/enroll", "/agent/ping", "/agent/ca", "/agent/install.sh", "/agent/install.ps1"];

    public static IApplicationBuilder UsePortIsolation(this IApplicationBuilder app, AIOPatcherOptions options) =>
        app.Use(async (ctx, next) =>
        {
            if (PublicChannelGate.IsProxyPort(ctx, options))
            {
                await PublicChannelGate.HandleAsync(ctx, next, options);
                return;
            }

            var isAgentPort = ctx.Connection.LocalPort == options.AgentPort;
            var isAgentPath = ctx.Request.Path.StartsWithSegments("/agent", StringComparison.OrdinalIgnoreCase);

            if (isAgentPort != isAgentPath)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (isAgentPort)
            {
                var anonymousAllowed = AnonymousAgentPaths.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase))
                    || (HttpMethods.IsGet(ctx.Request.Method)
                        && ctx.Request.Path.StartsWithSegments("/agent/download", StringComparison.OrdinalIgnoreCase));
                var certificate = await ctx.Connection.GetClientCertificateAsync(ctx.RequestAborted);
                if ((certificate is null && !anonymousAllowed) || (certificate is not null && CertificateAuthority.IsPeerCertificate(certificate)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            else if (FederationGate.IsFederationPath(ctx.Request.Path))
            {
                // Dashboard-Port, Site-Sharing: nur signierte Anfragen anderer Instanzen, kein Cookie, kein API-Token.
                await FederationGate.HandleAsync(ctx, next);
                return;
            }

            await next();
        });
}
