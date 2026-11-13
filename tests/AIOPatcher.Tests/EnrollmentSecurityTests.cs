using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

public sealed class EnrollmentSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IssueClientCertificate_UsesCsrKey_AndSetsIdentity()
    {
        var ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=evil-admin", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var hostId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        using var cert = ca.IssueClientCertificate(csr, hostId, siteId);

        Assert.True(ca.IsIssuedByThisCa(cert));
        Assert.Contains(hostId.ToString(), cert.Subject); // Subject aus der CSR wird ignoriert
        Assert.DoesNotContain("evil-admin", cert.Subject);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), cert.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public void IssueClientCertificate_RejectsTamperedCsr()
    {
        var ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=host", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        csr[^5] ^= 0xFF; // Signatur kaputt machen
        var pem = PemEncoding.WriteString("CERTIFICATE REQUEST", csr);

        Assert.ThrowsAny<CryptographicException>(() => ca.IssueClientCertificate(pem, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void IssueClientCertificate_RejectsWeakRsaKey()
    {
        var ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);
        using var rsa = RSA.Create(1024);
        var csr = new CertificateRequest("CN=host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();

        Assert.ThrowsAny<CryptographicException>(() => ca.IssueClientCertificate(csr, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Theory]
    [InlineData(null, "198.51.100.7", true)]
    [InlineData("", "198.51.100.7", true)]
    [InlineData("203.0.113.10/32,198.51.100.0/24", "198.51.100.7", true)]
    [InlineData("203.0.113.10/32,198.51.100.0/24", "::ffff:198.51.100.7", true)]
    [InlineData("203.0.113.10/32", "203.0.113.11", false)]
    [InlineData("2a01:4f8::/32", "2a01:4f8:1:2::5", true)]
    public void AgentNetworkPolicy_RespectsAllowlist(string? cidrs, string remote, bool expected)
    {
        Assert.Equal(expected, AgentNetworkPolicy.IsAllowed(cidrs, IPAddress.Parse(remote)));
    }

    [Fact]
    public void AgentNetworkPolicy_DeniesUnknownRemote_WhenAllowlistSet()
    {
        Assert.False(AgentNetworkPolicy.IsAllowed("10.0.0.0/8", null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
