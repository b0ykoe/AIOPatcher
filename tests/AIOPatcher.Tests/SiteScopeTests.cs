using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Globale Site-Auswahl im Dashboard: Listen-Endpunkte filtern mit siteId, ohne siteId bleibt alles sichtbar.</summary>
public sealed class SiteScopeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Site _home = new() { Name = "Home" };
    private readonly Site _office = new() { Name = "Office" };
    private readonly ManagedHost _nas;
    private readonly ManagedHost _web;

    public SiteScopeTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _nas = new ManagedHost { Site = _home, Name = "nas", Hostname = "nas", CertificateThumbprint = "a" };
        _web = new ManagedHost { Site = _office, Name = "web", Hostname = "web", CertificateThumbprint = "b" };
        _db.Hosts.AddRange(_nas, _web);
        _db.SaveChanges();
    }

    [Fact]
    public async Task Freezes_SiteKeepsOwnAndGlobal()
    {
        var now = DateTime.UtcNow;
        _db.PatchFreezes.AddRange(
            new PatchFreeze { Name = "global", StartsAt = now, EndsAt = now.AddDays(1), CreatedBy = "t" },
            new PatchFreeze { Name = "home", Site = _home, StartsAt = now, EndsAt = now.AddDays(1), CreatedBy = "t" },
            new PatchFreeze { Name = "office", Site = _office, StartsAt = now, EndsAt = now.AddDays(1), CreatedBy = "t" });
        await _db.SaveChangesAsync();

        Assert.Equal(["global", "home"], (await PatchRuleEndpoints.ListFreezes(_home.Id, _db, default)).Select(f => f.Name).Order());
        Assert.Equal(3, (await PatchRuleEndpoints.ListFreezes(null, _db, default)).Count);
    }

    [Fact]
    public async Task Holds_SiteKeepsOwnGlobalAndHostRules()
    {
        _db.PackageHolds.AddRange(
            new PackageHold { Pattern = "global-*", CreatedBy = "t" },
            new PackageHold { Pattern = "home-*", Site = _home, CreatedBy = "t" },
            new PackageHold { Pattern = "nas-*", Host = _nas, CreatedBy = "t" },
            new PackageHold { Pattern = "web-*", Host = _web, CreatedBy = "t" });
        await _db.SaveChangesAsync();

        Assert.Equal(["global-*", "home-*", "nas-*"], (await PatchRuleEndpoints.ListHolds(null, _home.Id, _db, default)).Select(h => h.Pattern).Order());
        Assert.Equal(["global-*", "web-*"], (await PatchRuleEndpoints.ListHolds(null, _office.Id, _db, default)).Select(h => h.Pattern).Order());
    }

    [Fact]
    public async Task Software_CountsOnlyHostsOfSite()
    {
        _db.InstalledApps.AddRange(
            new InstalledApp { Host = _nas, Name = "curl", Source = "apt", Version = "1" },
            new InstalledApp { Host = _web, Name = "curl", Source = "apt", Version = "1" },
            new InstalledApp { Host = _web, Name = "nginx", Source = "apt", Version = "1" });
        await _db.SaveChangesAsync();

        var home = await SoftwareEndpoints.Summary(null, null, _home.Id, _db, default);
        Assert.Equal(("curl", 1), (home.Single().Name, home.Single().Hosts));
        Assert.Equal(2, (await SoftwareEndpoints.Summary(null, null, null, _db, default)).Count);
    }

    [Fact]
    public async Task Plans_ShownWhenTheyTouchTheSite()
    {
        var homePlan = new Runbook { Name = "Home", Waves = [new RunbookWave { Order = 1, Name = "A", Hosts = [new RunbookWaveHost { Host = _nas }] }] };
        var officeRule = new Runbook { Name = "Office", Waves = [new RunbookWave { Order = 1, Name = "A", MemberSiteId = _office.Id }] };
        var empty = new Runbook { Name = "Leer", Waves = [new RunbookWave { Order = 1, Name = "A" }] };
        _db.Runbooks.AddRange(homePlan, officeRule, empty);
        await _db.SaveChangesAsync();

        var members = await PatchPlans.ResolveAsync(_db, default);
        var sites = await _db.Hosts.ToDictionaryAsync(h => h.Id, h => h.SiteId);

        Assert.True(RunbookEndpoints.TouchesSite(homePlan, _home.Id, members, sites));
        Assert.False(RunbookEndpoints.TouchesSite(homePlan, _office.Id, members, sites));
        Assert.True(RunbookEndpoints.TouchesSite(officeRule, _office.Id, members, sites));
        Assert.False(RunbookEndpoints.TouchesSite(officeRule, _home.Id, members, sites));
        Assert.True(RunbookEndpoints.TouchesSite(empty, _home.Id, members, sites)); // neu angelegt: nicht verstecken
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
