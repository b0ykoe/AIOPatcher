using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Proxy-Port (HTTP, nur für den Reverse Proxy im LAN): Agents von außen kommen hier über einen Proxy an, der TLS
/// beendet. Statt eines Client-Zertifikats trägt jede Anfrage eine Signatur (siehe <see cref="PublicChannel"/>); jede
/// Antwort signiert der Server, damit der Agent Aufträge auch durch fremde Proxys (Cloudflare) prüfen kann.
/// </summary>
public static class PublicChannelGate
{
    /// <summary>Geprüftes Agent-Zertifikat dieser Anfrage (statt TLS-Client-Zertifikat).</summary>
    public const string CertificateItem = "aiop.public.cert";

    /// <summary>IP des Agents laut Proxy (nur bei vertrauenswürdigem Proxy), sonst die des Proxys.</summary>
    public const string ClientIpItem = "aiop.public.client-ip";

    private static readonly string[] AnonymousPaths = ["/agent/enroll", "/agent/ping", "/agent/ca", "/agent/install.sh", "/agent/install.ps1", PublicChannel.CheckPath];

    /// <summary>Inhalt wird separat geprüft (SHA-256 im Installationsbefehl bzw. Update-Signatur): nicht puffern.</summary>
    private static bool IsBulkDownload(PathString path) =>
        path.StartsWithSegments("/agent/download", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/agent/install.sh", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/agent/install.ps1", StringComparison.OrdinalIgnoreCase);

    public static bool IsProxyPort(HttpContext ctx, AIOPatcherOptions options) =>
        options.AgentProxyPort > 0 && ctx.Connection.LocalPort == options.AgentProxyPort;

    public static async Task HandleAsync(HttpContext ctx, Func<Task> next, AIOPatcherOptions options)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/agent", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var access = ctx.RequestServices.GetRequiredService<PublicAccess>();
        var ca = ctx.RequestServices.GetRequiredService<CertificateAuthority>();
        var isCheck = path.Equals(PublicChannel.CheckPath, StringComparison.OrdinalIgnoreCase);
        if (access.AgentUrl is null && !isCheck)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound; // nicht eingerichtet: Port verrät nichts
            return;
        }

        var contact = Contact(ctx, options, DateTime.UtcNow);
        ctx.Items[ClientIpItem] = TrustedClientIp(ctx, options);
        if (isCheck)
        {
            access.NoteCheck(contact);
        }
        else
        {
            access.NoteAgentContact(contact);
        }

        var anonymous = AnonymousPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
                        || HttpMethods.IsGet(ctx.Request.Method) && path.StartsWithSegments("/agent/download", StringComparison.OrdinalIgnoreCase);
        if (!anonymous)
        {
            var (certificate, error) = await VerifyRequestAsync(ctx, ca, access, DateTimeOffset.UtcNow);
            if (certificate is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync(error);
                return;
            }

            if (CertificateAuthority.IsPeerCertificate(certificate))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; // Gegenstelle (Site-Sharing) ist kein Agent
                return;
            }

            ctx.Items[CertificateItem] = certificate;
        }

        if (IsBulkDownload(path))
        {
            await next();
            return;
        }

        await RunSignedAsync(ctx, next, ca);
    }

    /// <summary>
    /// Führt die Anfrage aus und signiert die Antwort mit dem Server-Zertifikat, gebunden an die Nonce der Anfrage
    /// (auch für den Föderationsweg, siehe <see cref="FederationGate"/>).
    /// </summary>
    public static async Task RunSignedAsync(HttpContext ctx, Func<Task> next, CertificateAuthority ca)
    {
        var nonce = ctx.Request.Headers[PublicChannel.NonceHeader].ToString();
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next();
        }
        finally
        {
            ctx.Response.Body = original;
        }

        var body = buffer.ToArray();
        var server = ca.GetServerCertificate();
        using (var key = server.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Server certificate has no ECDSA key."))
        {
            ctx.Response.Headers[PublicChannel.ServerCertHeader] = Convert.ToBase64String(server.RawData);
            ctx.Response.Headers[PublicChannel.ResponseSignatureHeader] = PublicChannel.Sign(key, PublicChannel.ResponseToSign(nonce, ctx.Response.StatusCode, body));
        }

        ctx.Response.ContentLength = body.Length;
        await original.WriteAsync(body, ctx.RequestAborted);
    }

    /// <summary>Signatur, Zertifikat (von dieser CA), Zeitstempel und einmalige Nonce prüfen.</summary>
    public static async Task<(X509Certificate2? Certificate, string Error)> VerifyRequestAsync(HttpContext ctx, CertificateAuthority ca, PublicAccess access, DateTimeOffset now)
    {
        var headers = ctx.Request.Headers;
        var nonce = headers[PublicChannel.NonceHeader].ToString();
        if (headers[PublicChannel.CertHeader].ToString() is not { Length: > 0 and < 8000 } certText
            || !long.TryParse(headers[PublicChannel.TimeHeader].ToString(), out var time)
            || nonce.Length is < 16 or > 64)
        {
            return (null, "Missing signature headers.");
        }

        if (!PublicChannel.IsFresh(time, now))
        {
            return (null, "Request too old or clock skew too large (check the time on the host).");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certText));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return (null, "Invalid certificate.");
        }

        if (!ca.IsIssuedByThisCa(certificate))
        {
            return (null, "Certificate not issued by this server.");
        }

        ctx.Request.EnableBuffering();
        using var body = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
        ctx.Request.Body.Position = 0;

        using var key = certificate.GetECDsaPublicKey();
        var data = PublicChannel.RequestToSign(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, time, nonce, body.ToArray());
        if (key is null || !PublicChannel.Verify(key, data, headers[PublicChannel.SignatureHeader].ToString()))
        {
            return (null, "Invalid signature.");
        }

        if (!access.TryUseNonce(CertificateAuthority.GetIdentityThumbprint(certificate) + ":" + nonce, now.UtcDateTime))
        {
            return (null, "Replayed request.");
        }

        return (certificate, "");
    }

    /// <summary>Für den Wizard: woher kam die Anfrage, über Cloudflare?</summary>
    public static ProxyContact Contact(HttpContext ctx, AIOPatcherOptions options, DateTime now)
    {
        var headers = ctx.Request.Headers;
        var forwarded = headers["CF-Connecting-IP"].ToString() is { Length: > 0 } cf ? cf
            : headers["X-Forwarded-For"].ToString().Split(',')[0].Trim() is { Length: > 0 } xff ? xff
            : headers["X-Real-IP"].ToString() is { Length: > 0 } real ? real : null;
        return new ProxyContact(now, Cut(forwarded ?? ctx.Connection.RemoteIpAddress?.ToString()), Cut(ctx.Connection.RemoteIpAddress?.ToString()),
            headers.ContainsKey("CF-Ray"), Cut(headers["X-Forwarded-Host"].ToString() is { Length: > 0 } h ? h : headers.Host.ToString()), Cut(ctx.Request.Path));

        static string? Cut(string? value) => value is null ? null : value.Length > 200 ? value[..200] : value;
    }

    /// <summary>
    /// IP des Agents für die Netzfreigabe der Site: Weitergereichte Adressen nur glauben, wenn der Proxy in
    /// TrustedProxies steht (sonst könnte jeder im LAN eine beliebige Adresse behaupten). Sonst: IP des Proxys.
    /// </summary>
    public static IPAddress? TrustedClientIp(HttpContext ctx, AIOPatcherOptions options)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !TrustedProxies.Parse(options.TrustedProxies).Any(n => n.Contains(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
        {
            return remote;
        }

        var headers = ctx.Request.Headers;
        var claimed = headers["CF-Connecting-IP"].ToString() is { Length: > 0 } cf ? cf : headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        return IPAddress.TryParse(claimed, out var ip) ? ip : remote;
    }
}
