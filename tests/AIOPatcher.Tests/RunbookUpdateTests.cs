using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>
/// Bestehenden Plan speichern: Die Gruppen werden ersetzt. Bis 0.14.4 hielt EF Core die neuen Gruppen (Id schon gesetzt)
/// für vorhanden, schickte UPDATE statt INSERT und das Speichern scheiterte mit "FOREIGN KEY constraint failed".
/// </summary>
public sealed class RunbookUpdateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SavingAnExistingPlan_ReplacesItsGroups(bool excludedHost, bool hostInOtherPlan)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        var site = new Site { Name = "Home" };
        var hosts = Enumerable.Range(1, 5).Select(i => new ManagedHost { Site = site, Name = "h" + i, Hostname = "h" + i, CertificateThumbprint = "t" + i }).ToList();
        db.Hosts.AddRange(hosts);
        hosts[4].PlanExcluded = excludedHost;
        var plan = new Runbook { Name = "Security daily", Waves = [new RunbookWave { Order = 1, Name = "All", IncludeOthers = true, Hosts = [new RunbookWaveHost { Host = hosts[0] }] }] };
        db.Runbooks.Add(plan);
        if (hostInOtherPlan)
        {
            db.Runbooks.Add(new Runbook { Name = "Other", Waves = [new RunbookWave { Order = 1, Name = "x", Hosts = [new RunbookWaveHost { Host = hosts[1] }] }] });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var request = new RunbookEndpoints.RunbookRequest("Security daily", "x", RunbookFailurePolicy.FinishWaveThenPause, true, 127, "02:00", 120,
        [
            new("All hosts", PatchScope.Security, false, false, hosts.Take(3).Select(h => h.Id).ToList(), IncludeOthers: true),
            new("Proxmox", PatchScope.All, false, true, [hosts[3].Id, hosts[4].Id]),
        ]);
        var result = await RunbookEndpoints.UpdateRunbook(plan.Id, request, db, new ServerClock(new AIOPatcherOptions()), new AuditLog(db, new HttpContextAccessor()), default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<RunbookEndpoints.RunbookDto>>(result);
        db.ChangeTracker.Clear();
        var saved = await db.Runbooks.Include(r => r.Waves).ThenInclude(w => w.Hosts).SingleAsync(r => r.Id == plan.Id);
        Assert.Equal(["All hosts", "Proxmox"], saved.Waves.OrderBy(w => w.Order).Select(w => w.Name));
        Assert.Equal(5, saved.Waves.Sum(w => w.Hosts.Count));
        Assert.False(await db.Hosts.AnyAsync(h => h.PlanExcluded)); // direkt ausgewählt hebt die Ausnahme auf
        Assert.Equal(1, await db.RunbookWaves.CountAsync(w => w.RunbookId == plan.Id && w.IncludeOthers));
    }
}
