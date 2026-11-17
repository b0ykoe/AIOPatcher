using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Einstellungen → Öffentlicher Zugang: Adresse, unter der Agents außerhalb den (intern laufenden) Server über einen
/// Reverse Proxy erreichen, optional das Dashboard; dazu was zuletzt über den Proxy ankam (Wizard-Test).
/// </summary>
public static class PublicAccessEndpoints
{
    public sealed record PublicAccessRequest(string? AgentUrl, string? DashboardUrl, string? Proxy);

    /// <param name="InternalHost">Name/IP des Servers im LAN (Ziel für den Proxy).</param>
    /// <param name="TrustedProxiesSet">AIOPatcher__TrustedProxies ist gesetzt (Netzfreigaben der Sites hinter dem Proxy).</param>
    public sealed record PublicAccessDto(
        string? AgentUrl, string? DashboardUrl, string? Proxy, string? UpdatedBy, DateTime? UpdatedAt,
        string InternalHost, string InternalAgentUrl, int ProxyPort, int DashboardPort, bool TrustedProxiesSet,
        ProxyContact? LastAgentContact, ProxyContact? LastCheck, int HostsViaPublic, string CheckPath);

    private static readonly string[] Proxies = ["nginx", "caddy", "npm", "traefik", "cloudflared", "opnsense"];

    public static void MapPublicAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/settings/public-access").RequireAuthorization(UserAccounts.AdminPolicy);
        admin.MapGet("", GetAsync);
        admin.MapPut("", SaveAsync);

        // Prüfadresse für den Wizard: nur über den Proxy-Port erreichbar (siehe PublicChannelGate), Antwort signiert.
        app.MapGet(PublicChannel.CheckPath, (HttpContext http, PublicAccess access) =>
        {
            var check = access.LastCheck;
            return Results.Ok(new
            {
                ok = true,
                server = "AIOPatcher",
                version = AgentDownloads.AgentVersion,
                configured = access.AgentUrl is not null,
                cloudflare = check?.Cloudflare ?? false,
                clientIp = check?.ClientIp,
                at = DateTime.UtcNow,
            });
        });
    }

    private static async Task<PublicAccessDto> GetAsync(PublicAccess access, AIOPatcherOptions options, AppDbContext db, CancellationToken ct) =>
        Dto(access, options, await db.Hosts.CountAsync(h => !h.IsRevoked && h.LastChannel == "public", ct));

    private static PublicAccessDto Dto(PublicAccess access, AIOPatcherOptions options, int viaPublic)
    {
        var s = access.Current;
        return new PublicAccessDto(s.AgentUrl, s.DashboardUrl, s.Proxy, s.UpdatedBy, s.UpdatedAt,
            options.PublicHostname, options.AgentApiUrl, options.AgentProxyPort, options.DashboardPort, !string.IsNullOrWhiteSpace(options.TrustedProxies),
            access.LastAgentContact, access.LastCheck, viaPublic, PublicChannel.CheckPath);
    }

    private static async Task<IResult> SaveAsync(PublicAccessRequest request, ClaimsPrincipal user, PublicAccess access, AIOPatcherOptions options,
        AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var agentUrl = PublicAccess.NormalizeUrl(request.AgentUrl);
        var dashboardUrl = PublicAccess.NormalizeUrl(request.DashboardUrl);
        if (!string.IsNullOrWhiteSpace(request.AgentUrl) && agentUrl is null)
        {
            errors["agentUrl"] = [L.T("Nur https://name (ohne Pfad), z. B. https://agents.example.com.", "Only https://name (no path), e.g. https://agents.example.com.")];
        }

        if (!string.IsNullOrWhiteSpace(request.DashboardUrl) && dashboardUrl is null)
        {
            errors["dashboardUrl"] = [L.T("Nur https://name (ohne Pfad), z. B. https://patch.example.com.", "Only https://name (no path), e.g. https://patch.example.com.")];
        }

        if (agentUrl is not null && dashboardUrl is not null && string.Equals(agentUrl, dashboardUrl, StringComparison.OrdinalIgnoreCase))
        {
            errors["dashboardUrl"] = [L.T("Dashboard und Agents brauchen verschiedene Namen.", "Dashboard and agents need different names.")];
        }

        if (request.Proxy is { } proxy && !Proxies.Contains(proxy))
        {
            errors["proxy"] = [L.T("Unbekannter Proxy.", "Unknown proxy.")];
        }

        if (agentUrl is not null && options.AgentProxyPort <= 0)
        {
            errors["agentUrl"] = [L.T("Der Proxy-Port ist abgeschaltet (AIOPatcher__AgentProxyPort=0).", "The proxy port is turned off (AIOPatcher__AgentProxyPort=0).")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var row = await db.PublicAccessSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            db.PublicAccessSettings.Add(row = new PublicAccessSettingsRecord());
        }

        row.AgentUrl = agentUrl;
        row.DashboardUrl = dashboardUrl;
        row.Proxy = request.Proxy;
        row.UpdatedBy = user.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;
        audit.Add("public-access.changed", $"agents: {agentUrl ?? "off"}, dashboard: {dashboardUrl ?? "off"}, proxy: {request.Proxy ?? "-"}");
        await db.SaveChangesAsync(ct);
        access.Use(row);
        return Results.Ok(Dto(access, options, await db.Hosts.CountAsync(h => !h.IsRevoked && h.LastChannel == "public", ct)));
    }
}
