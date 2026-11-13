using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Eigene kleine CA. Stellt das Server-Zertifikat (Dashboard + Agent-API) und später die Client-Zertifikate
/// der Agents aus. Agents pinnen das CA-Zertifikat bei der Registrierung.
/// </summary>
public sealed class CertificateAuthority
{
    private const string CaCertFile = "ca.crt";
    private const string CaKeyFile = "ca.key";
    private const string ServerCertFile = "server.crt";
    private const string ServerKeyFile = "server.key";

    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365 * 20);
    private static readonly TimeSpan ServerCertLifetime = TimeSpan.FromDays(397);
    private static readonly TimeSpan ServerCertRenewBefore = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly string _hostname;
    private readonly X509Certificate2 _caWithKey;
    private readonly Lock _serverCertLock = new();
    private X509Certificate2 _serverCertificate;

    private CertificateAuthority(string directory, string hostname, X509Certificate2 caWithKey, X509Certificate2 serverCertificate)
    {
        _directory = directory;
        _hostname = hostname;
        _caWithKey = caWithKey;
        _serverCertificate = serverCertificate;
        RootCertificate = X509CertificateLoader.LoadCertificate(caWithKey.RawData);
    }

    /// <summary>CA-Zertifikat ohne privaten Schlüssel.</summary>
    public X509Certificate2 RootCertificate { get; }

    public string RootCertificatePem => RootCertificate.ExportCertificatePem();

    /// <summary>SHA-256-Fingerprint der CA, wird im Installationsbefehl des Agents mitgegeben (Pinning).</summary>
    public string RootFingerprintSha256 => RootCertificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static CertificateAuthority LoadOrCreate(string directory, string hostname, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        var ca = LoadPair(directory, CaCertFile, CaKeyFile);
        if (ca is null)
        {
            logger.LogWarning("Keine CA gefunden, erzeuge neue AIOPatcher-CA in {Directory}. Bitte den CA-Schlüssel offline sichern!", directory);
            ca = CreateCa(directory);
        }

        var server = LoadPair(directory, ServerCertFile, ServerKeyFile);
        var authority = new CertificateAuthority(directory, hostname, ca, server ?? ca);
        if (server is null || authority.NeedsRenewal(server))
        {
            authority.RenewServerCertificate(logger);
        }

        logger.LogInformation("CA-Fingerprint (SHA-256): {Fingerprint}", authority.RootFingerprintSha256);
        return authority;
    }

    /// <summary>Aktuelles Server-Zertifikat. Erneuert sich selbst, wenn es bald abläuft (kein Neustart nötig).</summary>
    public X509Certificate2 GetServerCertificate()
    {
        var current = _serverCertificate;
        if (!NeedsRenewal(current))
        {
            return current;
        }

        lock (_serverCertLock)
        {
            if (NeedsRenewal(_serverCertificate))
            {
                RenewServerCertificate(logger: null);
            }

            return _serverCertificate;
        }
    }

    public static readonly TimeSpan ClientCertLifetime = TimeSpan.FromDays(730);
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Stellt aus einer CSR (PEM) ein Client-Zertifikat für einen Agent aus. Vom CSR wird nur der öffentliche
    /// Schlüssel übernommen, Subject und Erweiterungen legt die CA fest. Host- und Site-ID stehen als URI im SAN.
    /// </summary>
    public X509Certificate2 IssueClientCertificate(string csrPem, Guid hostId, Guid siteId)
    {
        // Prüft auch die Signatur der CSR (Nachweis, dass der Agent den privaten Schlüssel besitzt).
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        EnsureAcceptableKey(csr.PublicKey);

        var request = new CertificateRequest(new X500DistinguishedName($"CN={hostId}, OU=site-{siteId}, O=AIOPatcher Agent"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_caWithKey, true, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"urn:aiopatcher:host:{hostId}"));
        san.AddUri(new Uri($"urn:aiopatcher:site:{siteId}"));
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return request.Create(_caWithKey, now.AddMinutes(-5), now.Add(ClientCertLifetime), serial);
    }

    /// <summary>Fingerprint, über den Agents in der Datenbank identifiziert werden.</summary>
    public static string GetIdentityThumbprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static void EnsureAcceptableKey(PublicKey key)
    {
        using var ec = key.GetECDsaPublicKey();
        if (ec is not null)
        {
            if (ec.KeySize is 256 or 384)
            {
                return;
            }

            throw new CryptographicException("Nur ECDSA P-256/P-384 oder RSA ab 2048 Bit erlaubt.");
        }

        using var rsa = key.GetRSAPublicKey();
        if (rsa is not null && rsa.KeySize >= 2048)
        {
            return;
        }

        throw new CryptographicException("Nur ECDSA P-256/P-384 oder RSA ab 2048 Bit erlaubt.");
    }

    /// <summary>Prüft, ob ein Zertifikat von dieser CA ausgestellt wurde und gültig ist.</summary>
    public bool IsIssuedByThisCa(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(RootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Sperrung prüfen wir über die Datenbank
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (!chain.Build(certificate))
        {
            return false;
        }

        var root = chain.ChainElements[^1].Certificate;
        return root.RawDataMemory.Span.SequenceEqual(RootCertificate.RawDataMemory.Span);
    }

    private bool NeedsRenewal(X509Certificate2 cert)
    {
        if (ReferenceEquals(cert, _caWithKey))
        {
            return true;
        }

        var expiresSoon = cert.NotAfter.ToUniversalTime() - DateTime.UtcNow < ServerCertRenewBefore;
        var hostnameChanged = !string.Equals(cert.GetNameInfo(X509NameType.DnsName, false), _hostname, StringComparison.OrdinalIgnoreCase);
        return expiresSoon || hostnameChanged;
    }

    private void RenewServerCertificate(ILogger? logger)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={_hostname}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], // serverAuth
            false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_caWithKey, true, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(_hostname);
        if (!string.Equals(_hostname, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            san.AddDnsName("localhost");
        }

        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(_caWithKey, now.AddMinutes(-5), now.Add(ServerCertLifetime), serial);
        SavePair(_directory, ServerCertFile, ServerKeyFile, issued, key);
        using var withKey = issued.CopyWithPrivateKey(key);
        _serverCertificate = ToPersistable(withKey);
        logger?.LogInformation("Server-Zertifikat für {Hostname} ausgestellt, gültig bis {NotAfter:u}", _hostname, _serverCertificate.NotAfter.ToUniversalTime());
    }

    private static X509Certificate2 CreateCa(string directory)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=AIOPatcher Root CA, O=AIOPatcher", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var ca = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(CaLifetime));
        SavePair(directory, CaCertFile, CaKeyFile, ca, key);
        return ToPersistable(ca);
    }

    private static X509Certificate2? LoadPair(string directory, string certFile, string keyFile)
    {
        var certPath = Path.Combine(directory, certFile);
        var keyPath = Path.Combine(directory, keyFile);
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return null;
        }

        using var loaded = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return ToPersistable(loaded);
    }

    /// <summary>
    /// Speichert Zertifikat und Schlüssel als PEM. Der Schlüssel muss das Original-Objekt sein,
    /// denn aus einem PKCS#12-Import lässt er sich unter Windows nicht mehr exportieren.
    /// </summary>
    private static void SavePair(string directory, string certFile, string keyFile, X509Certificate2 cert, ECDsa key)
    {
        var keyPath = Path.Combine(directory, keyFile);

        WriteAtomically(keyPath, key.ExportPkcs8PrivateKeyPem());
        RestrictFile(keyPath);
        WriteAtomically(Path.Combine(directory, certFile), cert.ExportCertificatePem());
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content + "\n");
        RestrictFile(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// PEM-Schlüssel sind unter Windows "ephemeral" und funktionieren nicht mit SslStream.
    /// Über einen PKCS#12-Roundtrip bekommen wir ein überall nutzbares Zertifikat.
    /// </summary>
    private static X509Certificate2 ToPersistable(X509Certificate2 cert) =>
        X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), password: null);

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
