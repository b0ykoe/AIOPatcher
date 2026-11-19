using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.DataProtection;

namespace AIOPatcher.Server.Services.Federation;

/// <summary>Fehler einer Gegenstelle (Status 0 = nicht erreichbar oder Antwort nicht vertrauenswürdig).</summary>
public sealed class FederationException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Zugang zu einer Instanz A: Adresse, gepinnte CA und (nach der Annahme) unser Zertifikat mit Schlüssel.</summary>
public sealed record FederationTarget(string BaseUrl, string CaFingerprint, string? CaCertificatePem, string? CertificatePem, string? ProtectedKey);

/// <summary>
/// Site-Sharing, Seite B: ruft A über dessen externe Dashboard-Adresse auf. Jede Anfrage wird mit unserem Zertifikat
/// signiert, jede Antwort muss A mit seinem Server-Zertifikat signiert haben (CA aus dem Einladungslink gepinnt).
/// Darum schützt das Verfahren auch dann, wenn ein Proxy (Cloudflare) TLS beendet.
/// </summary>
public sealed class FederationClient(IDataProtectionProvider dataProtection)
{
    private const string KeyPurpose = "AIOPatcher.Federation.Key";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    // --- Schlüssel --------------------------------------------------------------------------------------

    public sealed record NewKey(string CsrPem, string ProtectedKey);

    /// <summary>Neuer Schlüssel (P-256) und CSR; der Schlüssel bleibt verschlüsselt bei uns.</summary>
    public NewKey CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=aiopatcher-peer", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var protectedKey = dataProtection.CreateProtector(KeyPurpose).Protect(Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        return new NewKey(csr, protectedKey);
    }

    private ECDsa LoadKey(string protectedKey)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(dataProtection.CreateProtector(KeyPurpose).Unprotect(protectedKey)), out _);
        return key;
    }

    // --- Aufrufe ----------------------------------------------------------------------------------------

    /// <summary>
    /// Erster Kontakt: Info holen und prüfen, dass A die gepinnte CA hat (Fingerabdruck aus dem Link) und die Antwort
    /// damit signiert ist. TLS wird hier noch nicht geprüft, die Signatur ersetzt das (die Info ist öffentlich).
    /// </summary>
    public async Task<FederationInfo> GetInfoAsync(string baseUrl, string caFingerprint, CancellationToken ct)
    {
        var target = new FederationTarget(baseUrl, caFingerprint, null, null, null);
        var (status, body, serverCert) = await SendRawAsync(target, HttpMethod.Get, FederationProtocol.InfoPath, null, sign: false, ct);
        if (status != 200)
        {
            throw new FederationException(status, ErrorText(body) ?? L.T($"Gegenstelle antwortet mit {status}.", $"The other instance answered {status}."));
        }

        var info = Deserialize<FederationInfo>(body);
        X509Certificate2 ca;
        try
        {
            ca = X509Certificate2.CreateFromPem(info.CaCertificatePem);
        }
        catch (CryptographicException)
        {
            throw new FederationException(0, "Invalid CA certificate.");
        }

        using (ca)
        {
            if (!string.Equals(Fingerprint(ca), caFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new FederationException(0, L.T("Der CA-Fingerabdruck passt nicht zum Link. Falsche Adresse oder manipulierte Verbindung.",
                    "The CA fingerprint does not match the link. Wrong address or tampered connection."));
            }

            if (serverCert is null || !IssuedBy(serverCert, ca))
            {
                throw new FederationException(0, L.T("Antwort nicht von dieser Instanz signiert.", "Response not signed by this instance."));
            }
        }

        return info;
    }

    /// <summary>Signierte (oder bei join ggf. unsignierte) Anfrage; Antwort wird gegen die gepinnte CA geprüft.</summary>
    public async Task<T> SendAsync<T>(FederationTarget target, HttpMethod method, string path, object? body, CancellationToken ct, bool sign = true)
    {
        var (status, response, _) = await SendRawAsync(target, method, path, body, sign, ct);
        if (status is < 200 or > 299)
        {
            throw new FederationException(status, ErrorText(response) ?? L.T($"Gegenstelle antwortet mit {status}.", $"The other instance answered {status}."));
        }

        return Deserialize<T>(response);
    }

    private async Task<(int Status, byte[] Body, X509Certificate2? ServerCert)> SendRawAsync(FederationTarget target, HttpMethod method, string path, object? body, bool sign, CancellationToken ct)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, FederationProtocol.Json);
        using var request = new HttpRequestMessage(method, target.BaseUrl.TrimEnd('/') + path);
        var nonce = PublicChannel.NewNonce();
        request.Headers.Add(PublicChannel.NonceHeader, nonce);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        if (sign)
        {
            if (target.CertificatePem is null || target.ProtectedKey is null)
            {
                throw new InvalidOperationException("No certificate for a signed request.");
            }

            using var key = LoadKey(target.ProtectedKey);
            using var certificate = X509Certificate2.CreateFromPem(target.CertificatePem);
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.Add(PublicChannel.CertHeader, Convert.ToBase64String(certificate.RawData));
            request.Headers.Add(PublicChannel.TimeHeader, time.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(PublicChannel.SignatureHeader,
                PublicChannel.Sign(key, PublicChannel.RequestToSign(method.Method, request.RequestUri!.PathAndQuery, time, nonce, bytes)));
        }

        HttpResponseMessage response;
        byte[] responseBody;
        try
        {
            response = await Client(target).SendAsync(request, ct);
            responseBody = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new FederationException(0, L.T($"Nicht erreichbar: {ex.Message}", $"Not reachable: {ex.Message}"));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FederationException(0, L.T("Zeitüberschreitung.", "Timed out."));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var serverCert = Header(response, PublicChannel.ServerCertHeader) is { } certText ? TryLoad(certText) : null;
            var signature = Header(response, PublicChannel.ResponseSignatureHeader);
            if (serverCert is null || signature is null)
            {
                // Typisch: Adresse zeigt nicht auf AIOPatcher, Funktion bei A aus (404 ohne Signatur) oder ein Proxy-Fehler.
                throw new FederationException(status == 200 ? 0 : status, status is 404
                    ? L.T("Unter dieser Adresse ist kein Teilen möglich (bei der anderen Instanz aus oder falsche Adresse).",
                        "Sharing is not available at this address (off on the other instance, or wrong address).")
                    : L.T($"Unsignierte Antwort ({status}).", $"Unsigned response ({status})."));
            }

            using var publicKey = serverCert.GetECDsaPublicKey();
            if (publicKey is null || !PublicChannel.Verify(publicKey, PublicChannel.ResponseToSign(nonce, status, responseBody), signature))
            {
                throw new FederationException(0, L.T("Signatur der Antwort ungültig.", "Invalid response signature."));
            }

            if (target.CaCertificatePem is not null)
            {
                using var ca = X509Certificate2.CreateFromPem(target.CaCertificatePem);
                if (!IssuedBy(serverCert, ca))
                {
                    throw new FederationException(0, L.T("Antwort nicht von der erwarteten Instanz signiert.", "Response not signed by the expected instance."));
                }
            }

            return (status, responseBody, serverCert);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static X509Certificate2? TryLoad(string base64)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Wurde das Zertifikat von genau dieser CA ausgestellt (und ist gültig)?</summary>
    public static bool IssuedBy(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(certificate))
        {
            return false;
        }

        return chain.ChainElements[^1].Certificate.RawDataMemory.Span.SequenceEqual(ca.RawDataMemory.Span);
    }

    private static T Deserialize<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, FederationProtocol.Json) ?? throw new FederationException(0, "Empty response.");
        }
        catch (JsonException)
        {
            throw new FederationException(0, L.T("Antwort nicht lesbar (andere Version?).", "Response not readable (different version?)."));
        }
    }

    private static string? ErrorText(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// HTTP-Client je Ziel. TLS: öffentlich gültiges Zertifikat (Proxy, Cloudflare) oder eines der gepinnten CA von A.
    /// Ohne bekannte CA (erster Kontakt, nur /info) wird TLS nicht geprüft; dort zählt allein die Signatur.
    /// </summary>
    private HttpClient Client(FederationTarget target)
    {
        var key = $"{target.BaseUrl}|{target.CaFingerprint}|{(target.CaCertificatePem is null ? "info" : "pinned")}";
        return _clients.GetOrAdd(key, _ =>
        {
            var ca = target.CaCertificatePem is null ? null : X509Certificate2.CreateFromPem(target.CaCertificatePem);
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                        errors == SslPolicyErrors.None
                        || ca is null
                        || certificate is not null && IssuedBy(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), ca),
                },
            };
            return new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = FederationProtocol.MaxResponseBytes };
        });
    }
}
