using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class CveLookupTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public CveLookupTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Theory]
    [InlineData("CVE-2024-3094", "CVE-2024-3094")]
    [InlineData(" cve-2021-44228 ", "CVE-2021-44228")]
    [InlineData("2024-3094", "CVE-2024-3094")]
    [InlineData("CVE-2024-1234567", "CVE-2024-1234567")]
    [InlineData("CVE-24-3094", null)]
    [InlineData("openssl", null)]
    [InlineData("CVE-2024-3094/../x", null)]
    public void NormalizeId(string input, string? expected) => Assert.Equal(expected, CveLookupService.NormalizeId(input));

    [Fact]
    public void ParseNvd_PicksEnglishDescriptionScoresAndCwe()
    {
        var json = JsonNode.Parse("""
        {"totalResults":1,"vulnerabilities":[{"cve":{
          "id":"CVE-2024-3094","published":"2024-03-29T17:15:21.150","lastModified":"2025-02-01T10:00:00.000","vulnStatus":"Modified",
          "descriptions":[{"lang":"es","value":"Código malicioso"},{"lang":"en","value":"Malicious code was discovered in xz."}],
          "metrics":{"cvssMetricV31":[
            {"source":"secondary@example","type":"Secondary","cvssData":{"baseScore":9.0,"baseSeverity":"CRITICAL","vectorString":"CVSS:3.1/X"}},
            {"source":"nvd@nist.gov","type":"Primary","cvssData":{"baseScore":10.0,"baseSeverity":"CRITICAL","vectorString":"CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H"}}]},
          "weaknesses":[{"description":[{"lang":"en","value":"CWE-506"},{"lang":"en","value":"NVD-CWE-Other"}]}],
          "references":[{"url":"https://www.openwall.com/lists/oss-security/2024/03/29/4"},{"url":"http://insecure.example"}]}}]}
        """)!;

        var record = Assert.Single(CveLookupService.ParseNvd(json));

        Assert.Equal("Malicious code was discovered in xz.", record.Description);
        var score = Assert.Single(record.Scores);
        Assert.Equal(10.0, score.BaseScore); // Primary vor Secondary
        Assert.Equal("3.1", score.Version);
        Assert.Equal(["CWE-506"], record.Weaknesses);
        Assert.Equal(["https://www.openwall.com/lists/oss-security/2024/03/29/4"], record.References); // nur https
        Assert.Equal(new DateTime(2024, 3, 29, 17, 15, 21, 150, DateTimeKind.Utc), record.Published);
    }

    [Fact]
    public void ParseOsv_FixPerEcosystem_SkipsGitRanges()
    {
        var json = JsonNode.Parse("""
        {"id":"DEBIAN-CVE-2024-3094","summary":"xz backdoor","upstream":["CVE-2024-3094"],
         "affected":[
          {"package":{"ecosystem":"Debian:12","name":"xz-utils"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"5.4.1-1"}]}]},
          {"package":{"ecosystem":"Debian:13","name":"xz-utils"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"}]}]},
          {"ranges":[{"type":"GIT","repo":"https://github.com/tukaani-project/xz","events":[{"introduced":"abc"}]}]}]}
        """)!;

        var osv = CveLookupService.ParseOsv(json);

        Assert.Equal("xz backdoor", osv.Summary);
        Assert.Contains("CVE-2024-3094", osv.Aliases);
        Assert.Equal(2, osv.Fixes.Count);
        Assert.Equal("5.4.1-1", osv.Fixes.Single(f => f.System == "Debian:12").FixedIn);
        Assert.Null(osv.Fixes.Single(f => f.System == "Debian:13").FixedIn); // noch offen
    }

    [Fact]
    public void ParseMsrcAffected_KbPerProduct()
    {
        var json = JsonNode.Parse("""
        {"value":[
          {"product":"Windows Server 2022","kbArticles":[{"articleName":"5050001","fixedBuildNumber":"10.0.20348.3091"}]},
          {"product":"Windows Server 2022","kbArticles":[{"articleName":"5050001","fixedBuildNumber":"10.0.20348.3091"}]},
          {"product":"Windows 11 Version 24H2 for x64-based Systems","kbArticles":[{"articleName":"5050009","fixedBuildNumber":""}]},
          {"product":"Something","kbArticles":[]}]}
        """)!;

        var fixes = CveLookupService.ParseMsrcAffected(json);

        Assert.Equal(2, fixes.Count);
        var server = fixes.Single(f => f.System == "Windows Server 2022");
        Assert.Equal("KB5050001", server.Package);
        Assert.Equal("10.0.20348.3091", server.FixedIn);
        Assert.Null(fixes.Single(f => f.System.StartsWith("Windows 11")).FixedIn);
    }

    [Fact]
    public async Task AffectedHosts_ExactCveMatch_FixedAndUnfixed()
    {
        var site = new Site { Name = "Homelab" };
        var web = new ManagedHost { Site = site, Name = "web01", Hostname = "web01", CertificateThumbprint = "a" };
        var db = new ManagedHost { Site = site, Name = "db01", Hostname = "db01", CertificateThumbprint = "b" };
        var gone = new ManagedHost { Site = site, Name = "old01", Hostname = "old01", CertificateThumbprint = "c", IsRevoked = true };
        _db.Hosts.AddRange(web, db, gone);
        _db.PendingUpdates.Add(new PendingUpdate { Host = web, Name = "xz-utils", Cves = "CVE-2024-30940,CVE-2024-3094" });
        _db.PendingUpdates.Add(new PendingUpdate { Host = db, Name = "other", Cves = "CVE-2024-30941" }); // nur Präfix gleich
        _db.PendingUpdates.Add(new PendingUpdate { Host = gone, Name = "xz-utils", Cves = "CVE-2024-3094" });
        _db.HostVulnerabilities.Add(new HostVulnerability { Host = db, CveId = "CVE-2024-3094", Package = "xz-utils", InstalledVersion = "5.6.0-1", FoundAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var hosts = await CveLookupService.AffectedHostsAsync("CVE-2024-3094", _db, CancellationToken.None);

        Assert.Equal(["db01", "web01"], hosts.Select(h => h.HostName));
        Assert.Equal("xz-utils 5.6.0-1", hosts[0].Package);
        Assert.Equal("xz-utils", hosts[1].Fix);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
