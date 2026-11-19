using AIOPatcher.Agent;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Agent meldet seine lokale policy.json mit den Systemdaten; das Dashboard zeigt sie (ändern kann es sie nicht).</summary>
public sealed class PolicyReportTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _host;

    public PolicyReportTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost { Site = new Site { Name = "Home" }, Name = "web", Hostname = "web", CertificateThumbprint = "a", OsFamily = OsFamily.Linux };
        _db.Hosts.Add(_host);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Agent_ReportsAllValues()
    {
        var report = new AgentPolicy(AllowPatch: PatchPermission.Security, AllowReboot: false, AllowSystemChanges: true, DisabledModules: ["users"]).ToReport();
        Assert.Equal("Security", report.AllowPatch);
        Assert.False(report.AllowReboot);
        Assert.True(report.AllowSystemChanges);
        Assert.False(report.AllowServerUpdate);
        Assert.Equal(["users"], report.DisabledModules!);
    }

    private async Task DescribeAsync(AgentPolicyReport? policy)
    {
        var description = new SystemDescription("0.14.0", "web", OsFamily.Linux, "Ubuntu", "24.04", "6.8", TimeSpan.FromHours(1), false, true, [], "x64",
            [new(AgentModules.System, true, false, [ModuleActions.Describe])], null, policy);
        var context = new ModuleContext(_db, new Notifier(), new FeatureFlags(new AIOPatcherOptions()), new AgentDownloads(new AIOPatcherOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")),
        }), null, DateTime.UtcNow, new Dictionary<(string, string), DateTime>());
        var task = new AgentTaskRecord { HostId = _host.Id, Module = AgentModules.System, Action = ModuleActions.Describe };
        await new SystemServerModule().HandleAsync(context, _host, task,
            new TaskResultRequest(JobOutcome.Succeeded, null, PullJsonContext.ToElement(description, PullJsonContext.Default.SystemDescription)), default);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Server_StoresReportedPolicy_AndShowsItInTheHostList()
    {
        await DescribeAsync(new AgentPolicyReport("Security", AllowReboot: false, AllowMonitoringChanges: true));
        var stored = PolicyReport.Parse(_host.ReportedPolicy)!;
        Assert.Equal("Security", stored.AllowPatch);
        Assert.False(stored.AllowReboot);
        Assert.Null(stored.AllowSystemChanges); // nicht gemeldet (z. B. Lite-Agent)

        var summaries = await HostEndpoints.EnrichAsync(_db, [await _db.Hosts.Select(h => new HostEndpoints.HostSummaryDto(
            h.Id, h.SiteId, "Home", h.Name, h.Hostname, h.OsFamily, null, null, null, null, null, HostEndpoints.HostStatus.Online, false, false, 0, 0,
            new HostEndpoints.AutoPatchDto(false, PatchScope.Security, false, 0, new TimeOnly(3, 0), 0), null, null, 0, 0, false)).SingleAsync()],
            new ServerClock(new AIOPatcherOptions()), default);
        Assert.Equal("Security", summaries.Single().Policy!.AllowPatch);
    }

    [Fact]
    public async Task OlderAgent_WithoutPolicy_ClearsTheValue()
    {
        await DescribeAsync(new AgentPolicyReport("All"));
        Assert.NotNull(_host.ReportedPolicy);
        await DescribeAsync(null);
        Assert.Null(_host.ReportedPolicy);
    }

    [Fact]
    public void Serialize_RejectsOddValues()
    {
        var json = PolicyReport.Serialize(new AgentPolicyReport("Everything", DisabledModules: ["firewall", new string('x', 100)]));
        var back = PolicyReport.Parse(json)!;
        Assert.Equal("All", back.AllowPatch);
        Assert.Equal(["firewall"], back.DisabledModules!);
        Assert.Null(PolicyReport.Parse("{kaputt"));
    }
}
