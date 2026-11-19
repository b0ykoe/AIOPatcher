using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public class MaintenanceWindowTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeOnly ThreeAm = new(3, 0);

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Current_InsideDailyWindow_Winter()
    {
        // 16.11.2026 03:30 Berlin (MEZ, UTC+1) = 02:30 UTC
        var w = MaintenanceWindow.Current(MaintenanceDays.Daily, ThreeAm, 120, Utc(2026, 11, 16, 2, 30), Berlin);

        Assert.Equal(Utc(2026, 11, 16, 2), w!.Value.StartUtc);
        Assert.Equal(Utc(2026, 11, 16, 4), w.Value.EndUtc);
    }

    [Fact]
    public void Current_OutsideWindow_IsNull_AndNextIsTomorrow()
    {
        var now = Utc(2026, 11, 16, 5);

        Assert.Null(MaintenanceWindow.Current(MaintenanceDays.Daily, ThreeAm, 120, now, Berlin));
        Assert.Equal(Utc(2026, 11, 17, 2), MaintenanceWindow.Next(MaintenanceDays.Daily, ThreeAm, 120, now, Berlin)!.Value.StartUtc);
    }

    [Fact]
    public void Current_WindowAcrossMidnight_BelongsToStartDay()
    {
        // Montag 23:00 für 3 Stunden. Dienstag 00:30 Berlin = Montag 23:30 UTC.
        var w = MaintenanceWindow.Current(MaintenanceDays.Monday, new TimeOnly(23, 0), 180, Utc(2026, 11, 16, 23, 30), Berlin);

        Assert.Equal(Utc(2026, 11, 16, 22), w!.Value.StartUtc); // 16.11.2026 ist ein Montag
        Assert.Equal(Utc(2026, 11, 17, 1), w.Value.EndUtc);
    }

    [Fact]
    public void Current_WrongWeekday_IsNull()
    {
        Assert.Null(MaintenanceWindow.Current(MaintenanceDays.Tuesday, ThreeAm, 120, Utc(2026, 11, 16, 2, 30), Berlin));
    }

    [Fact]
    public void Next_SkipsToNextActiveWeekday()
    {
        // Montag 05:00 UTC, Fenster nur Do -> Do 19.11. 03:00 Berlin = 02:00 UTC
        var next = MaintenanceWindow.Next(MaintenanceDays.Thursday, ThreeAm, 60, Utc(2026, 11, 16, 5), Berlin);

        Assert.Equal(Utc(2026, 11, 19, 2), next!.Value.StartUtc);
    }

    [Fact]
    public void Summer_UsesUtcPlusTwo()
    {
        // 15.07.2026 03:00 Berlin (MESZ, UTC+2) = 01:00 UTC
        var w = MaintenanceWindow.Current(MaintenanceDays.Daily, ThreeAm, 60, Utc(2026, 7, 15, 1, 15), Berlin);

        Assert.Equal(Utc(2026, 7, 15, 1), w!.Value.StartUtc);
    }

    [Fact]
    public void DstSpringForward_NonExistentStartTime_ShiftsOneHour()
    {
        // 29.03.2026: 02:00 -> 03:00 MESZ. 02:30 gibt es nicht -> 03:30 MESZ = 01:30 UTC
        var next = MaintenanceWindow.Next(MaintenanceDays.Sunday, new TimeOnly(2, 30), 60, Utc(2026, 3, 28, 12), Berlin);

        Assert.Equal(Utc(2026, 3, 29, 1, 30), next!.Value.StartUtc);
    }

    [Fact]
    public void NoDays_NeverOpens()
    {
        Assert.Null(MaintenanceWindow.Next(MaintenanceDays.None, ThreeAm, 120, Utc(2026, 11, 16, 2), Berlin));
    }
}

/// <summary>Patch-Pläne: Zeitplan, Zugehörigkeit (ausgewählt, Regel, alle übrigen), Übernahme des früheren Auto-Patch pro Host.</summary>
public sealed class PatchPlanTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateTime InWindow = new(2026, 11, 16, 2, 30, 0, DateTimeKind.Utc);   // 03:30 Berlin
    private static readonly DateTime WindowEnd = new(2026, 11, 16, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OutsideWindow = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Site _site = new() { Name = "Homelab" };
    private readonly ManagedHost _host;

    public PatchPlanTests()
    {
        // Texte sind zweisprachig: Tests prüfen die englische Fassung, unabhängig von der Sprache des Rechners.
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate(); // echte Migrationen, nicht EnsureCreated

        _host = Host("web01");
        _host.AutoPatchEnabled = true; // früheres Auto-Patch pro Host: wird zum Plan
        _host.AutoPatchScope = PatchScope.Security;
        _host.AutoRebootAllowed = true;
        _host.MaintenanceDays = MaintenanceDays.Daily;
        _host.MaintenanceStart = new TimeOnly(3, 0);
        _host.MaintenanceDurationMinutes = 120;
        _db.SaveChanges();
        PatchPlans.MigrateHostAutoPatchAsync(_db, CancellationToken.None).GetAwaiter().GetResult();
    }

    private ManagedHost Host(string name, string? tags = null)
    {
        var host = new ManagedHost { Site = _site, Name = name, Hostname = name, CertificateThumbprint = Guid.NewGuid().ToString("N"), Tags = tags };
        _db.Hosts.Add(host);
        return host;
    }

    private Task Tick(DateTime now) => RunbookEngine.TickAsync(_db, Berlin, now, CancellationToken.None);

    /// <summary>Erster Durchlauf legt den Lauf an, der zweite (10 s später) startet die Jobs.</summary>
    private async Task TickTwice(DateTime now)
    {
        await Tick(now);
        await Tick(now.AddSeconds(10));
    }

    private Runbook Plan => _db.Runbooks.Include(r => r.Waves).ThenInclude(w => w.Hosts).Single();

    [Fact]
    public async Task Migration_TurnsHostAutoPatchIntoPlan_Once()
    {
        var plan = Plan;
        Assert.True(plan.ScheduleEnabled);
        Assert.Equal((MaintenanceDays.Daily, new TimeOnly(3, 0), 120), (plan.ScheduleDays, plan.ScheduleStart, plan.ScheduleDurationMinutes));
        var group = Assert.Single(plan.Waves);
        Assert.Equal((PatchScope.Security, true), (group.Scope, group.AllowReboot));
        Assert.Equal(_host.Id, Assert.Single(group.Hosts).HostId);
        Assert.False(_host.AutoPatchEnabled);
        Assert.Equal(0, await PatchPlans.MigrateHostAutoPatchAsync(_db, CancellationToken.None));
    }

    [Fact]
    public async Task InWindow_OneRunPerWindow_JobLimitedToWindow()
    {
        await Tick(InWindow);
        await Tick(InWindow.AddMinutes(10));

        var run = await _db.RunbookRuns.SingleAsync();
        Assert.Equal(AutoPatchScheduler.Actor, run.RequestedBy);
        var job = await _db.Jobs.SingleAsync();
        Assert.Equal((JobType.Patch, PatchScope.Security, true), (job.Type, job.Scope, job.AllowReboot));
        Assert.Equal(WindowEnd, job.NotAfter);
    }

    [Fact]
    public async Task OutsideWindow_OrScheduleOff_DoesNothing()
    {
        await Tick(OutsideWindow);
        Assert.False(await _db.RunbookRuns.AnyAsync());

        Plan.ScheduleEnabled = false;
        await _db.SaveChangesAsync();
        await Tick(InWindow);
        Assert.False(await _db.RunbookRuns.AnyAsync());
    }

    [Fact]
    public async Task PatchFreeze_SkipsWindow_ForItsSiteOnly()
    {
        var otherSite = new Site { Name = "Kunde" };
        _db.PatchFreezes.Add(new PatchFreeze { Name = "Andere Site", Site = otherSite, StartsAt = InWindow.AddDays(-1), EndsAt = InWindow.AddDays(1), CreatedBy = "test" });
        _db.PatchFreezes.Add(new PatchFreeze { Name = "Jahresabschluss", SiteId = _host.SiteId, StartsAt = InWindow.AddDays(-1), EndsAt = InWindow.AddHours(1), CreatedBy = "test" });
        await _db.SaveChangesAsync();

        await Tick(InWindow);

        Assert.Contains("Jahresabschluss", Plan.LastWindowNote);
        Assert.Empty(_db.Jobs);
    }

    [Fact]
    public async Task FreshScanWithoutSecurityUpdates_SkippedWithoutJob()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _db.PendingUpdates.Add(new PendingUpdate { HostId = _host.Id, Name = "nicht-security", IsSecurity = false });
        await _db.SaveChangesAsync();

        await TickTwice(InWindow);

        var item = await _db.RunbookRunItems.SingleAsync();
        Assert.Equal(RunItemStatus.Skipped, item.Status);
        Assert.StartsWith("Nothing to do", item.Message);
        Assert.Empty(_db.Jobs);
    }

    [Fact]
    public async Task PendingRebootWithRebootAllowed_CreatesJobEvenWithoutUpdates()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _host.RebootRequired = true;
        await _db.SaveChangesAsync();

        await TickTwice(InWindow);
        Assert.Single(_db.Jobs);
    }

    [Fact]
    public async Task QueuedJobAfterWindowEnd_IsCancelled()
    {
        await TickTwice(InWindow); // Job angelegt, Host offline
        Assert.Equal(1, await AutoPatchScheduler.RunOnceAsync(_db, WindowEnd.AddMinutes(1), CancellationToken.None));

        var job = await _db.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Contains("Maintenance window missed", job.Summary);
    }

    [Fact]
    public async Task RevokedHost_IsNotAMember()
    {
        _host.IsRevoked = true;
        await _db.SaveChangesAsync();

        await Tick(InWindow);
        Assert.False(await _db.RunbookRuns.AnyAsync());
        Assert.Contains("no hosts", Plan.LastWindowNote);
    }

    [Fact]
    public async Task Membership_SelectedBeatsRule_RuleBeatsOthers_OnePlanPerHost()
    {
        var db1 = Host("db1", "db,prod");
        var nas = Host("nas");
        var tagged = Host("web02", "prod");
        _db.Runbooks.Add(new Runbook
        {
            Name = "Prod",
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
            Waves = [new RunbookWave { Order = 1, Name = "Prod", MemberTags = "prod" }],
        });
        _db.Runbooks.Add(new Runbook
        {
            Name = "Rest",
            CreatedAt = DateTime.UtcNow.AddMinutes(2),
            Waves = [new RunbookWave { Order = 1, Name = "Alle anderen", IncludeOthers = true, Hosts = [new RunbookWaveHost { Host = db1 }] }],
        });
        await _db.SaveChangesAsync();

        var members = await PatchPlans.ResolveAsync(_db, CancellationToken.None);

        Assert.Equal(("Prod", PlanVia.Rule), (members[tagged.Id].PlanName, members[tagged.Id].Via));
        Assert.Equal(("Rest", PlanVia.Host), (members[db1.Id].PlanName, members[db1.Id].Via)); // ausgewählt schlägt Tag-Regel
        Assert.Equal(("Rest", PlanVia.Others), (members[nas.Id].PlanName, members[nas.Id].Via));
        Assert.Equal(PlanVia.Host, members[_host.Id].Via); // aus der Übernahme
    }

    [Fact]
    public async Task ExcludedHost_IsNotPickedUpByRulesOrOthers_UntilIncludedAgain()
    {
        var pve = Host("pve", "prod");
        var nas = Host("nas");
        _db.Runbooks.Add(new Runbook
        {
            Name = "Alle",
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
            Waves = [new RunbookWave { Order = 1, Name = "Prod", MemberTags = "prod" }, new RunbookWave { Order = 2, Name = "Rest", IncludeOthers = true }],
        });
        pve.PlanExcluded = true;
        nas.PlanExcluded = true;
        await _db.SaveChangesAsync();

        var members = await PatchPlans.ResolveAsync(_db, CancellationToken.None);
        Assert.False(members.ContainsKey(pve.Id)); // Tag-Regel greift nicht
        Assert.False(members.ContainsKey(nas.Id)); // "alle übrigen" greift nicht

        var audit = new AuditLog(_db, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        await AIOPatcher.Server.Endpoints.HostEndpoints.IncludeInPlansAsync([pve.Id, nas.Id], _db, audit, CancellationToken.None);
        members = await PatchPlans.ResolveAsync(_db, CancellationToken.None);
        Assert.Equal(PlanVia.Rule, members[pve.Id].Via);
        Assert.Equal(PlanVia.Others, members[nas.Id].Via);
    }

    [Fact]
    public async Task PauseBetweenGroups_DelaysNextGroup()
    {
        var second = Host("web02", "late");
        var plan = Plan;
        plan.Waves.Single().PauseAfterMinutes = 30;
        _db.RunbookWaves.Add(new RunbookWave { RunbookId = plan.Id, Order = 2, Name = "Später", MemberTags = "late" });
        await _db.SaveChangesAsync();

        var run = await RunbookEngine.StartRunAsync(_db, plan.Id, "admin:test", InWindow, null, CancellationToken.None);
        await _db.SaveChangesAsync();
        await Tick(InWindow);

        // Gruppe 1 fertig (Job erfolgreich, Host meldet sich danach)
        var job = await _db.Jobs.SingleAsync();
        job.Status = JobStatus.Succeeded;
        job.FinishedAt = InWindow.AddMinutes(1);
        _host.LastSeenAt = InWindow.AddMinutes(2);
        await _db.SaveChangesAsync();
        await Tick(InWindow.AddMinutes(3));

        Assert.Equal(2, run.CurrentWave);
        Assert.Equal(InWindow.AddMinutes(33), run.ResumeAt);
        Assert.Single(_db.Jobs); // Gruppe 2 wartet

        await Tick(InWindow.AddMinutes(34));
        Assert.Equal(2, await _db.Jobs.CountAsync());
        Assert.Contains(await _db.Jobs.Select(j => j.HostId).ToListAsync(), id => id == second.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
