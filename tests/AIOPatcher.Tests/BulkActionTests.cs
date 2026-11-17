using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class BulkActionTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _web, _db1, _revoked, _busy;

    public BulkActionTests()
    {
        // Texte sind zweisprachig: Tests prüfen die englische Fassung, unabhängig von der Sprache des Rechners.
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        var site = new Site { Name = "Homelab" };
        ManagedHost Host(string name, bool revoked = false) => new()
        {
            Site = site, Name = name, Hostname = name, CertificateThumbprint = Guid.NewGuid().ToString("N"), IsRevoked = revoked,
        };

        _web = Host("web01");
        _db1 = Host("db01");
        _revoked = Host("old01", revoked: true);
        _busy = Host("busy01");
        _db.Hosts.AddRange(_web, _db1, _revoked, _busy);
        _db.Jobs.Add(new PatchJob { Host = _busy, Type = JobType.Scan, RequestedBy = "admin:x", Status = JobStatus.Queued });
        _db.SaveChanges();
    }

    [Fact]
    public async Task PlanAsync_CreatesJobsForEligibleHosts_AndExplainsSkips()
    {
        var unknown = Guid.NewGuid();
        var plan = await JobPlanner.PlanAsync(_db, [_web.Id, _db1.Id, _revoked.Id, _busy.Id, unknown], JobType.Patch, PatchScope.Security, allowReboot: true, "admin:test", CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.Equal(["db01", "web01"], plan.Created.Select(j => j.Host!.Name).Order());
        Assert.All(plan.Created, j => Assert.True(j.AllowReboot));
        Assert.Equal(3, plan.Skipped.Count);
        Assert.Contains(plan.Skipped, s => s.HostId == _revoked.Id && s.Reason.Contains("revoked"));
        Assert.Contains(plan.Skipped, s => s.HostId == _busy.Id && s.Reason.Contains("already"));
        Assert.Contains(plan.Skipped, s => s.HostId == unknown && s.Reason == JobPlanner.HostNotFound);
        Assert.Equal(3, await _db.Jobs.CountAsync()); // 1 vorhandener + 2 neue
    }

    [Fact]
    public async Task PlanAsync_RebootJob_AlwaysAllowsReboot()
    {
        var plan = await JobPlanner.PlanAsync(_db, [_web.Id], JobType.Reboot, PatchScope.Security, allowReboot: false, "admin:test", CancellationToken.None);

        Assert.True(Assert.Single(plan.Created).AllowReboot);
    }

    [Fact]
    public async Task PlanAsync_DuplicateIds_CreateOneJob()
    {
        var plan = await JobPlanner.PlanAsync(_db, [_web.Id, _web.Id], JobType.Scan, PatchScope.Security, false, "admin:test", CancellationToken.None);

        Assert.Single(plan.Created);
    }

    [Theory]
    [InlineData(true, 0, "03:00", 120, "days")]
    [InlineData(true, 200, "03:00", 120, "days")]
    [InlineData(true, 127, "25:00", 120, "start")]
    [InlineData(true, 127, "03:00", 10, "durationMinutes")]
    [InlineData(true, 127, "03:00", 721, "durationMinutes")]
    public void Validate_RejectsInvalidSettings(bool enabled, int days, string start, int duration, string field)
    {
        var errors = AutoPatchSettings.Validate(new AutoPatchRequest(enabled, PatchScope.Security, false, days, start, duration), out _);

        Assert.True(errors.ContainsKey(field));
    }

    [Fact]
    public void Validate_AcceptsDisabledScheduleWithoutDays()
    {
        Assert.Empty(AutoPatchSettings.Validate(new AutoPatchRequest(false, PatchScope.All, true, 0, "22:30", 90), out var start));
        Assert.Equal(new TimeOnly(22, 30), start);
        Assert.Empty(AutoPatchSettings.Validate(new AutoPatchRequest(true, PatchScope.All, true, 31, "22:30", 90), out _));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
