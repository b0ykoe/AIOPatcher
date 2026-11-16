using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class RunbookPreparationTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _vm;
    private readonly Runbook _runbook;
    private readonly FakePreparation _prep = new();
    private DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakePreparation : IRunbookPreparation
    {
        public DateTime? LastBackup { get; set; }
        public ProviderTaskState NextState { get; set; } = ProviderTaskState.Running;
        public List<string> Snapshots { get; } = [];
        public int Prunes { get; private set; }

        public Task<DateTime?> LastBackupAsync(ManagedHost host, CancellationToken ct) => Task.FromResult(LastBackup);

        public Task<string> StartSnapshotAsync(ManagedHost host, string name, CancellationToken ct)
        {
            Snapshots.Add(name);
            return Task.FromResult("UPID:pve:1");
        }

        public Task<ProviderTaskStatus> GetTaskAsync(ManagedHost host, string taskRef, CancellationToken ct) =>
            Task.FromResult(new ProviderTaskStatus(NextState, NextState == ProviderTaskState.Failed ? "VM is locked" : null));

        public Task PruneSnapshotsAsync(ManagedHost host, CancellationToken ct)
        {
            Prunes++;
            return Task.CompletedTask;
        }
    }

    public RunbookPreparationTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        var site = new Site { Name = "Homelab" };
        var pve = new Integration { Name = "pve", Type = IntegrationType.Proxmox, ProtectedSecret = "x", BaseUrl = "https://pve:8006" };
        _vm = new ManagedHost { Site = site, Name = "web01", Hostname = "web01", CertificateThumbprint = "a", Integration = pve, ExternalId = "pve/qemu/101" };
        _db.Hosts.Add(_vm);
        _runbook = new Runbook
        {
            Name = "Mit Snapshot",
            Waves = [new RunbookWave { Order = 1, Name = "VMs", SnapshotBeforePatch = true, RequireBackupWithinHours = 24, Hosts = [new() { Host = _vm }] }],
        };
        _db.Runbooks.Add(_runbook);
        _db.SaveChanges();
    }

    private async Task<RunbookRunItem> StartAndTickAsync()
    {
        await RunbookEngine.StartRunAsync(_db, _runbook.Id, "admin:test", _now, null, CancellationToken.None);
        await _db.SaveChangesAsync();
        await TickAsync();
        return await _db.RunbookRunItems.SingleAsync();
    }

    private async Task TickAsync()
    {
        _now += TimeSpan.FromSeconds(10);
        await RunbookEngine.TickAsync(_db, Berlin, _now, CancellationToken.None, preparation: _prep);
    }

    [Fact]
    public async Task Snapshot_RunsBeforePatchJob()
    {
        _prep.LastBackup = _now.AddHours(-3);
        var item = await StartAndTickAsync();

        Assert.Equal(RunItemStatus.Preparing, item.Status);
        Assert.Single(_prep.Snapshots);
        Assert.StartsWith(SnapshotNames.Prefix, item.SnapshotName);
        Assert.Empty(_db.Jobs); // noch kein Patch, solange der Snapshot läuft

        _prep.NextState = ProviderTaskState.Succeeded;
        await TickAsync(); // Snapshot fertig, Job wird angelegt
        await _db.Entry(item).ReloadAsync();

        Assert.Equal(RunItemStatus.Running, item.Status);
        Assert.NotNull(item.SnapshotTakenAt);
        Assert.Equal(1, _prep.Prunes);
        var job = await _db.Jobs.SingleAsync();
        Assert.Equal(JobType.Patch, job.Type);
    }

    [Fact]
    public async Task Cancel_WithdrawsQueuedJob_AndClosesItems()
    {
        _prep.LastBackup = _now.AddHours(-3);
        _prep.NextState = ProviderTaskState.Succeeded;
        var item = await StartAndTickAsync(); // Snapshot gestartet
        await TickAsync();                    // fertig, Job geplant
        var run = await _db.RunbookRuns.Include(r => r.Items).SingleAsync();

        Assert.Null(await RunbookEngine.CancelAsync(_db, run, _now, CancellationToken.None));
        await _db.SaveChangesAsync();

        Assert.Equal(RunItemStatus.Skipped, item.Status);
        Assert.Equal(JobStatus.Cancelled, (await _db.Jobs.SingleAsync()).Status);
        Assert.Equal(RunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task TooOldBackup_FailsHost_WithoutSnapshot()
    {
        _prep.LastBackup = _now.AddHours(-30);
        var item = await StartAndTickAsync();

        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Contains("older than 24 h", item.Message);
        Assert.Empty(_prep.Snapshots);
        Assert.Empty(_db.Jobs);
    }

    [Fact]
    public async Task FailedSnapshot_FailsHost_AndPausesRun()
    {
        _prep.LastBackup = _now.AddHours(-1);
        var item = await StartAndTickAsync();
        _prep.NextState = ProviderTaskState.Failed;
        await TickAsync();
        await _db.Entry(item).ReloadAsync();

        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Contains("VM is locked", item.Message);
        Assert.Empty(_db.Jobs);
        var run = await _db.RunbookRuns.SingleAsync();
        Assert.Equal(RunStatus.Paused, run.Status);
    }

    [Fact]
    public async Task HostWithoutMapping_Fails()
    {
        _vm.IntegrationId = null;
        _vm.Integration = null;
        _vm.ExternalId = null;
        await _db.SaveChangesAsync();
        var real = new RunbookPreparation(new VirtualizationProviderFactory(null!), Microsoft.Extensions.Logging.Abstractions.NullLogger<RunbookPreparation>.Instance);

        await RunbookEngine.StartRunAsync(_db, _runbook.Id, "admin:test", _now, null, CancellationToken.None);
        await _db.SaveChangesAsync();
        await RunbookEngine.TickAsync(_db, Berlin, _now.AddSeconds(10), CancellationToken.None, preparation: real);
        var item = await _db.RunbookRunItems.SingleAsync();

        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Contains("No Proxmox/Hetzner mapping", item.Message);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
