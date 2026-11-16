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

public sealed class AutoPatchSchedulerTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateTime InWindow = new(2026, 11, 16, 2, 30, 0, DateTimeKind.Utc);   // 03:30 Berlin
    private static readonly DateTime WindowStart = new(2026, 11, 16, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 11, 16, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OutsideWindow = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _host;

    public AutoPatchSchedulerTests()
    {
        // Texte sind zweisprachig: Tests prüfen die englische Fassung, unabhängig von der Sprache des Rechners.
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate(); // echte Migrationen, nicht EnsureCreated

        var site = new Site { Name = "Homelab" };
        _host = new ManagedHost
        {
            Site = site,
            Name = "web01",
            Hostname = "web01",
            CertificateThumbprint = Guid.NewGuid().ToString("N"),
            AutoPatchEnabled = true,
            AutoPatchScope = PatchScope.Security,
            AutoRebootAllowed = true,
            MaintenanceDays = MaintenanceDays.Daily,
            MaintenanceStart = new TimeOnly(3, 0),
            MaintenanceDurationMinutes = 120,
        };
        _db.Hosts.Add(_host);
        _db.SaveChanges();
    }

    private Task<AutoPatchScheduler.RunResult> Run(DateTime now) => AutoPatchScheduler.RunOnceAsync(_db, Berlin, now, CancellationToken.None);

    [Fact]
    public async Task PatchFreeze_SkipsWindow_ForItsSiteOnly()
    {
        _host.LastScanAt = InWindow; // frischer Scan mit offenem Update, sonst "nichts zu tun"
        _db.PendingUpdates.Add(new PendingUpdate { HostId = _host.Id, Name = "openssl", IsSecurity = true });
        var otherSite = new Site { Name = "Kunde" };
        _db.PatchFreezes.Add(new PatchFreeze { Name = "Andere Site", Site = otherSite, StartsAt = InWindow.AddDays(-1), EndsAt = InWindow.AddDays(1), CreatedBy = "test" });
        _db.PatchFreezes.Add(new PatchFreeze { Name = "Jahresabschluss", SiteId = _host.SiteId, StartsAt = InWindow.AddDays(-1), EndsAt = InWindow.AddHours(1), CreatedBy = "test" });
        await _db.SaveChangesAsync();

        var result = await Run(InWindow);

        Assert.Equal(0, result.Created);
        Assert.Contains("Jahresabschluss", _host.LastAutoPatchNote);
        Assert.Empty(_db.Jobs);
    }

    [Fact]
    public async Task InWindow_CreatesOneJobPerWindow()
    {
        var first = await Run(InWindow);
        var second = await Run(InWindow.AddMinutes(10));

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
        var job = await _db.Jobs.SingleAsync();
        Assert.Equal(JobType.Patch, job.Type);
        Assert.Equal(PatchScope.Security, job.Scope);
        Assert.True(job.AllowReboot);
        Assert.Equal(AutoPatchScheduler.Actor, job.RequestedBy);
        Assert.Equal(WindowEnd, job.NotAfter);
        Assert.Equal(WindowStart, (await _db.Hosts.SingleAsync()).LastAutoPatchWindowStart);
    }

    [Fact]
    public async Task OutsideWindow_DoesNothing()
    {
        Assert.Equal(0, (await Run(OutsideWindow)).Created);
        Assert.False(await _db.Jobs.AnyAsync());
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _host.AutoPatchEnabled = false;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await Run(InWindow)).Created);
    }

    [Fact]
    public async Task FreshScanWithoutSecurityUpdates_SkipsWithoutJob()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _db.PendingUpdates.Add(new PendingUpdate { HostId = _host.Id, Name = "nicht-security", IsSecurity = false });
        await _db.SaveChangesAsync();

        var result = await Run(InWindow);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.StartsWith("Nothing to do",(await _db.Hosts.SingleAsync()).LastAutoPatchNote);
    }

    [Fact]
    public async Task PendingRebootNotAllowed_ExplainsWhyNothingHappens()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _host.RebootRequired = true;
        _host.AutoRebootAllowed = false;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await Run(InWindow)).Created);
        Assert.Contains("reboot is pending",(await _db.Hosts.SingleAsync()).LastAutoPatchNote);
    }

    [Fact]
    public async Task FreshScanWithSecurityUpdate_CreatesJob()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _db.PendingUpdates.Add(new PendingUpdate { HostId = _host.Id, Name = "openssl", IsSecurity = true });
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await Run(InWindow)).Created);
    }

    [Fact]
    public async Task PendingRebootWithRebootAllowed_CreatesJobEvenWithoutUpdates()
    {
        _host.LastScanAt = InWindow.AddHours(-2);
        _host.RebootRequired = true;
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await Run(InWindow)).Created);
    }

    [Fact]
    public async Task ActiveJob_SkipsWindow()
    {
        _db.Jobs.Add(new PatchJob { HostId = _host.Id, Type = JobType.Scan, RequestedBy = "admin:x", Status = JobStatus.Running });
        await _db.SaveChangesAsync();

        var result = await Run(InWindow);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.True(await _db.AuditEntries.AnyAsync(a => a.Action == "auto-patch.skipped"));
    }

    [Fact]
    public async Task QueuedJobAfterWindowEnd_IsCancelled()
    {
        await Run(InWindow);                       // Job angelegt, Host offline
        var result = await Run(WindowEnd.AddMinutes(1));

        Assert.Equal(1, result.Expired);
        var job = await _db.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Contains("Maintenance window missed", job.Summary);
    }

    [Fact]
    public async Task RevokedHost_IsIgnored()
    {
        _host.IsRevoked = true;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await Run(InWindow)).Created);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
