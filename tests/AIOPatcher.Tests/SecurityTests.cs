using System.Text.Json.Nodes;
using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class CveMappingTests
{
    [Theory]
    [InlineData("Debian-Security:12/stable-security", "Debian:12")]
    [InlineData("Debian:12.8/stable", "Debian:12")]
    [InlineData("Ubuntu:24.04/noble-updates, Ubuntu:24.04/noble-security", "Ubuntu:24.04:LTS")]
    [InlineData("Ubuntu:24.10/oracular-security", "Ubuntu:24.10")]
    [InlineData("Proxmox:bookworm", null)]
    [InlineData(null, null)]
    public void OsvEcosystem_FromAptOrigin(string? origin, string? expected) => Assert.Equal(expected, CveMapping.OsvEcosystem(origin));

    [Fact]
    public void LookupKey_UsesSourcePackageAndBothVersions()
    {
        var update = new PendingUpdate { Name = "libssl3", SourcePackage = "openssl", CurrentVersion = "3.0.13-1", AvailableVersion = "3.0.15-1~deb12u1", Classification = "Debian-Security:12/stable-security" };
        Assert.Equal("osv|Debian:12|openssl|3.0.13-1|3.0.15-1~deb12u1", CveMapping.LookupKey(update, OsFamily.Linux));

        var newPackage = new PendingUpdate { Name = "linux-image-6.8.0-50", CurrentVersion = null, AvailableVersion = "6.8.0-50.51", Classification = "Ubuntu:24.04/noble-security" };
        Assert.Null(CveMapping.LookupKey(newPackage, OsFamily.Linux));

        var kb = new PendingUpdate { Name = "2026-11 Cumulative Update (KB5050001)", AvailableVersion = "KB5050001" };
        Assert.Equal("msrc|KB5050001", CveMapping.LookupKey(kb, OsFamily.Windows));
    }

    [Fact]
    public void ExtractCves_FromOsvIds()
    {
        Assert.Equal(["CVE-2024-3094"], CveMapping.ExtractCves("UBUNTU-CVE-2024-3094"));
        Assert.Empty(CveMapping.ExtractCves("USN-7000-1"));
    }

    [Fact]
    public void ParseMsrcDocument_MapsKbToCves()
    {
        var doc = JsonNode.Parse("""
        {
          "Vulnerability": [
            { "CVE": "CVE-2026-1001", "Remediations": [ { "Description": { "Value": "5050001" }, "Type": 2 }, { "Description": { "Value": "Release Notes" }, "Type": 2 } ] },
            { "CVE": "CVE-2026-1002", "Remediations": [ { "Description": { "Value": "5050001" } }, { "Description": { "Value": "5050002" } } ] },
            { "CVE": "not-a-cve", "Remediations": [ { "Description": { "Value": "5050003" } } ] }
          ]
        }
        """);

        var map = CveMapping.ParseMsrcDocument(doc);

        Assert.Equal(["CVE-2026-1001", "CVE-2026-1002"], map["5050001"].Order());
        Assert.Equal(["CVE-2026-1002"], map["5050002"]);
        Assert.False(map.ContainsKey("5050003"));
    }
}

public sealed class ContainerTests
{
    [Theory]
    [InlineData("nginx", "docker.io/library/nginx:latest")]
    [InlineData("nginx:1.27", "docker.io/library/nginx:1.27")]
    [InlineData("grafana/grafana:11.3.0", "docker.io/grafana/grafana:11.3.0")]
    [InlineData("ghcr.io/home-assistant/home-assistant:stable", "ghcr.io/home-assistant/home-assistant:stable")]
    [InlineData("registry.example.com:5000/team/app:1.0", "registry.example.com:5000/team/app:1.0")]
    [InlineData("app@sha256:0123", null)]
    [InlineData("", null)]
    public void ImageReference_Normalizes(string image, string? expected) => Assert.Equal(expected, ImageReference.Parse(image)?.Normalized);

    [Fact]
    public void ImageReference_PicksMatchingRepoDigest()
    {
        var reference = ImageReference.Parse("nginx:1.27")!;
        Assert.Equal("registry-1.docker.io", reference.ApiHost);
        Assert.Equal("sha256:aaa", reference.LocalDigest(["other/image@sha256:bbb", "nginx@sha256:aaa"]));
        Assert.Null(reference.LocalDigest([]));
    }

    [Fact]
    public void ImageReference_KeepsAllDigestsOfTheRepository_CurrentIfAnyMatches()
    {
        // Dasselbe Image über zwei Tags gezogen (z. B. mssql 2022-latest und 2022-CU16): je Tag ein Digest
        var reference = ImageReference.Parse("mcr.microsoft.com/mssql/server:2022-latest")!;
        var digests = reference.LocalDigest(["mcr.microsoft.com/mssql/server@sha256:cu16", "mcr.microsoft.com/mssql/server@sha256:latest", "other@sha256:x"]);
        Assert.Equal("sha256:cu16 sha256:latest", digests);

        var container = new ContainerInfo { HostId = Guid.NewGuid(), Name = "sql", Image = "mcr.microsoft.com/mssql/server:2022-latest", LocalDigest = digests };
        Assert.True(container.HasDigest("sha256:latest"));
        Assert.False(container.HasDigest("sha256:newer"));
    }

    [Fact]
    public void DockerInventory_ParsesInspectOutput()
    {
        var containers = DockerInventory.ParseContainers("/web|nginx:1.27|sha256:img1|running\n/db|postgres:16|sha256:img2|running\nbroken line\n");
        Assert.Equal(["web", "db"], containers.Select(c => c.Name));
        Assert.Equal("nginx:1.27", containers[0].Image);

        var digests = DockerInventory.ParseImageDigests("sha256:img1|[\"nginx@sha256:aaa\"]\nsha256:img2|null\n");
        Assert.Equal(["nginx@sha256:aaa"], digests["sha256:img1"]);
        Assert.Empty(digests["sha256:img2"]);
    }

    [Fact]
    public void Apt_ParseSourceMap()
    {
        var map = AptOutputParser.ParseSourceMap("libssl3\topenssl\ncurl\tcurl\n\nbroken\n");
        Assert.Equal("openssl", map["libssl3"]);
        Assert.Equal(2, map.Count);
    }
}

public sealed class KevNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public KevNotificationTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Fact]
    public async Task ApplyScores_SetsKevAndEpss_AndNotifiesOncePerCve()
    {
        var host = new ManagedHost { Site = new Site { Name = "Homelab" }, Name = "web01", Hostname = "web01", CertificateThumbprint = "a" };
        _db.Hosts.Add(host);
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "xz-utils", Cves = "CVE-2024-3094,CVE-2024-0001", CveCheckedAt = DateTime.UtcNow });
        _db.CveInfos.Add(new CveInfo { Id = "CVE-2024-3094", IsKev = true, KevName = "XZ Utils backdoor", Epss = 0.85 });
        _db.CveInfos.Add(new CveInfo { Id = "CVE-2024-0001", Epss = 0.02 });
        await _db.SaveChangesAsync();
        var notifier = new Notifier();

        await CveService.ApplyScoresAsync(_db, notifier, CancellationToken.None);
        await CveService.ApplyScoresAsync(_db, notifier, CancellationToken.None);

        var update = await _db.PendingUpdates.SingleAsync();
        Assert.True(update.IsKev);
        Assert.Equal(0.85, update.MaxEpss);
        Assert.True(notifier.Reader.TryRead(out var n));
        Assert.Equal(NotificationEvents.KevDetected, n!.Event);
        Assert.Contains("CVE-2024-3094", n.Message);
        Assert.False(notifier.Reader.TryRead(out _)); // zweiter Durchlauf meldet nicht erneut
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

public sealed class InstalledVulnerabilityTests
{
    [Theory]
    [InlineData("Ubuntu 24.04.1 LTS", "24.04", "Ubuntu:24.04:LTS")]
    [InlineData("Proxmox VE", "8.3.0 (Debian GNU/Linux 12 (bookworm))", "Debian:12")]
    [InlineData("Proxmox Backup Server", "3.3.2 (Debian GNU/Linux 12 (bookworm))", "Debian:12")]
    [InlineData("Debian GNU/Linux 12 (bookworm)", "12", "Debian:12")]
    [InlineData("Windows 11 Pro", "23H2", null)]
    public void EcosystemForHost(string osName, string osVersion, string? expected) =>
        Assert.Equal(expected, CveMapping.EcosystemForHost(osName, osVersion));

    [Fact]
    public void EpssCsv_ParsesAndSkipsHeader()
    {
        var csv = "#model_version:v2026.06.15,score_date:2026-11-16T12:03:16Z\ncve,epss,percentile\nCVE-1999-0001,0.04753,0.92025\nCVE-2024-3094,0.909,0.9981\n";
        var map = CveMapping.ParseEpssCsv(new StringReader(csv));

        Assert.Equal(2, map.Count);
        Assert.Equal(0.909f, map[CveMapping.CveKey("CVE-2024-3094")!.Value].Epss, 3);
        Assert.Null(CveMapping.CveKey("GHSA-xxxx"));
    }
}

public sealed class VulnerabilityListTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public VulnerabilityListTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Fact]
    public async Task List_MergesFixedAndUnfixed_KevFirst()
    {
        var site = new Site { Name = "Homelab" };
        var linux = new ManagedHost { Site = site, Name = "web01", Hostname = "web01", CertificateThumbprint = "a", OsFamily = OsFamily.Linux };
        var win = new ManagedHost { Site = site, Name = "dc01", Hostname = "dc01", CertificateThumbprint = "b", OsFamily = OsFamily.Windows };
        _db.Hosts.AddRange(linux, win);
        _db.PendingUpdates.Add(new PendingUpdate { Host = linux, Name = "openssl", Cves = "CVE-2024-0001,CVE-2024-3094" });
        _db.PendingUpdates.Add(new PendingUpdate { Host = win, Name = "2026-11 Cumulative Update (KB5050001)", Cves = "CVE-2026-1001" });
        _db.HostVulnerabilities.Add(new HostVulnerability { Host = linux, CveId = "CVE-2025-9999", Package = "sudo", InstalledVersion = "1.9.15", FoundAt = DateTime.UtcNow });
        _db.CveInfos.Add(new CveInfo { Id = "CVE-2024-3094", IsKev = true, Epss = 0.9 });
        _db.CveInfos.Add(new CveInfo { Id = "CVE-2026-1001", Epss = 0.3 });
        await _db.SaveChangesAsync();
        var cves = new CveService(null!, null!, new AIOPatcher.Server.Options.AIOPatcherOptions(), new Notifier(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CveService>.Instance);

        var list = await AIOPatcher.Server.Endpoints.VulnerabilityEndpoints.ListAsync(null, _db, cves, CancellationToken.None);

        Assert.Equal(4, list.Count);
        Assert.Equal("CVE-2024-3094", list[0].Cve); // KEV zuerst
        Assert.Contains("KEV", list[0].Sources);
        Assert.Contains("MSRC", list.Single(v => v.Cve == "CVE-2026-1001").Sources);
        var unfixed = list.Single(v => v.Cve == "CVE-2025-9999");
        Assert.False(unfixed.FixAvailable);
        Assert.Equal("sudo 1.9.15", Assert.Single(unfixed.Hosts).Package);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
