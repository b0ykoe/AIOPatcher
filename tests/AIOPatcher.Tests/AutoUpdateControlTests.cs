using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Platforms;
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

/// <summary>Eigene Update-Automatik der Hosts (unattended-upgrades, Windows Update) aus dem Dashboard schalten.</summary>
public sealed class AutoUpdateControlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private readonly Notifier _notifier = new();
    private readonly ManagedHost _host;

    public AutoUpdateControlTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "api", Hostname = "api", CertificateThumbprint = "x",
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
    public void Policy_OwnSwitch_IndependentOfSystemChanges()
    {
        var off = new AgentJob(Guid.NewGuid(), JobType.SystemChange, PatchScope.Security, false, Change: new SystemChange(SystemChangeKind.AutoUpdatesOff));

        Assert.Null(JobExecutor.CheckPolicy(off, new AgentPolicy())); // ab Werk erlaubt, obwohl allowSystemChanges aus ist
        Assert.Equal(JobOutcome.Rejected, JobExecutor.CheckPolicy(off, new AgentPolicy(AllowAutoUpdateChanges: false))?.Outcome);

        // Benutzer-Änderungen brauchen weiter allowSystemChanges
        var user = off with { Change = new SystemChange(SystemChangeKind.UserLock, UserName: "bob") };
        Assert.Equal(JobOutcome.Rejected, JobExecutor.CheckPolicy(user, new AgentPolicy())?.Outcome);
    }

    [Fact]
    public void PolicySet_Key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.False(AgentPolicy.Set(dir, "allowAutoUpdateChanges", "false").AllowAutoUpdateChanges);
        Assert.False(AgentPolicy.Load(dir).AllowAutoUpdateChanges);
    }

    [Fact]
    public void AptOverride_WinsAndIsExplained()
    {
        Assert.Contains("APT::Periodic::Unattended-Upgrade \"0\";", NativeAutoUpdates.AptOverride(false));
        Assert.DoesNotContain("Update-Package-Lists", NativeAutoUpdates.AptOverride(false));
        Assert.Contains("APT::Periodic::Unattended-Upgrade \"1\";", NativeAutoUpdates.AptOverride(true));
        Assert.StartsWith("//", NativeAutoUpdates.AptOverride(true));
        Assert.EndsWith("99aiopatcher-auto-upgrades", NativeAutoUpdates.AptOverrideFile); // lädt nach 20auto-upgrades
    }

    [Fact]
    public void Mapping_ModuleAndFeature()
    {
        Assert.Equal(AgentModules.Updates, AgentModules.ForChange(SystemChangeKind.AutoUpdatesOn));
        var job = new PatchJob { Type = JobType.SystemChange, RequestedBy = "admin:test", Payload = JsonSerializer.Serialize(new SystemChange(SystemChangeKind.AutoUpdatesOff), AgentJsonContext.Default.SystemChange) };
        Assert.Equal(Feature.AutoUpdateControl, ComplianceEndpoints.FeatureFor(job));
    }

    [Fact]
    public async Task Job_BecomesUpdatesChange_ThenFreshDescription()
    {
        var first = await PollAsync();
        await AnswerAsync(first!, Describe(nativeAuto: true, updatesActions: [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot, ModuleActions.Change]));
        await AnswerAsync((await PollAsync())!, null); // Scan
        Assert.Null(await PollAsync());
        Assert.True(_host.NativeAutoUpdatesEnabled);

        var job = new PatchJob
        {
            HostId = _host.Id, Type = JobType.SystemChange, RequestedBy = "admin:test",
            Payload = JsonSerializer.Serialize(new SystemChange(SystemChangeKind.AutoUpdatesOff), AgentJsonContext.Default.SystemChange),
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var task = await PollAsync();
        Assert.Equal((AgentModules.Updates, ModuleActions.Change), (task!.Module, task.Action));
        Assert.Equal(SystemChangeKind.AutoUpdatesOff, PullJsonContext.FromElement(task.Payload, PullJsonContext.Default.SystemChange)!.Kind);
        await AnswerAsync(task, null);
        Assert.Equal(JobStatus.Succeeded, job.Status);

        // danach gleich den neuen Stand holen
        var describe = await PollAsync();
        Assert.Equal((AgentModules.System, ModuleActions.Describe), (describe!.Module, describe.Action));
        await AnswerAsync(describe, Describe(nativeAuto: false, updatesActions: [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot, ModuleActions.Change]));
        Assert.False(_host.NativeAutoUpdatesEnabled);
    }

    [Fact]
    public async Task OldAgent_WithoutChange_JobRejected()
    {
        await AnswerAsync((await PollAsync())!, Describe(nativeAuto: true, updatesActions: [ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot]));
        var job = new PatchJob
        {
            HostId = _host.Id, Type = JobType.SystemChange, RequestedBy = "admin:test",
            Payload = JsonSerializer.Serialize(new SystemChange(SystemChangeKind.AutoUpdatesOff), AgentJsonContext.Default.SystemChange),
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var task = await PollAsync();
        Assert.NotEqual(ModuleActions.Change, task?.Action);
        Assert.Equal(JobStatus.Rejected, job.Status);
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

    private static JsonElement Describe(bool nativeAuto, IReadOnlyList<string> updatesActions) => PullJsonContext.ToElement(new SystemDescription(
        "0.1.0", "api", OsFamily.Linux, "Ubuntu", "22.04", "5.15", TimeSpan.FromHours(5), false, nativeAuto, [], "x64",
        [new(AgentModules.System, true, false, []), new(AgentModules.Updates, true, false, updatesActions), new(AgentModules.Agent, true, false, [])]),
        PullJsonContext.Default.SystemDescription);
}
