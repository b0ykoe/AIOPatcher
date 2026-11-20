using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Erst die VMs, dann der Hypervisor: in derselben Gruppe und über Pläne hinweg.</summary>
public sealed class HypervisorOrderTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _pve, _vm, _nas;
    private DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    public HypervisorOrderTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        var site = new Site { Name = "Homelab" };
        var proxmox = new Integration { Name = "pve", Type = IntegrationType.Proxmox, ProtectedSecret = "x" };
        ManagedHost Host(string name) => new() { Site = site, Name = name, Hostname = name, CertificateThumbprint = Guid.NewGuid().ToString("N") };
        _pve = Host("pve01.lan");
        _pve.OsName = "Proxmox VE 9.1";
        _vm = Host("docker");
        _vm.Integration = proxmox;
        _vm.ExternalId = "pve01/qemu/101";
        _nas = Host("nas"); // ohne Zuordnung
        _db.Hosts.AddRange(_pve, _vm, _nas);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Runbook Plan(string name, params ManagedHost[] hosts)
    {
        var runbook = new Runbook
        {
            Name = name,
            Waves = [new RunbookWave { Order = 1, Name = "All", Scope = PatchScope.All, AllowReboot = true, Hosts = hosts.Select(h => new RunbookWaveHost { Host = h }).ToList() }],
        };
        _db.Runbooks.Add(runbook);
        _db.SaveChanges();
        return runbook;
    }

    private async Task<RunbookRun> StartAsync(Runbook runbook)
    {
        var run = await RunbookEngine.StartRunAsync(_db, runbook.Id, "admin:test", _now, null, CancellationToken.None);
        await _db.SaveChangesAsync();
        return run;
    }

    private async Task TickAsync()
    {
        _now += TimeSpan.FromSeconds(10);
        await RunbookEngine.TickAsync(_db, Berlin, _now, CancellationToken.None);
    }

    private Task<bool> HasJob(ManagedHost host) => _db.Jobs.AnyAsync(j => j.HostId == host.Id);

    private async Task FinishAsync(ManagedHost host)
    {
        var job = await _db.Jobs.Where(j => j.HostId == host.Id).OrderByDescending(j => j.CreatedAt).FirstAsync();
        job.Status = JobStatus.Succeeded;
        job.FinishedAt = _now;
        host.LastSeenAt = _now.AddSeconds(5);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public void RecognizesHypervisorAndGuests()
    {
        Assert.True(RunbookEngine.IsHypervisor(_pve));
        Assert.Equal("pve01", RunbookEngine.GuestNode(_vm));
        Assert.True(RunbookEngine.RunsOn(_vm, _pve)); // "pve01.lan" ist Knoten "pve01"
        Assert.False(RunbookEngine.RunsOn(_nas, _pve));
    }

    [Fact]
    public async Task SameGroup_HypervisorGoesLast()
    {
        var run = await StartAsync(Plan("Patchday", _pve, _vm, _nas));
        await TickAsync();

        Assert.True(await HasJob(_vm));
        Assert.True(await HasJob(_nas));
        Assert.False(await HasJob(_pve));
        var item = run.Items.Single(i => i.HostId == _pve.Id);
        Assert.Equal(RunItemStatus.Pending, item.Status);
        Assert.Contains("hypervisor goes last", item.Message);

        await FinishAsync(_vm);
        await FinishAsync(_nas);
        await TickAsync(); // prüfen: beide zurück
        await TickAsync();
        Assert.True(await HasJob(_pve));
    }

    [Fact]
    public async Task OtherPlan_HypervisorWaitsForBusyGuests_AndGuestsWaitForTheHypervisor()
    {
        // VM wird von einem anderen Plan gerade gepatcht
        var vmRun = await StartAsync(Plan("VMs", _vm));
        await TickAsync();
        Assert.True(await HasJob(_vm));

        var pveRun = await StartAsync(Plan("Hypervisor", _pve));
        await TickAsync();
        Assert.False(await HasJob(_pve));
        Assert.Contains("1 VM(s) on this hypervisor", pveRun.Items.Single().Message);

        await FinishAsync(_vm);
        await TickAsync();
        Assert.True(await HasJob(_pve));

        await TickAsync();
        await TickAsync();
        Assert.Equal(RunStatus.Succeeded, vmRun.Status);
    }

    [Fact]
    public async Task GuestWaits_WhileItsHypervisorIsBeingPatched()
    {
        _db.Jobs.Add(new PatchJob { HostId = _pve.Id, Type = JobType.Patch, Status = JobStatus.Running, RequestedBy = "admin:test", CreatedAt = _now });
        await _db.SaveChangesAsync();

        var run = await StartAsync(Plan("VMs", _vm, _nas));
        await TickAsync();
        Assert.False(await HasJob(_vm));
        Assert.True(await HasJob(_nas)); // ohne Zuordnung: wartet nicht
        Assert.Contains("hypervisor is being patched", run.Items.Single(i => i.HostId == _vm.Id).Message);
    }
}
