using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class IpamTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public IpamTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Theory]
    [InlineData("10.10.0.0/24", new string[0], "10.10.0.2")]
    [InlineData("10.10.0.0/24", new[] { "10.10.0.2", "10.10.0.3" }, "10.10.0.4")]
    [InlineData("10.10.0.0/30", new[] { "10.10.0.2" }, null)]
    [InlineData("2a01:4f8::/64", new string[0], null)]
    public void FirstFree_SkipsNetworkGatewayBroadcastAndUsed(string cidr, string[] used, string? expected) =>
        Assert.Equal(expected, IpamEndpoints.FirstFree(cidr, used.ToHashSet()));

    [Theory]
    [InlineData("10.10.0.17/24", "10.10.0.0/24")]
    [InlineData(" 2a01:4f8:0:0::1/64 ", "2a01:4f8::/64")]
    [InlineData("10.10.0.0/33", null)]
    [InlineData("kaputt", null)]
    public void Normalize_Cidr(string input, string? expected) => Assert.Equal(expected, IpamEndpoints.Normalize(input));

    [Fact]
    public async Task ArpImport_ReplacesOwnEntries_AndAssignsMostSpecificSubnet()
    {
        var site = new Site { Name = "Homelab" };
        var wide = new Subnet { Site = site, Cidr = "10.0.0.0/8", Name = "Alles" };
        var lan = new Subnet { Site = site, Cidr = "10.10.0.0/24", Name = "LAN" };
        _db.Subnets.AddRange(wide, lan);
        var fw = new Integration { Name = "fw01", Type = IntegrationType.Opnsense, ProtectedSecret = "x", Site = site };
        _db.Integrations.Add(fw);
        await _db.SaveChangesAsync();

        await OpnsenseMonitor.ImportArpAsync(_db, fw, site.Id, [new("10.10.0.20", "AA:BB:CC:00:00:01", "LAN", "printer"), new("10.20.0.5", null, "OPT1", null)], CancellationToken.None);
        await _db.SaveChangesAsync();
        await OpnsenseMonitor.ImportArpAsync(_db, fw, site.Id, [new("10.10.0.20", "aa:bb:cc:00:00:01", "LAN", "printer")], CancellationToken.None);
        await _db.SaveChangesAsync();

        var entry = await _db.IpAddresses.SingleAsync();
        Assert.Equal(lan.Id, entry.SubnetId);
        Assert.Equal("aa:bb:cc:00:00:01", entry.MacAddress);
        Assert.Equal("printer", entry.Hostname);
        Assert.Equal(IpSource.Opnsense, entry.Source);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
