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

    [Fact]
    public void Timings_DefaultsThenSettingsWithoutRestart()
    {
        var host = Host("web1");
        bool MetricsDue(double minutesAgo) =>
            new MetricsServerModule().Due(host, new ModuleContext(_db, new Notifier(), _features, _downloads, null, Now,
                new Dictionary<(string, string), DateTime> { [(AgentModules.Metrics, ModuleActions.Sample)] = Now.AddMinutes(-minutesAgo) }, _schedule), null) is not null;

        // Standard 5 Minuten
        Assert.Equal(TimeSpan.FromMinutes(5), _schedule.Interval(Timings.Metrics));
        Assert.False(MetricsDue(4));
        Assert.True(MetricsDue(5));

        // Auf 15 Minuten gestellt: gilt sofort
        _schedule.Use(new ScanSettingsRecord { TimingsJson = Timings.With(null, Timings.Metrics, 15) });
        Assert.False(MetricsDue(10));
        Assert.True(MetricsDue(15));
        Assert.Equal(TimeSpan.FromMinutes(15), _schedule.Interval(Timings.ReleaseCheck)); // andere bleiben beim Standard
    }

    [Fact]
    public async Task ReleaseCheck_CheckNow_OnlyWithRepositoryAndFeature()
    {
        // Ohne Repository fragt "Jetzt prüfen" GitHub nicht ab (HTTP würde hier mangels Factory scheitern)
        var check = new ReleaseCheck(null!, new AIOPatcherOptions { UpdateCheckRepository = "" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseCheck>.Instance, _features);
        Assert.False(await check.CheckNowAsync(default));
        Assert.Null(check.CheckedAt);
        Assert.Equal(TimeSpan.FromSeconds(10), ReleaseCheck.ManualCooldown);
    }

    [Fact]
    public void Timings_StoreOnlyDeviations_AndDropInvalidValues()
    {
        var json = Timings.With(null, Timings.Opnsense, 60);
        json = Timings.With(json, Timings.Sharing, 15);
        Assert.Equal("""{"opnsense":60,"sharing":15}""", json);

        // Zurück auf den Standard entfernt den Eintrag
        json = Timings.With(json, Timings.Opnsense, 30);
        json = Timings.With(json, Timings.Sharing, 5);
        Assert.Null(json);

        // Unbekannte Schlüssel und nicht wählbare Werte (z. B. Versionsprüfung jede Minute) fallen beim Lesen weg
        var parsed = Timings.Parse("""{"releaseCheck":1,"nope":5,"metrics":10}""");
        Assert.Equal(new Dictionary<string, int> { ["metrics"] = 10 }, parsed);
        Assert.Empty(Timings.Parse("kaputt"));
    }

    [Fact]
    public async Task Timings_EndpointValidatesAndSaves()
    {
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin")], "test"));
        var audit = new AuditLog(_db, new Microsoft.AspNetCore.Http.HttpContextAccessor());

        var bad = await AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.SaveTimingAsync(new("releaseCheck", 1), admin, _db, audit, _schedule, default);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.Ok<AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.ScanSettingsDto>>(bad);
        var unknown = await AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.SaveTimingAsync(new("nope", 15), admin, _db, audit, _schedule, default);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.Ok<AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.ScanSettingsDto>>(unknown);

        var ok = await AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.SaveTimingAsync(new("backups", 60), admin, _db, audit, _schedule, default);
        var dto = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AIOPatcher.Server.Endpoints.ScanSettingsEndpoints.ScanSettingsDto>>(ok).Value!;
        Assert.Equal(60, dto.Timings!.Single(t => t.Key == "backups").Minutes);
        Assert.Equal(TimeSpan.FromHours(1), _schedule.Interval(Timings.Backups));
        Assert.Equal("""{"backups":60}""", (await _db.ScanSettings.SingleAsync()).TimingsJson);
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
