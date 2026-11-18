using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Scan-Intervalle: Standard aus den Einstellungen, je Host abweichend; Images nach dem kürzesten Intervall.</summary>
public sealed class ScanIntervalTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private readonly ScanSchedule _schedule = new();
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    public ScanIntervalTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static ManagedHost Host(string name, int? updateHours = null, int? imageHours = null, Site? site = null) => new()
    {
        Site = site ?? new Site { Name = "Home" }, Name = name, Hostname = name, CertificateThumbprint = name,
        CertificateExpiresAt = Now.AddYears(1), OsFamily = OsFamily.Linux, UpdateScanHours = updateHours, ImageCheckHours = imageHours,
    };

    /// <summary>Scan fällig, wenn der letzte vor lastScanHoursAgo Stunden angefordert wurde?</summary>
    private bool ScanDue(ManagedHost host, double lastScanHoursAgo)
    {
        var history = new Dictionary<(string, string), DateTime> { [(AgentModules.Updates, ModuleActions.Scan)] = Now.AddHours(-lastScanHoursAgo) };
        var context = new ModuleContext(_db, new Notifier(), _features, _downloads, null, Now, history, _schedule);
        return new UpdatesServerModule().Due(host, context, null) is { Action: ModuleActions.Scan };
    }

    [Fact]
    public void UpdateScan_FollowsDefault_HostOverride_AndSettingsChange()
    {
        var host = Host("web1");

        // Standard 6 Stunden
        Assert.False(ScanDue(host, 5.9));
        Assert.True(ScanDue(host, 6));

        // Standard auf 1 Stunde: gilt sofort, ohne Neustart
        _schedule.Use(new ScanSettingsRecord { UpdateScanHours = 1 });
        Assert.True(ScanDue(host, 1));
        Assert.False(ScanDue(host, 0.9));

        // Host abweichend 24 Stunden: Standard gilt für ihn nicht
        host.UpdateScanHours = 24;
        Assert.False(ScanDue(host, 23));
        Assert.True(ScanDue(host, 24));
    }

    [Fact]
    public void WithoutSchedule_TheFixedDefaultStays()
    {
        var host = Host("web1", updateHours: 1);
        var history = new Dictionary<(string, string), DateTime> { [(AgentModules.Updates, ModuleActions.Scan)] = Now.AddHours(-2) };
        var context = new ModuleContext(_db, new Notifier(), _features, _downloads, null, Now, history);
        Assert.Null(new UpdatesServerModule().Due(host, context, null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(24, true)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(48, false)]
    public void OnlyListedIntervalsAreAllowed(int? hours, bool allowed) => Assert.Equal(allowed, ScanSchedule.IsAllowed(hours));

    [Fact]
    public async Task ImageCheck_UsesShortestIntervalOfTheHostsRunningTheImage()
    {
        var site = new Site { Name = "Home" };
        var docker = Host("docker", imageHours: 1, site: site);
        var nas = Host("nas", site: site);
        _db.Hosts.AddRange(docker, nas);
        _db.Containers.AddRange(
            new ContainerInfo { Host = docker, Name = "web", Image = "nginx:1", Reference = "docker.io/library/nginx:1" },
            new ContainerInfo { Host = nas, Name = "web", Image = "nginx:1", Reference = "docker.io/library/nginx:1" },
            new ContainerInfo { Host = nas, Name = "db", Image = "postgres:17", Reference = "docker.io/library/postgres:17" });
        await _db.SaveChangesAsync();

        _schedule.Use(new ScanSettingsRecord { ImageCheckHours = 12 });
        var due = await ContainerUpdateService.DueIntervalsAsync(_db, _schedule, CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(1), due["docker.io/library/nginx:1"]);     // läuft auch auf dem Host mit 1 Stunde
        Assert.Equal(TimeSpan.FromHours(12), due["docker.io/library/postgres:17"]); // nur auf dem Host mit Standard
    }
}
