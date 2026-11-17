using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Pull-Agent: Der Server plant Aufträge, der Agent antwortet nur darauf. Module pro Host schaltbar.</summary>
public sealed class PullProtocolTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private readonly Notifier _notifier = new();
    private readonly ManagedHost _host;

    public PullProtocolTests()
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

    private async Task<AgentTask?> PollAsync(string? version = "0.1.0")
    {
        var task = await PullEndpoints.NextTaskAsync(_db, _host, _notifier, _features, _downloads, null, version, DateTime.UtcNow, CancellationToken.None);
        await _db.SaveChangesAsync();
        return task;
    }

    private async Task<int> AnswerAsync(AgentTask task, JobOutcome outcome, JsonElement? payload = null)
    {
        var audit = new AuditLog(_db, new HttpContextAccessor());
        var (status, _) = await PullEndpoints.ApplyResultAsync(_db, audit, _notifier, _features, _downloads, null, _host, task.TaskId,
            new TaskResultRequest(outcome, "ok", payload), NullLogger.Instance, CancellationToken.None);
        return status;
    }

    private static JsonElement Describe(params ModuleInfo[] modules) => PullJsonContext.ToElement(new SystemDescription(
        "0.1.0", "web1", OsFamily.Linux, "Debian GNU/Linux", "12", "6.1", TimeSpan.FromHours(5), false, false,
        [new NetworkAddress("eth0", "10.0.0.5", 24)], "arm64", modules), PullJsonContext.Default.SystemDescription);

    private static ModuleInfo Module(string id, bool available = true, bool local = false) => new(id, available, local, []);

    [Fact]
    public async Task FirstPoll_AsksForDescription_ThenOnlyUsableModules()
    {
        var first = await PollAsync();
        Assert.Equal((AgentModules.System, ModuleActions.Describe), (first!.Module, first.Action));

        Assert.Equal(200, await AnswerAsync(first, JobOutcome.Succeeded, Describe(
            Module(AgentModules.System), Module(AgentModules.Updates), Module(AgentModules.Software),
            Module(AgentModules.Docker, available: false), Module(AgentModules.Users, local: true), Module(AgentModules.Agent))));
        Assert.Equal("arm64", _host.Architecture);
        Assert.Equal("Debian GNU/Linux", _host.OsName);
        Assert.Single(await _db.IpAddresses.Where(a => a.HostId == _host.Id).ToListAsync());

        var scan = await PollAsync();
        Assert.Equal((AgentModules.Updates, ModuleActions.Scan), (scan!.Module, scan.Action));
        await AnswerAsync(scan, JobOutcome.Succeeded, PullJsonContext.ToElement(
            new UpdateScanResult([new AvailableUpdate("openssl", "3.0.1", "3.0.2", true, null)]), PullJsonContext.Default.UpdateScanResult));
        Assert.Equal(1, await _db.PendingUpdates.CountAsync(u => u.HostId == _host.Id));

        var software = await PollAsync();
        Assert.Equal((AgentModules.Software, ModuleActions.Inventory), (software!.Module, software.Action));
        await AnswerAsync(software, JobOutcome.Succeeded, PullJsonContext.ToElement(new SoftwareList([]), PullJsonContext.Default.SoftwareList));

        // docker fehlt, users lokal gesperrt, Compliance-Schalter aus, kein Update-Binary: nichts mehr zu tun.
        Assert.Null(await PollAsync());
    }

    [Fact]
    public async Task VersionChange_TriggersDescribe()
    {
        var first = await PollAsync();
        await AnswerAsync(first!, JobOutcome.Succeeded, Describe(Module(AgentModules.System), Module(AgentModules.Agent)));
        Assert.Null(await PollAsync("0.1.0"));

        // Neue Agent-Version (z. B. nach Self-Update): sobald die letzte Beschreibung eine Minute alt ist, neu beschreiben.
        var last = await _db.AgentTasks.SingleAsync();
        last.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        await _db.SaveChangesAsync();
        Assert.Equal(ModuleActions.Describe, (await PollAsync("0.2.0"))!.Action);
    }

    [Fact]
    public async Task AgentRestart_TriggersDescribe_UptimeFromBootTime()
    {
        PullEndpoints.NoteInstance(_host, "a1", DateTime.UtcNow);
        var first = await PollAsync();
        await AnswerAsync(first!, JobOutcome.Succeeded, Describe(Module(AgentModules.System), Module(AgentModules.Agent)));
        Assert.Null(await PollAsync());
        Assert.InRange(_host.BootedAt!.Value, DateTime.UtcNow.AddHours(-5).AddMinutes(-1), DateTime.UtcNow.AddHours(-5).AddMinutes(1));

        // gleicher Prozess: nichts Neues; neuer Prozess (Reboot): sofort neu beschreiben, danach wieder Ruhe
        PullEndpoints.NoteInstance(_host, "a1", DateTime.UtcNow);
        Assert.Null(await PollAsync());
        PullEndpoints.NoteInstance(_host, "b2", DateTime.UtcNow);
        var again = await PollAsync();
        Assert.Equal(ModuleActions.Describe, again!.Action);
        await AnswerAsync(again, JobOutcome.Succeeded, Describe(Module(AgentModules.System), Module(AgentModules.Agent)));
        Assert.Null(await PollAsync());
    }

    [Fact]
    public async Task Jobs_BecomeModuleTasks_DisabledModuleRejectsJob()
    {
        var first = await PollAsync();
        await AnswerAsync(first!, JobOutcome.Succeeded, Describe(Module(AgentModules.System), Module(AgentModules.Updates), Module(AgentModules.Software), Module(AgentModules.Agent)));

        var patch = new PatchJob { HostId = _host.Id, Type = JobType.Patch, Scope = PatchScope.All, AllowReboot = true, RequestedBy = "admin:test" };
        _db.Jobs.Add(patch);
        await _db.SaveChangesAsync();

        var task = await PollAsync();
        Assert.Equal((AgentModules.Updates, ModuleActions.Install), (task!.Module, task.Action));
        var install = PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.InstallRequest);
        Assert.Equal(PatchScope.All, install!.Scope);
        Assert.Equal(JobStatus.Running, patch.Status);

        await AnswerAsync(task, JobOutcome.Succeeded);
        Assert.Equal(JobStatus.Succeeded, patch.Status);

        // Nach der Installation gleich neu scannen.
        Assert.Equal(ModuleActions.Scan, (await PollAsync())!.Action);

        _host.DisabledModules = AgentModules.Updates;
        var app = new PatchJob { HostId = _host.Id, Type = JobType.Scan, RequestedBy = "admin:test" };
        _db.Jobs.Add(app);
        await _db.SaveChangesAsync();
        await PollAsync();
        Assert.Equal(JobStatus.Rejected, app.Status);
        Assert.Contains("updates", app.Summary);
    }

    [Fact]
    public async Task UnansweredTask_IsMarkedLost_LateAnswerStillCounts()
    {
        var first = await PollAsync();
        var second = await PollAsync(); // ohne "running": der erste gilt als verloren
        var lost = await _db.AgentTasks.SingleAsync(t => t.Id == first!.TaskId);
        Assert.Equal(JobStatus.Cancelled, lost.Status);
        Assert.Equal(ModuleActions.Describe, second!.Action); // verlorene Anforderung wird wiederholt
        Assert.Equal(200, await AnswerAsync(first!, JobOutcome.Succeeded, Describe(Module(AgentModules.System))));
        Assert.Equal(404, await AnswerAsync(new AgentTask(Guid.NewGuid(), "system", "describe"), JobOutcome.Succeeded));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ComplianceModules_OnlyWithFeature()
    {
        var first = await PollAsync();
        await AnswerAsync(first!, JobOutcome.Succeeded, Describe(Module(AgentModules.System), Module(AgentModules.Firewall), Module(AgentModules.Agent)));
        Assert.Null(await PollAsync());

        await _features.SetAsync(_db, Feature.ComplianceChecks, true, "test", CancellationToken.None);
        var firewall = await PollAsync();
        Assert.Equal((AgentModules.Firewall, ModuleActions.Status), (firewall!.Module, firewall.Action));
        await AnswerAsync(firewall, JobOutcome.Succeeded, PullJsonContext.ToElement(
            new FirewallStatus("ufw", true, true, "deny", "allow", [], 0, null, false), PullJsonContext.Default.FirewallStatus));
        var stored = await _db.HostCompliance.SingleAsync();
        Assert.Equal("ufw", stored.FirewallProduct);
        Assert.True(stored.FirewallEnabled);
    }

    [Fact]
    public async Task LiteAgent_GetsOnlyWhatItReports()
    {
        // Lite-Agent (alte Systeme): system, updates, agent nur mit "renew".
        var first = await PollAsync("1.0-lite");
        await AnswerAsync(first!, JobOutcome.Succeeded, Describe(
            new ModuleInfo(AgentModules.System, true, false, [ModuleActions.Describe]),
            new ModuleInfo(AgentModules.Updates, true, false, [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot]),
            new ModuleInfo(AgentModules.Agent, true, false, [ModuleActions.Renew])));

        Assert.Equal(ModuleActions.Scan, (await PollAsync("1.0-lite"))!.Action);

        var app = new PatchJob { HostId = _host.Id, Type = JobType.AppUpdate, Packages = "7zip.7zip", RequestedBy = "admin:test" };
        await _features.SetAsync(_db, Feature.AppUpdates, true, "test", CancellationToken.None);
        _db.Jobs.Add(app);
        await _db.SaveChangesAsync();
        await PollAsync("1.0-lite");
        Assert.Equal(JobStatus.Rejected, app.Status); // kein Modul "software"

        Assert.True(Server.Services.Modules.ModuleJson.Supports(_host, AgentModules.Agent, ModuleActions.Renew));
        Assert.False(Server.Services.Modules.ModuleJson.Supports(_host, AgentModules.Agent, ModuleActions.Update)); // kein Self-Update-Angebot
    }

    // --- Agent ----------------------------------------------------------------------------------------

    private sealed class EchoModule(string id) : IAgentModule
    {
        public string Id => id;
        public bool Available => true;
        public IReadOnlyList<string> Actions { get; } = ["status"];
        public Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct) =>
            Task.FromResult(new TaskOutcome(JobOutcome.Succeeded, "done"));
    }

    [Fact]
    public async Task AgentModuleHost_EnforcesLocalPolicyAndKnownActions()
    {
        var host = new ModuleHost([new EchoModule(AgentModules.Firewall), new EchoModule(AgentModules.System)]);
        var context = new TaskContext(new AgentPolicy(DisabledModules: ["firewall", "system"]), _ => { });

        Assert.Equal(JobOutcome.Rejected, (await host.ExecuteAsync(new AgentTask(Guid.NewGuid(), "firewall", "status"), context, CancellationToken.None)).Outcome);
        Assert.Equal(JobOutcome.Succeeded, (await host.ExecuteAsync(new AgentTask(Guid.NewGuid(), "system", "status"), context, CancellationToken.None)).Outcome); // system nicht sperrbar
        Assert.Equal(JobOutcome.Rejected, (await host.ExecuteAsync(new AgentTask(Guid.NewGuid(), "system", "rm -rf"), context, CancellationToken.None)).Outcome);
        Assert.Equal(JobOutcome.Rejected, (await host.ExecuteAsync(new AgentTask(Guid.NewGuid(), "shell", "status"), context, CancellationToken.None)).Outcome);

        var described = host.Describe(context.Policy).ToDictionary(m => m.Id);
        Assert.True(described["firewall"].DisabledLocally);
        Assert.False(described["system"].DisabledLocally);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
