using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Commands;

/// <summary>
/// Einmalige Registrierung:
/// 1. CA vom Server holen und gegen den Fingerprint aus dem Installationsbefehl prüfen (Pinning).
/// 2. Erst dann über die gepinnte Verbindung Token + CSR schicken.
/// 3. Zertifikat, Schlüssel und CA lokal ablegen.
/// Mit --public-url: Ist die interne Adresse nicht erreichbar (Host außerhalb), läuft alles über den Reverse Proxy.
/// Die CA wird dort ebenfalls am Fingerprint erkannt, die Antwort auf die Registrierung ist vom Server signiert.
/// </summary>
public static class EnrollCommand
{
    public static async Task<int> RunAsync(CommandLine args, CancellationToken ct)
    {
        var server = args.Get("server");
        var token = args.Get("token");
        var fingerprint = args.Get("fingerprint");
        var publicUrl = args.Get("public-url");
        var stateDir = args.Get("state-dir") ?? AgentState.DefaultDirectory;

        if (server is null || token is null || fingerprint is null)
        {
            await Console.Error.WriteLineAsync("Usage: aiopatcher-agent enroll --server https://<host>:8443 --token <token> --fingerprint <sha256> [--public-url https://<name>] [--state-dir <path>] [--force]");
            return 2;
        }

        if (AgentState.Exists(stateDir) && !args.Has("force"))
        {
            await Console.Error.WriteLineAsync($"Agent is already enrolled ({stateDir}). Use --force to re-enroll.");
            return 1;
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            await Console.Error.WriteLineAsync("--server must be an https:// URL.");
            return 2;
        }

        Uri? publicUri = null;
        if (publicUrl is not null && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out publicUri) || publicUri.Scheme != Uri.UriSchemeHttps))
        {
            await Console.Error.WriteLineAsync("--public-url must be an https:// URL.");
            return 2;
        }

        // 1. CA holen und pinnen
        X509Certificate2 ca;
        try
        {
            try
            {
                ca = await FetchAndVerifyCaAsync(serverUri, CertificateHelper.NormalizeFingerprint(fingerprint), ct);
            }
            catch (Exception ex) when (publicUri is not null && ServerClient.IsUnreachable(ex))
            {
                Console.WriteLine($"{serverUri} not reachable, using the public address {publicUri}.");
                ca = await FetchCaViaPublicAsync(publicUri, CertificateHelper.NormalizeFingerprint(fingerprint), ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or CryptographicException or InvalidDataException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"CA verification failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"CA verified: {CertificateHelper.Sha256Fingerprint(ca)}");

        // 2. Schlüssel + CSR, Enrollment über die gepinnte Verbindung
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostname = Dns.GetHostName();
        var csr = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

        EnrollResponse response;
        using (var client = new ServerClient(serverUri, ca, clientCertificate: null, publicUri))
        {
            try
            {
                response = await client.EnrollAsync(
                    new EnrollRequest(token.Trim(), csr, hostname, PlatformInfo.OsFamily, AgentState.AgentVersion), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or ServerRejectedException)
            {
                await Console.Error.WriteLineAsync($"Enrollment failed: {ex.Message}");
                return 1;
            }
        }

        // 3. Antwort prüfen und speichern
        using var issued = X509Certificate2.CreateFromPem(response.ClientCertificatePem);
        using var issuedKey = issued.GetECDsaPublicKey();
        if (issuedKey is null || !issuedKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
        {
            await Console.Error.WriteLineAsync("Server issued a certificate for a different key. Aborting.");
            return 1;
        }

        using var returnedCa = X509Certificate2.CreateFromPem(response.CaCertificatePem);
        if (!returnedCa.RawDataMemory.Span.SequenceEqual(ca.RawDataMemory.Span))
        {
            await Console.Error.WriteLineAsync("Server returned a different CA than the pinned one. Aborting.");
            return 1;
        }

        AgentState.Save(stateDir, new AgentConfig(serverUri.GetLeftPart(UriPartial.Authority), response.HostId, response.SiteId, publicUri?.GetLeftPart(UriPartial.Authority)),
            ca.ExportCertificatePem(), response.ClientCertificatePem, key);

        AgentPolicy.WriteDefaultIfMissing(stateDir);
        AIOPatcher.Agent.Maintenance.UpdateState.Reset(stateDir); // evtl. anderer Server: Update-Schlüssel neu lernen
        Console.WriteLine($"Enrolled. Host ID {response.HostId}, data in {stateDir}");
        Console.WriteLine($"Local policy: {Path.Combine(stateDir, AgentPolicy.FileName)} (restrict patching and reboots here).");
        return 0;
    }

    private static async Task<X509Certificate2> FetchAndVerifyCaAsync(Uri server, string expectedFingerprint, CancellationToken ct)
    {
        X509Certificate2? presentedLeaf = null;
        var nameMismatch = false;

        // Hier kennen wir die CA noch nicht: Server-Zertifikat merken, Prüfung danach gegen die gepinnte CA.
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
        {
            if (cert is not null)
            {
                presentedLeaf = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            }

            nameMismatch = errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);
            return true;
        };

        using var http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        var pem = await http.GetStringAsync("/agent/ca", ct);
        var ca = X509Certificate2.CreateFromPem(pem);

        var actual = CertificateHelper.Sha256Fingerprint(ca);
        if (!string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new CryptographicException($"Fingerprint mismatch. Expected {expectedFingerprint}, server returned {actual}.");
        }

        if (nameMismatch)
        {
            throw new CryptographicException($"Server certificate does not match the hostname '{server.Host}'.");
        }

        if (presentedLeaf is null || !CertificateHelper.ValidateServer(ca, presentedLeaf, System.Net.Security.SslPolicyErrors.None))
        {
            throw new CryptographicException("Server certificate was not issued by the pinned CA.");
        }

        return ca;
    }

    /// <summary>
    /// CA über den Reverse Proxy holen: TLS gehört dort dem Proxy (Let's Encrypt, Cloudflare), darum zählt allein der
    /// Fingerprint der gelieferten CA. Danach prüft der Agent jede Antwort gegen diese CA (Antwort-Signatur).
    /// </summary>
    private static async Task<X509Certificate2> FetchCaViaPublicAsync(Uri publicUrl, string expectedFingerprint, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = publicUrl, Timeout = TimeSpan.FromSeconds(30) };
        var pem = await http.GetStringAsync("/agent/ca", ct);
        var ca = X509Certificate2.CreateFromPem(pem);
        var actual = CertificateHelper.Sha256Fingerprint(ca);
        if (!string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new CryptographicException($"Fingerprint mismatch via {publicUrl}. Expected {expectedFingerprint}, got {actual}.");
        }

        return ca;
    }
}
