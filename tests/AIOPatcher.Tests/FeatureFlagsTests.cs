using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Funktionen &amp; Sicherheit: Standardwerte, Sperren per Konfiguration und Durchsetzung beim Job-Versand.</summary>
public sealed class FeatureFlagsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ManagedHost _host;
    private static readonly DateTime Now = new(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc);

    public FeatureFlagsTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost { Site = new Site { Name = "Homelab" }, Name = "win01", Hostname = "win01", CertificateThumbprint = Guid.NewGuid().ToString("N") };
        _db.Hosts.Add(_host);
        _db.SaveChanges();
    }

    private static FeatureFlags Flags(Action<AIOPatcherOptions>? configure = null)
    {
        var options = new AIOPatcherOptions { UpdateCheckRepository = "example/aiopatcher" };
        configure?.Invoke(options);
        return new FeatureFlags(options);
    }

    private PatchJob Queue(JobType type, string requestedBy = "admin:test", bool allowReboot = false)
    {
        var job = new PatchJob { Host = _host, Type = type, RequestedBy = requestedBy, AllowReboot = allowReboot, Status = JobStatus.Queued, CreatedAt = Now.AddMinutes(-10 + _db.Jobs.Count()) };
        _db.Jobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    [Fact]
    public void Defaults_RiskyFeaturesAreOff()
    {
        var flags = Flags();

        Assert.False(flags.IsOn(Feature.AppUpdates));
        Assert.False(flags.IsOn(Feature.OpnsenseUpdates));
        Assert.True(flags.IsOn(Feature.PatchJobs));
        Assert.True(flags.IsOn(Feature.Automation));
        Assert.All(FeatureFlags.Definitions.Where(d => d.Risk == FeatureRisk.High), d => Assert.False(d.Default));
        Assert.Equal(Enum.GetValues<Feature>().Length, FeatureFlags.Definitions.Length);
    }

    [Fact]
    public async Task SetAsync_PersistsAndSurvivesReload()
    {
        var flags = Flags();
        Assert.True(await flags.SetAsync(_db, Feature.AppUpdates, true, "admin", CancellationToken.None));
        Assert.True(await flags.SetAsync(_db, Feature.Reboots, false, "admin", CancellationToken.None));

        var reloaded = Flags();
        await reloaded.LoadAsync(_db, CancellationToken.None);

        Assert.True(reloaded.IsOn(Feature.AppUpdates));
        Assert.False(reloaded.IsOn(Feature.Reboots));
        Assert.Equal("admin", reloaded.Stored(Feature.Reboots)?.ChangedBy);
    }

    [Fact]
    public async Task LockedFeature_CannotBeEnabled_AndIsOffEvenIfStoredOn()
    {
        await Flags().SetAsync(_db, Feature.AppUpdates, true, "admin", CancellationToken.None);
        var flags = Flags(o => o.LockedFeatures = "appupdates, OpnsenseUpdates");
        await flags.LoadAsync(_db, CancellationToken.None);

        Assert.Equal("AIOPatcher__LockedFeatures", flags.LockReason(Feature.AppUpdates));
        Assert.False(flags.IsOn(Feature.AppUpdates));
        Assert.False(await flags.SetAsync(_db, Feature.OpnsenseUpdates, true, "admin", CancellationToken.None));
        Assert.True(await flags.SetAsync(_db, Feature.AppUpdates, false, "admin", CancellationToken.None)); // ausschalten geht immer
    }

    [Fact]
    public void LegacyOptions_LockTheirFeature()
    {
        var flags = Flags(o => { o.CveLookupEnabled = false; o.AgentAutoUpdate = false; o.UpdateCheckRepository = null; });

        Assert.False(flags.IsOn(Feature.CveLookup));
        Assert.False(flags.IsOn(Feature.AgentSelfUpdate));
        Assert.False(flags.IsOn(Feature.ReleaseCheck));
        Assert.True(flags.IsOn(Feature.ContainerChecks));
    }

    [Fact]
    public async Task Dispatch_RejectsDisabledJobTypes_AndHandsOutTheNextAllowedOne()
    {
        var flags = Flags(); // AppUpdates ab Werk aus
        await flags.SetAsync(_db, Feature.Reboots, false, "admin", CancellationToken.None);
        var app = Queue(JobType.AppUpdate);
        var reboot = Queue(JobType.Reboot);
        var scan = Queue(JobType.Scan);

        var job = await AgentEndpoints.DispatchJobAsync(_db, _host, null, Now, CancellationToken.None, flags);

        Assert.Equal(scan.Id, job?.JobId);
        Assert.Equal(JobStatus.Rejected, app.Status);
        Assert.Equal(JobStatus.Rejected, reboot.Status);
        Assert.Contains("Settings → Features & security", app.Summary);
    }

    [Fact]
    public async Task Dispatch_PatchWithRebootsOff_PatchesWithoutReboot()
    {
        var flags = Flags();
        await flags.SetAsync(_db, Feature.Reboots, false, "admin", CancellationToken.None);
        Queue(JobType.Patch, allowReboot: true);

        var job = await AgentEndpoints.DispatchJobAsync(_db, _host, null, Now, CancellationToken.None, flags);

        Assert.NotNull(job);
        Assert.Equal(JobType.Patch, job.Type);
        Assert.False(job.AllowReboot);
    }

    [Fact]
    public async Task Dispatch_EmergencyStop_HoldsAutomatedJobs_ButNotManualOnes()
    {
        var flags = Flags();
        await flags.SetAsync(_db, Feature.Automation, false, "admin", CancellationToken.None);
        var scheduled = Queue(JobType.Patch, AutoPatchScheduler.Actor);
        var runbook = Queue(JobType.Patch, "runbook:abc");
        var manual = Queue(JobType.Scan);

        var job = await AgentEndpoints.DispatchJobAsync(_db, _host, null, Now, CancellationToken.None, flags);

        Assert.Equal(manual.Id, job?.JobId);
        Assert.Equal(JobStatus.Queued, scheduled.Status); // wartet, wird nicht abgelehnt
        Assert.Equal(JobStatus.Queued, runbook.Status);

        await flags.SetAsync(_db, Feature.Automation, true, "admin", CancellationToken.None);
        manual.Status = JobStatus.Succeeded;
        var resumed = await AgentEndpoints.DispatchJobAsync(_db, _host, null, Now, CancellationToken.None, flags);
        Assert.Equal(scheduled.Id, resumed?.JobId);
    }

    [Fact]
    public void FeatureList_ReportsLockAndDefaults()
    {
        var list = FeatureEndpoints.List(Flags(o => o.LockedFeatures = "OpnsenseUpdates"));

        var opn = Assert.Single(list, f => f.Feature == Feature.OpnsenseUpdates);
        Assert.Equal("AIOPatcher__LockedFeatures", opn.LockedBy);
        Assert.Equal(FeatureRisk.High, opn.Risk);
        Assert.Contains(list, f => f.Feature == Feature.Automation && f.Enabled && f.Group == "hosts");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
