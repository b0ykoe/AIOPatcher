using System.Text.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Auslastung: Messwerte lesen, speichern, verdichten; Proxmox-Ressourcen.</summary>
public sealed class MetricsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private readonly Notifier _notifier = new();
    private readonly ManagedHost _host;

    public MetricsTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "pve", Hostname = "pve", CertificateThumbprint = "x",
            CertificateExpiresAt = DateTime.UtcNow.AddYears(2), OsFamily = OsFamily.Linux,
        };
        _db.Hosts.Add(_host);
        _db.SaveChanges();
        _features.LoadAsync(_db, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Cpu_FromProcStatDeltas()
    {
        var before = MetricsCollector.ParseProcStat("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n")!.Value;
        var after = MetricsCollector.ParseProcStat("cpu  200 0 200 1300 100 0 0 0 0 0\n")!.Value;
        Assert.Equal((200UL, 1000UL), before); // Leerlauf = idle + iowait
        Assert.Equal(25, MetricsCollector.CpuPercent(before, after), 1);
        Assert.Equal(0, MetricsCollector.CpuPercent(after, before)); // Zähler zurückgesetzt
    }

    [Fact]
    public void Memory_AvailableCountsAsFree()
    {
        var (total, used, swapTotal, swapUsed) = MetricsCollector.ParseMeminfo(
            "MemTotal:        8000000 kB\nMemFree:          500000 kB\nMemAvailable:    6000000 kB\nSwapTotal:       1000000 kB\nSwapFree:         750000 kB\n");
        Assert.Equal(8000000L * 1024, total);
        Assert.Equal(2000000L * 1024, used);
        Assert.Equal((1000000L * 1024, 250000L * 1024), (swapTotal!.Value, swapUsed!.Value));
    }

    [Fact]
    public void Disks_RealFileSystemsOnce_WithoutGuestVolumes()
    {
        const string mounts = """
            /dev/mapper/pve-root / ext4 rw,relatime 0 0
            proc /proc proc rw 0 0
            tmpfs /run tmpfs rw 0 0
            /dev/sda2 /boot/efi vfat rw 0 0
            rpool/data/subvol-101-disk-0 /rpool/data/subvol-101-disk-0 zfs rw 0 0
            overlay /var/lib/docker/overlay2/abc/merged overlay rw 0 0
            /dev/mapper/pve-root /srv/bind ext4 rw 0 0
            //nas/backup /mnt/nas\040share cifs rw 0 0
            """;
        var disks = MetricsCollector.LinuxDisks(mounts, _ => (1000, 250));
        Assert.Equal(["/", "/boot/efi", "/mnt/nas share"], disks.Select(d => d.Mount));
        Assert.Equal(750, disks[0].Used);
    }

    [Fact]
    public void DockerStats_Parsed()
    {
        var stats = MetricsCollector.ParseDockerStats("web|1.26%|12.5MiB / 1.94GiB\ndb|0.00%|0B / 0B\nbroken\n");
        Assert.Equal(["db", "web"], stats.Select(s => s.Name));
        Assert.Equal(1.3, stats[1].CpuPercent, 1);
        Assert.Equal((long)(12.5 * 1024 * 1024), stats[1].MemoryUsed);
        Assert.Equal((long)(1.94 * 1024 * 1024 * 1024), stats[1].MemoryLimit);
        Assert.Equal(512_000, MetricsCollector.ParseSize("512kB"));
        Assert.Null(MetricsCollector.ParseSize("abc"));
    }

    [Fact]
    public void Proxmox_Resources_GuestsNodesStorage()
    {
        var data = JsonNode.Parse("""
            [
              {"id":"node/pve","type":"node","node":"pve","status":"online","cpu":0.12,"maxcpu":8,"mem":4000,"maxmem":16000,"disk":10,"maxdisk":100,"uptime":3600},
              {"id":"qemu/100","type":"qemu","node":"pve","vmid":100,"name":"win11","status":"running","cpu":0.5,"maxcpu":4,"mem":2000,"maxmem":8000,"disk":0,"maxdisk":64000},
              {"id":"lxc/101","type":"lxc","node":"pve","vmid":101,"name":"portainer","status":"running","cpu":0.02,"maxcpu":2,"mem":500,"maxmem":2000,"disk":3000,"maxdisk":8000},
              {"id":"qemu/9000","type":"qemu","node":"pve","vmid":9000,"name":"tpl","template":1},
              {"id":"storage/pve/local","type":"storage","node":"pve","storage":"local","disk":50,"maxdisk":100,"status":"available"},
              {"id":"sdn/pve/localnetwork","type":"sdn","node":"pve"}
            ]
            """)!.AsArray();
        var resources = ProxmoxProvider.ParseResources(data);
        Assert.Equal(["pve", "portainer", "win11", "local"], resources.Select(r => r.Name));
        var lxc = resources.Single(r => r.Type == "lxc");
        Assert.Equal(("pve/lxc/101", 101, 2), (lxc.ExternalId, lxc.VmId!.Value, lxc.MaxCpu!.Value));
        Assert.Null(resources.Single(r => r.Type == "node").ExternalId);
    }

    [Fact]
    public void Downsample_AveragesBuckets_DiskMax()
    {
        var from = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 12).Select(i => new HostMetricSample { At = from.AddMinutes(5 * i), Cpu = i, Memory = 50, Disk = i == 3 ? 90 : 10 }).ToList();
        var points = MetricsEndpoints.Downsample(samples, from, from.AddHours(1), 2);
        Assert.Equal(2, points.Count);
        Assert.Equal(2.5f, points[0].Cpu);
        Assert.Equal(90, points[0].Disk);
        Assert.Equal(10, points[1].Disk);
    }

    [Fact]
    public async Task Sample_EveryFiveMinutes_StoredWithHistory()
    {
        await AnswerAsync((await PollAsync())!, PullJsonContext.ToElement(new SystemDescription(
            "0.1.0", "pve", OsFamily.Linux, "Proxmox VE", "9", "6.8", TimeSpan.FromHours(5), false, false, [], "x64",
            [new(AgentModules.System, true, false, []), new(AgentModules.Metrics, true, false, []), new(AgentModules.Agent, true, false, [])]),
            PullJsonContext.Default.SystemDescription));

        var sample = await PollAsync();
        Assert.Equal((AgentModules.Metrics, ModuleActions.Sample), (sample!.Module, sample.Action));
        await AnswerAsync(sample, PullJsonContext.ToElement(new HostMetrics(TimeSpan.FromHours(5), 12.5, 8, 8000, 2000, null, null, [0.5, 0.4, 0.3],
            [new DiskUsage("/", "ext4", 100, 40), new DiskUsage("/data", "xfs", 100, 85)], [new ContainerUsage("web", 1, 10, 100)]), PullJsonContext.Default.HostMetrics));

        var stored = await _db.MetricSamples.SingleAsync();
        Assert.Equal((12.5f, 25f, 85f), (stored.Cpu, stored.Memory, stored.Disk!.Value));
        Assert.NotNull(_host.MetricsAt);
        Assert.Single(JsonSerializer.Deserialize(_host.Metrics!, PullJsonContext.Default.HostMetrics)!.Containers!);
        Assert.Null(await PollAsync()); // nicht vor Ablauf von 5 Minuten
    }

    [Fact]
    public async Task Containers_ReportedAgainOnRequestAndAfterScan()
    {
        await AnswerAsync((await PollAsync())!, PullJsonContext.ToElement(new SystemDescription(
            "0.1.0", "docker1", OsFamily.Linux, "Debian", "13", "6.12", TimeSpan.FromHours(5), false, false, [], "x64",
            [new(AgentModules.System, true, false, []), new(AgentModules.Docker, true, false, []), new(AgentModules.Agent, true, false, [])]),
            PullJsonContext.Default.SystemDescription));
        var inventory = await PollAsync();
        Assert.Equal((AgentModules.Docker, ModuleActions.Inventory), (inventory!.Module, inventory.Action));
        await AnswerAsync(inventory, PullJsonContext.ToElement(new ContainerList([]), PullJsonContext.Default.ContainerList));
        Assert.Null(await PollAsync());

        // "Neu prüfen" im Dashboard
        _host.ContainersRequestedAt = DateTime.UtcNow.AddSeconds(1);
        await _db.SaveChangesAsync();
        Assert.Equal(AgentModules.Docker, (await PollAsync())!.Module);
    }

    private async Task<AgentTask?> PollAsync()
    {
        var task = await PullEndpoints.NextTaskAsync(_db, _host, _notifier, _features, _downloads, null, "0.1.0", DateTime.UtcNow, CancellationToken.None);
        await _db.SaveChangesAsync();
        return task;
    }

    private async Task AnswerAsync(AgentTask task, JsonElement? payload)
    {
        var audit = new AuditLog(_db, new HttpContextAccessor());
        var (status, _) = await PullEndpoints.ApplyResultAsync(_db, audit, _notifier, _features, _downloads, null, _host, task.TaskId,
            new TaskResultRequest(JobOutcome.Succeeded, "ok", payload), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(200, status);
    }
}
