using System.Text.Json;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Dienste/Container vor und nach einem Neustart vergleichen.</summary>
public sealed class RebootCheckTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private readonly Notifier _notifier = new();
    private readonly ManagedHost _host;

    public RebootCheckTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "web1", Hostname = "web1", CertificateThumbprint = "x",
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

    // --- Agent: Ausgaben lesen -----------------------------------------------------------------------

    [Fact]
    public void Systemd_OnlyEnabledServices_TemplatesCount()
    {
        const string running = """
            ssh.service                 loaded active running OpenBSD Secure Shell server
            ● nginx.service             loaded active running A high performance web server
            getty@tty1.service          loaded active running Getty on tty1
            packagekit.service          loaded active running PackageKit Daemon
            user@1000.service           loaded active running User Manager for UID 1000
            """;
        var enabled = RunningServicesCollector.FirstColumn("ssh.service enabled enabled\nnginx.service enabled enabled\ngetty@.service enabled enabled\n").ToHashSet();

        var units = RunningServicesCollector.FirstColumn(running).Where(u => RunningServicesCollector.IsEnabled(u, enabled)).ToList();

        Assert.Equal(["ssh.service", "nginx.service", "getty@tty1.service"], units);
    }

    [Fact]
    public void Windows_ScNames_AndWhichServicesCount()
    {
        const string sc = "\r\nSERVICE_NAME: Dnscache\r\nDISPLAY_NAME: DNS-Client\r\n        TYPE               : 20  WIN32_SHARE_PROCESS\r\n\r\nSERVICE_NAME: MSSQLSERVER\r\n";
        Assert.Equal(["Dnscache", "MSSQLSERVER"], RunningServicesCollector.ParseScNames(sc));

        Assert.True(RunningServicesCollector.IsWatchedWindowsService(start: 2, type: 0x10, delayed: false, hasTriggers: false));
        Assert.False(RunningServicesCollector.IsWatchedWindowsService(2, 0x10, delayed: true, hasTriggers: false)); // verzögert
        Assert.False(RunningServicesCollector.IsWatchedWindowsService(2, 0x20, false, hasTriggers: true)); // Trigger-Start
        Assert.False(RunningServicesCollector.IsWatchedWindowsService(3, 0x10, false, false)); // manuell
        Assert.False(RunningServicesCollector.IsWatchedWindowsService(2, 0x60, false, false)); // Benutzerdienst-Vorlage
        Assert.False(RunningServicesCollector.IsWatchedWindowsService(2, 0x01, false, false)); // Treiber
    }

    [Fact]
    public void Docker_RestartPolicy()
    {
        var (running, noRestart) = RunningServicesCollector.ParseContainers("/web|no\n/db|unless-stopped\n/cache|\n");
        Assert.Equal(["cache", "db", "web"], running);
        Assert.Equal(["cache", "web"], noRestart);
    }

    // --- Server: vergleichen -------------------------------------------------------------------------

    [Fact]
    public void Apply_ComparesOnlyAfterNewBoot_TracksRecovery()
    {
        var now = DateTime.UtcNow;
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromHours(5), ["ssh.service", "nginx.service"], ["db", "web"], [], ["web"]), now, null);
        Assert.Null(_host.RebootCheck);
        Assert.Equal(2, RebootCheck.Snapshot(_host)!.Services.Count);

        // gleicher Start, später: kein Vergleich
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(30)), ["ssh.service"], ["db"]), now.AddMinutes(30), null);
        Assert.Null(_host.RebootCheck);

        // gerade erst gestartet: ignorieren (Grundstand bleibt)
        var before = _host.RunningSnapshot;
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromMinutes(1), [], []), now.AddHours(1), null);
        Assert.Equal(before, _host.RunningSnapshot);

        // nach dem Neustart: nginx fehlgeschlagen; Grundstand war der letzte (ssh, db)
        _host.RunningSnapshot = null;
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromHours(5), ["ssh.service", "nginx.service"], ["db", "web"], [], ["web"]), now, null);
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromMinutes(6), ["ssh.service"], ["db"], ["nginx.service"]), now.AddHours(1), null);
        var check = RebootCheck.Result(_host)!;
        Assert.Equal([new RebootCheckItem("service", "nginx.service", "stopped"), new RebootCheckItem("container", "web", "noRestart")], check.Missing);
        Assert.Equal((2, 2), (check.ServicesBefore, check.ContainersBefore));

        // nginx kommt später doch noch: nicht mehr offen
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromMinutes(40), ["ssh.service", "nginx.service"], ["db"]), now.AddHours(1).AddMinutes(34), null);
        check = RebootCheck.Result(_host)!;
        Assert.Equal(["web"], check.Missing.Select(m => m.Name));
        Assert.Equal(["nginx.service"], check.Recovered.Select(m => m.Name));
    }

    [Fact]
    public void Apply_DockerUnreachableAfterBoot_NoContainerFindings()
    {
        var now = DateTime.UtcNow;
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromHours(5), ["ssh.service"], ["db", "web"]), now, null);
        RebootCheck.Apply(_host, new RunningServices(TimeSpan.FromMinutes(6), ["ssh.service"], null), now.AddHours(1), null);
        Assert.Empty(RebootCheck.Result(_host)!.Missing);
    }

    // --- Ablauf über das Pull-Protokoll --------------------------------------------------------------

    [Fact]
    public async Task Flow_SnapshotBeforeRebootJob_CompareFiveMinutesAfterBoot()
    {
        PullEndpoints.NoteInstance(_host, "before-reboot", DateTime.UtcNow);
        await AnswerAsync(await NextAsync(AgentModules.System), Describe(TimeSpan.FromHours(5)));
        await AnswerAsync(await NextAsync(AgentModules.Services), Running(TimeSpan.FromHours(5), ["ssh.service", "nginx.service"], ["db"]));
        Assert.Null(await PollAsync());

        // Neustart-Job: vorher den Stand frisch holen, wenn der letzte älter als 10 Minuten ist
        await AgeAsync(AgentModules.Services, TimeSpan.FromMinutes(20));
        _db.Jobs.Add(new PatchJob { HostId = _host.Id, Type = JobType.Reboot, RequestedBy = "admin:test" });
        await _db.SaveChangesAsync();
        var snapshot = await PollAsync();
        Assert.Equal((AgentModules.Services, ModuleActions.List), (snapshot!.Module, snapshot.Action));
        await AnswerAsync(snapshot, Running(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(20)), ["ssh.service", "nginx.service"], ["db"]));
        var reboot = await PollAsync();
        Assert.Equal((AgentModules.Updates, ModuleActions.Reboot), (reboot!.Module, reboot.Action));
        await AnswerAsync(reboot, null);

        // Agent nach dem Neustart: erst beschreiben; 1 Minute nach dem Start noch nicht vergleichen
        PullEndpoints.NoteInstance(_host, "after-reboot", DateTime.UtcNow);
        await AnswerAsync(await NextAsync(AgentModules.System), Describe(TimeSpan.FromMinutes(1)));
        Assert.Null(await NextOrNullAsync(AgentModules.Services));

        // 20 Minuten später, 6 Minuten nach dem Start (Beschreibung neu): vergleichen, nginx fehlt
        await AgeAsync(null, TimeSpan.FromMinutes(20));
        _host.AgentRestartedAt -= TimeSpan.FromMinutes(20);
        await AnswerAsync(await NextAsync(AgentModules.System), Describe(TimeSpan.FromMinutes(6)));
        await AnswerAsync(await NextAsync(AgentModules.Services), Running(TimeSpan.FromMinutes(6), ["ssh.service"], ["db"]));
        Assert.Equal(["nginx.service"], RebootCheck.Result(_host)!.Missing.Select(m => m.Name));
        Assert.Null(await NextOrNullAsync(AgentModules.Services)); // nicht noch einmal
    }

    private async Task<AgentTask?> PollAsync()
    {
        var task = await PullEndpoints.NextTaskAsync(_db, _host, _notifier, _features, _downloads, null, "0.1.0", DateTime.UtcNow, CancellationToken.None);
        await _db.SaveChangesAsync();
        return task;
    }

    /// <summary>Pollen, bis ein Auftrag für dieses Modul kommt; andere (Scan usw.) leer beantworten.</summary>
    private async Task<AgentTask?> NextOrNullAsync(string module)
    {
        for (var i = 0; i < 10; i++)
        {
            var task = await PollAsync();
            if (task is null || task.Module == module)
            {
                return task;
            }

            await AnswerAsync(task, null);
        }

        throw new InvalidOperationException("too many tasks");
    }

    private async Task<AgentTask> NextAsync(string module) => await NextOrNullAsync(module) ?? throw new InvalidOperationException($"no {module} task");

    private async Task AnswerAsync(AgentTask task, JsonElement? payload)
    {
        var audit = new AuditLog(_db, new HttpContextAccessor());
        var (status, _) = await PullEndpoints.ApplyResultAsync(_db, audit, _notifier, _features, _downloads, null, _host, task.TaskId,
            new TaskResultRequest(JobOutcome.Succeeded, "ok", payload), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(200, status);
    }

    /// <summary>Zeit vergehen lassen: Aufträge (eines Moduls oder alle) älter machen.</summary>
    private async Task AgeAsync(string? module, TimeSpan by)
    {
        foreach (var t in await _db.AgentTasks.Where(t => t.HostId == _host.Id && (module == null || t.Module == module)).ToListAsync())
        {
            t.CreatedAt -= by;
        }

        await _db.SaveChangesAsync();
    }

    private static JsonElement Describe(TimeSpan uptime) => PullJsonContext.ToElement(new SystemDescription(
        "0.1.0", "web1", OsFamily.Linux, "Debian GNU/Linux", "12", "6.1", uptime, false, false, [], "x64",
        [new(AgentModules.System, true, false, []), new(AgentModules.Updates, true, false, []), new(AgentModules.Services, true, false, []), new(AgentModules.Agent, true, false, [])]),
        PullJsonContext.Default.SystemDescription);

    private static JsonElement Running(TimeSpan uptime, List<string> services, List<string> containers) =>
        PullJsonContext.ToElement(new RunningServices(uptime, services, containers), PullJsonContext.Default.RunningServices);
}
