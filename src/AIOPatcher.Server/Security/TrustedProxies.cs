using System.Net;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.HttpOverrides;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Reverse Proxy vor dem Dashboard (Traefik, nginx, Nginx Proxy Manager): Die echte Client-IP aus X-Forwarded-For
/// übernehmen, aber nur von eingetragenen Proxys und nur auf dem Dashboard-Port. Ohne Eintrag ändert sich nichts,
/// so kann niemand von außen eine IP vortäuschen (Login-Bremse, Audit-Log).
/// Schema und Host werden bewusst nicht übernommen: Das Dashboard spricht selbst HTTPS, URLs baut es nicht aus dem Host.
/// </summary>
public static class TrustedProxies
{
    /// <summary>"127.0.0.1, 172.30.0.0/24, ::1" → Netze. Wirft bei ungültigen Einträgen (Tippfehler sollen beim Start auffallen).</summary>
    public static List<System.Net.IPNetwork> Parse(string? value)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                networks.Add(new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"AIOPatcher__TrustedProxies: \"{entry}\" ist keine IP-Adresse und kein Netz (z. B. 127.0.0.1 oder 172.30.0.0/24).");
            }
        }

        return networks;
    }

    public static ForwardedHeadersOptions CreateOptions(IEnumerable<System.Net.IPNetwork> networks)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = null, // Proxy-Ketten (z. B. Cloudflare → Traefik): von rechts alle vertrauten Einträge überspringen
        };
        // Standardmäßig wäre Loopback vertraut; hier gilt nur, was eingetragen ist.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var network in networks)
        {
            forwarded.KnownIPNetworks.Add(network);
            // Kestrel lauscht dual-stack: IPv4-Clients erscheinen als ::ffff:a.b.c.d.
            if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96));
            }
        }

        return forwarded;
    }

    /// <summary>
    /// Für die Prüfen-Seite: Was sieht der Server von dieser Anfrage? Läuft nach der Middleware, d. h.
    /// X-Original-For = Proxy wurde erkannt, verbliebenes X-Forwarded-For = Proxy schickt Header, ist aber nicht eingetragen.
    /// </summary>
    public static (bool Ok, string Message) Describe(HttpContext http)
    {
        var client = http.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "?";
        var original = http.Request.Headers["X-Original-For"].ToString();
        if (IPEndPoint.TryParse(original, out var proxy))
        {
            original = (proxy.Address.IsIPv4MappedToIPv6 ? proxy.Address.MapToIPv4() : proxy.Address).ToString(); // "[::1]:51169" → "::1"
        }
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();

        if (original.Length > 0)
        {
            return (true, L.T($"Über Reverse Proxy ({original}), deine IP: {client}.", $"Via reverse proxy ({original}), your IP: {client}."));
        }

        if (forwarded.Length > 0)
        {
            return (false, L.T(
                $"Ein Proxy ({client}) schickt X-Forwarded-For, steht aber nicht in AIOPatcher__TrustedProxies. Login-Bremse und Audit-Log sehen nur den Proxy.",
                $"A proxy ({client}) sends X-Forwarded-For but is not listed in AIOPatcher__TrustedProxies. Login throttling and the audit log only see the proxy."));
        }

        return (true, L.T($"Direkt verbunden, deine IP: {client}.", $"Connected directly, your IP: {client}."));
    }

    public static IApplicationBuilder UseTrustedProxies(this IApplicationBuilder app, AIOPatcherOptions options)
    {
        var networks = Parse(options.TrustedProxies);
        if (networks.Count == 0)
        {
            return app;
        }

        var forwarded = CreateOptions(networks);
        // Agent-Port nie: dort zählt die echte TCP-Gegenstelle (Netzfreigabe pro Standort).
        return app.UseWhen(ctx => ctx.Connection.LocalPort == options.DashboardPort, branch => branch.UseForwardedHeaders(forwarded));
    }
}
