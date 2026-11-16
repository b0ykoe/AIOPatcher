using System.Security.Cryptography;
using AIOPatcher.Agent.Maintenance;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class SelfUpdateTests
{
    [Fact]
    public void Signature_FromReleaseKey_IsAccepted_OthersRejected()
    {
        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(release.ExportSubjectPublicKeyInfo());
        var hash = SHA256.HashData("agent binary"u8.ToArray());

        var good = Convert.ToBase64String(release.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence));
        var forged = Convert.ToBase64String(attacker.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence));
        var otherFile = SHA256.HashData("other binary"u8.ToArray());

        Assert.True(SelfMaintenance.VerifySignature(publicKey, hash, good));
        Assert.False(SelfMaintenance.VerifySignature(publicKey, hash, forged));
        Assert.False(SelfMaintenance.VerifySignature(publicKey, otherFile, good));
        Assert.False(SelfMaintenance.VerifySignature(publicKey, hash, "not-base64!"));
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("0.2.0", "0.1.0+abc123", true)]
    [InlineData("0.2.0", null, true)]
    public void IsNewer_ComparesVersions(string available, string? installed, bool expected) =>
        Assert.Equal(expected, AgentDownloads.IsNewer(available, installed));

    [Fact]
    public void RuntimeId_ForOsAndArchitecture()
    {
        Assert.Equal("linux-arm64", AgentDownloads.RuntimeId(OsFamily.Linux, "arm64"));
        Assert.Equal("win-x64", AgentDownloads.RuntimeId(OsFamily.Windows, "x64"));
        Assert.Null(AgentDownloads.RuntimeId(OsFamily.Windows, "arm64"));
        Assert.Null(AgentDownloads.RuntimeId(OsFamily.Linux, null)); // alter Agent ohne Angabe
    }

    [Fact]
    public void ApiToken_FormatAndHash()
    {
        var token = ApiTokenAuthenticationHandler.NewToken();
        Assert.StartsWith("aiop_", token);
        Assert.Equal(53, token.Length);
        Assert.Equal(ApiTokenAuthenticationHandler.Hash(token), ApiTokenAuthenticationHandler.Hash(" " + token + " "));
        Assert.NotEqual(ApiTokenAuthenticationHandler.Hash(token), ApiTokenAuthenticationHandler.Hash(ApiTokenAuthenticationHandler.NewToken()));
    }
}

public sealed class ReportTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public ReportTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Fact]
    public async Task Report_FlagsOverdueSecurityAndKev_AsNotCompliant()
    {
        var now = DateTime.UtcNow;
        var site = new Site { Name = "Kunde" };
        ManagedHost Host(string name) => new() { Site = site, Name = name, Hostname = name, CertificateThumbprint = name, LastSeenAt = now };
        var clean = Host("clean");
        var overdue = Host("overdue");
        var kev = Host("kev");
        var fresh = Host("=fresh"); // Formel-Injektion in CSV abfangen
        _db.Hosts.AddRange(clean, overdue, kev, fresh);
        _db.PendingUpdates.Add(new PendingUpdate { Host = overdue, Name = "openssl", IsSecurity = true, FirstSeenAt = now.AddDays(-20) });
        _db.PendingUpdates.Add(new PendingUpdate { Host = kev, Name = "xz", IsSecurity = true, IsKev = true, FirstSeenAt = now.AddDays(-1) });
        _db.PendingUpdates.Add(new PendingUpdate { Host = fresh, Name = "curl", IsSecurity = true, FirstSeenAt = now.AddDays(-2) });
        _db.Jobs.Add(new PatchJob { Host = clean, Type = JobType.Patch, Status = JobStatus.Succeeded, RequestedBy = "scheduler", FinishedAt = now.AddDays(-3) });
        await _db.SaveChangesAsync();

        var report = await ReportEndpoints.BuildAsync(_db, new AIOPatcherOptions(), null, 30, 14, CancellationToken.None);

        Assert.Equal(4, report.Summary.Hosts);
        Assert.Equal(2, report.Summary.Compliant);
        Assert.Equal(50, report.Summary.CompliancePercent);
        Assert.False(report.Hosts.Single(h => h.Host == "overdue").Compliant);
        Assert.False(report.Hosts.Single(h => h.Host == "kev").Compliant);
        Assert.True(report.Hosts.Single(h => h.Host == "=fresh").Compliant);
        Assert.Equal(1, report.Hosts.Single(h => h.Host == "clean").JobsSucceeded);

        var csv = ReportEndpoints.ToCsv(report);
        Assert.Contains("\"'=fresh\"", csv);
        Assert.DoesNotContain(",=fresh,", csv);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
