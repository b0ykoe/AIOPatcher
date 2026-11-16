using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class RunbookEngineTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _web1, _web2, _pve;
    private readonly Runbook _runbook;
    private readonly Notifier _notifier = new();
    private DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc); // 13:00 Berlin, außerhalb jedes Fensters

    public RunbookEngineTests()
    {
        // Texte sind zweisprachig: Tests prüfen die englische Fassung, unabhängig von der Sprache des Rechners.
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        var site = new Site { Name = "Homelab" };
        ManagedHost Host(string name) => new() { Site = site, Name = name, Hostname = name, CertificateThumbprint = Guid.NewGuid().ToString("N") };
        _web1 = Host("web01");
        _web2 = Host("web02");
        _pve = Host("pve01");
        _db.Hosts.AddRange(_web1, _web2, _pve);

        _runbook = new Runbook
        {
            Name = "Homelab-Patchday",
            Waves =
            [
                new RunbookWave { Order = 1, Name = "Web", Scope = PatchScope.Security, Hosts = [new() { Host = _web1 }, new() { Host = _web2 }] },
                new RunbookWave { Order = 2, Name = "PVE", Scope = PatchScope.All, AllowReboot = true, Hosts = [new() { Host = _pve }] },
            ],
        };
        _db.Runbooks.Add(_runbook);
        _db.SaveChanges();
    }

    private List<Notification> DrainNotifications()
    {
        var list = new List<Notification>();
        while (_notifier.Reader.TryRead(out var n))
        {
            list.Add(n);
        }

        return list;
    }

    [Fact]
    public async Task Notifies_OnPause_AndOnFinish_OncePerChange()
    {
        await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1, JobStatus.Failed);
        await CompleteAsync(_web2);
        await TickAsync();
        await TickAsync();

        var paused = Assert.Single(DrainNotifications());
        Assert.Equal(NotificationEvents.RunNeedsAttention, paused.Event);
        Assert.Contains("paused", paused.Title);

        var run = await _db.RunbookRuns.SingleAsync();
        Assert.Null(RunbookEngine.Continue(run));
        await _db.SaveChangesAsync();
        await TickAsync();
        await TickAsync();
        await CompleteAsync(_pve);
        await TickAsync();
        await TickAsync();

        var finished = Assert.Single(DrainNotifications());
        Assert.Equal(NotificationEvents.RunFinished, finished.Event);
        Assert.Equal(NotificationSeverity.Critical, finished.Severity); // ein Host war fehlgeschlagen
    }

    // --- Hilfen: Agent-Verhalten nachspielen -----------------------------------------------------------

    private async Task<RunbookRun> StartAsync(DateTime? notAfter = null)
    {
        var run = await RunbookEngine.StartRunAsync(_db, _runbook.Id, "admin:test", _now, notAfter, CancellationToken.None);
        await _db.SaveChangesAsync();
        return run;
    }

    private async Task TickAsync(TimeSpan? advance = null)
    {
        _now += advance ?? TimeSpan.FromSeconds(10);
        await RunbookEngine.TickAsync(_db, Berlin, _now, CancellationToken.None, _notifier);
    }

    private async Task<PatchJob> JobOf(ManagedHost host) =>
        await _db.Jobs.Where(j => j.HostId == host.Id).OrderByDescending(j => j.CreatedAt).FirstAsync();

    /// <summary>Agent beendet den Job und meldet sich danach wieder (optional nach Neustart).</summary>
    private async Task CompleteAsync(ManagedHost host, JobStatus status = JobStatus.Succeeded, bool reportBack = true, bool stillNeedsReboot = false)
    {
        var job = await JobOf(host);
        job.Status = status;
        job.FinishedAt = _now;
        job.Summary = status == JobStatus.Succeeded ? "ok" : "apt-get exited with code 100.";
        host.RebootRequired = stillNeedsReboot;
        if (reportBack)
        {
            host.LastSeenAt = _now.AddSeconds(5);
        }

        await _db.SaveChangesAsync();
    }

    private async Task<RunbookRun> ReloadAsync(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.RunbookRuns.Include(r => r.Items).Include(r => r.Waves).SingleAsync(r => r.Id == id);
    }

    // --- Tests ---------------------------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_RunsWavesInOrder()
    {
        var run = await StartAsync();
        await TickAsync();

        var web1Job = await JobOf(_web1);
        Assert.Equal(PatchScope.Security, web1Job.Scope);
        Assert.StartsWith("runbook:", web1Job.RequestedBy);
        Assert.False(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id)); // Welle 2 noch nicht

        await CompleteAsync(_web1);
        await CompleteAsync(_web2);
        await TickAsync(); // Welle 1 geprüft und fertig -> nächste Welle
        await TickAsync(); // Welle 2 startet

        var pveJob = await JobOf(_pve);
        Assert.Equal(PatchScope.All, pveJob.Scope);
        Assert.True(pveJob.AllowReboot);

        await CompleteAsync(_pve);
        await TickAsync();

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.All(run.Items, i => Assert.Equal(RunItemStatus.Succeeded, i.Status));
        Assert.Equal("3 succeeded.", run.Message);
    }

    [Fact]
    public async Task Failure_FinishWaveThenPause_ThenContinue()
    {
        var run = await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1, JobStatus.Failed);
        await CompleteAsync(_web2);
        await TickAsync();

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Contains("1 host(s) failed", run.Message);
        Assert.False(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id));

        Assert.Null(RunbookEngine.Continue(run));
        await _db.SaveChangesAsync();
        await TickAsync(); // bestätigte Fehler -> nächste Welle
        await TickAsync();

        Assert.True(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id));
    }

    [Fact]
    public async Task StopImmediately_DoesNotStartWaitingHosts()
    {
        _runbook.FailurePolicy = RunbookFailurePolicy.StopImmediately;
        _db.Jobs.Add(new PatchJob { HostId = _web2.Id, Type = JobType.Scan, RequestedBy = "admin:x", Status = JobStatus.Running });
        await _db.SaveChangesAsync();

        var run = await StartAsync();
        await TickAsync(); // web01 startet, web02 wartet auf seinen Scan
        await CompleteAsync(_web1, JobStatus.Failed);
        var scan = await _db.Jobs.SingleAsync(j => j.HostId == _web2.Id && j.Type == JobType.Scan);
        scan.Status = JobStatus.Succeeded;
        await _db.SaveChangesAsync();
        await TickAsync();

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(RunItemStatus.Pending, run.Items.Single(i => i.HostId == _web2.Id).Status);
        Assert.False(await _db.Jobs.AnyAsync(j => j.HostId == _web2.Id && j.Type == JobType.Patch));
    }

    [Fact]
    public async Task FinishWave_StartsWaitingHostOnceItsOtherJobIsDone()
    {
        _db.Jobs.Add(new PatchJob { HostId = _web2.Id, Type = JobType.Scan, RequestedBy = "admin:x", Status = JobStatus.Running });
        await _db.SaveChangesAsync();

        var run = await StartAsync();
        await TickAsync();
        Assert.Contains("another job",(await ReloadAsync(run.Id)).Items.Single(i => i.HostId == _web2.Id).Message);

        (await _db.Jobs.SingleAsync(j => j.HostId == _web2.Id && j.Type == JobType.Scan)).Status = JobStatus.Succeeded;
        await _db.SaveChangesAsync();
        await TickAsync();

        Assert.True(await _db.Jobs.AnyAsync(j => j.HostId == _web2.Id && j.Type == JobType.Patch));
    }

    [Fact]
    public async Task ApprovalRequired_WaitsBeforeWave()
    {
        _runbook.Waves.Single(w => w.Order == 2).RequireApproval = true;
        await _db.SaveChangesAsync();

        var run = await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1);
        await CompleteAsync(_web2);
        await TickAsync();

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.WaitingApproval, run.Status);
        await TickAsync();
        Assert.False(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id));

        Assert.Null(RunbookEngine.Approve(run));
        await _db.SaveChangesAsync();
        await TickAsync();
        Assert.True(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id));
    }

    [Fact]
    public async Task Verify_FailsIfHostNeverReportsBack()
    {
        var run = await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1, reportBack: false);
        await CompleteAsync(_web2);
        await TickAsync();

        Assert.Equal(RunItemStatus.Verifying, (await ReloadAsync(run.Id)).Items.Single(i => i.HostId == _web1.Id).Status);

        await TickAsync(RunbookEngine.VerifyTimeout + TimeSpan.FromMinutes(1));
        var item = (await ReloadAsync(run.Id)).Items.Single(i => i.HostId == _web1.Id);
        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Contains("did not report back", item.Message);
    }

    [Fact]
    public async Task Verify_RebootStillPending_FailsWhenRebootWasAllowed()
    {
        var run = await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1);
        await CompleteAsync(_web2);
        await TickAsync();
        await TickAsync();
        await CompleteAsync(_pve, stillNeedsReboot: true);
        await TickAsync();

        run = await ReloadAsync(run.Id);
        var item = run.Items.Single(i => i.HostId == _pve.Id);
        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Contains("reboot", item.Message);
        Assert.Equal(RunStatus.Paused, run.Status);
    }

    [Fact]
    public async Task Verify_RebootPendingButNotAllowed_SucceedsWithNote()
    {
        var run = await StartAsync();
        await TickAsync();
        await CompleteAsync(_web1, stillNeedsReboot: true);
        await CompleteAsync(_web2);
        await TickAsync();

        var item = (await ReloadAsync(run.Id)).Items.Single(i => i.HostId == _web1.Id);
        Assert.Equal(RunItemStatus.Succeeded, item.Status);
        Assert.Contains("not allowed", item.Message);
    }

    [Fact]
    public async Task WindowEnd_PausesBeforeNextWave_AndJobsCarryWindowEnd()
    {
        var run = await StartAsync(notAfter: _now.AddMinutes(30));
        await TickAsync();
        Assert.Equal(_now.AddMinutes(30).AddSeconds(-10), (await JobOf(_web1)).NotAfter);

        await CompleteAsync(_web1);
        await CompleteAsync(_web2);
        await TickAsync(TimeSpan.FromMinutes(40));

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Contains("Maintenance window over", run.Message);
        Assert.False(await _db.Jobs.AnyAsync(j => j.HostId == _pve.Id));
    }

    [Fact]
    public async Task Cancel_WithdrawsQueuedJobs_AndSkipsPendingHosts()
    {
        var run = await StartAsync();
        await TickAsync();

        Assert.Null(await RunbookEngine.CancelAsync(_db, run, _now, CancellationToken.None));
        await _db.SaveChangesAsync();

        run = await ReloadAsync(run.Id);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.All(await _db.Jobs.Where(j => j.Type == JobType.Patch).ToListAsync(), j => Assert.Equal(JobStatus.Cancelled, j.Status));
        Assert.Equal(RunItemStatus.Skipped, run.Items.Single(i => i.HostId == _pve.Id).Status);
    }

    [Fact]
    public async Task Start_RejectsSecondActiveRun_AndEmptyRunbook()
    {
        await StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync());

        var empty = new Runbook { Name = "Leer", Waves = [new RunbookWave { Order = 1, Name = "Nichts" }] };
        _db.Runbooks.Add(empty);
        await _db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookEngine.StartRunAsync(_db, empty.Id, "admin:test", _now, null, CancellationToken.None));
        Assert.Contains("contains no hosts", ex.Message);
    }

    [Fact]
    public async Task DeletedHost_IsSkipped_AndHostCanBeDeleted()
    {
        var run = await StartAsync();
        _db.Hosts.Remove(_web2);
        await _db.SaveChangesAsync(); // darf nicht an Fremdschlüsseln scheitern

        await TickAsync();
        var item = (await ReloadAsync(run.Id)).Items.Single(i => i.HostName == "web02");
        Assert.Null(item.HostId);
        Assert.Equal(RunItemStatus.Skipped, item.Status);
    }

    [Fact]
    public async Task Schedule_StartsOneRunPerWindow_AndSuspendsHostAutoPatch()
    {
        _runbook.ScheduleEnabled = true;
        _runbook.ScheduleDays = MaintenanceDays.Daily;
        _runbook.ScheduleStart = new TimeOnly(3, 0);
        _runbook.ScheduleDurationMinutes = 240;
        _web1.AutoPatchEnabled = true;
        _web1.MaintenanceDays = MaintenanceDays.Daily;
        _web1.MaintenanceStart = new TimeOnly(3, 0);
        _web1.MaintenanceDurationMinutes = 120;
        await _db.SaveChangesAsync();

        _now = new DateTime(2026, 11, 17, 2, 30, 0, DateTimeKind.Utc); // 03:30 Berlin
        await TickAsync();
        await TickAsync();

        var runs = await _db.RunbookRuns.ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(new DateTime(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc), run.NotAfter); // 03:00 + 4 h Berlin = 06:00 UTC
        Assert.Equal(AutoPatchScheduler.Actor, run.RequestedBy);

        // Host-Auto-Patch für web01 greift nicht, weil web01 zum geplanten Ablauf gehört.
        var autoPatch = await AutoPatchScheduler.RunOnceAsync(_db, Berlin, _now, CancellationToken.None);
        Assert.Equal(0, autoPatch.Created);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
