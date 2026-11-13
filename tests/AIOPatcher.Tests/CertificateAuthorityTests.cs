using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Server.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ServerCertificate_IsIssuedByCa_AndCoversHostname()
    {
        var ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);
        var server = ca.GetServerCertificate();

        Assert.True(server.HasPrivateKey);
        Assert.True(ca.IsIssuedByThisCa(server));
        Assert.True(server.MatchesHostname("patch.example.com"));
        Assert.True(server.MatchesHostname("localhost"));
    }

    [Fact]
    public void Reload_KeepsSameCa()
    {
        var first = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);
        var second = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);

        Assert.Equal(first.RootFingerprintSha256, second.RootFingerprintSha256);
        Assert.Equal(first.GetServerCertificate().Thumbprint, second.GetServerCertificate().Thumbprint);
    }

    [Fact]
    public void HostnameChange_ReissuesServerCertificate_WithSameCa()
    {
        var first = CertificateAuthority.LoadOrCreate(_dir, "old.example.com", NullLogger.Instance);
        var second = CertificateAuthority.LoadOrCreate(_dir, "new.example.com", NullLogger.Instance);

        Assert.Equal(first.RootFingerprintSha256, second.RootFingerprintSha256);
        Assert.True(second.GetServerCertificate().MatchesHostname("new.example.com"));
        Assert.False(second.GetServerCertificate().MatchesHostname("old.example.com"));
    }

    [Fact]
    public void ForeignCertificate_IsRejected()
    {
        var ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=evil", key, HashAlgorithmName.SHA256);
        using var foreign = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(ca.IsIssuedByThisCa(foreign));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist best effort.
        }
    }
}
