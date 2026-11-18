using System.Text.Json;
using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Patchnotes anstehender Updates: Vormerken nach dem Scan, Abholen per updates.changelog, Kürzen auf das Neue.</summary>
public sealed class ChangelogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private const string PveManager = """
        pve-manager (8.3.1) bookworm; urgency=medium

          * ui: fix backup job editor.

          * api: guard against empty node list.

         -- Proxmox Support Team <support@proxmox.com>  Wed, 04 Dec 2024 16:32:01 +0100

        pve-manager (8.3.0) bookworm; urgency=medium

          * add new SDN features.

         -- Proxmox Support Team <support@proxmox.com>  Thu, 21 Nov 2024 18:40:12 +0100

        pve-manager (8.2.10) bookworm; urgency=medium

          * old entry the host already has.

         -- Proxmox Support Team <support@proxmox.com>  Mon, 18 Nov 2024 10:00:00 +0100
        """;

    public ChangelogTests()
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

    [Fact]
    public void Since_KeepsOnlyEntriesNewerThanInstalled_NewestFirst()
    {
        var sections = UpdateChangelogs.Since(PveManager, "8.2.10");

        Assert.Equal(["8.3.1", "8.3.0"], sections.Select(s => s.Version));
        Assert.Contains("fix backup job editor", sections[0].Text);
        Assert.Equal("Wed, 04 Dec 2024 16:32:01 +0100", sections[0].Date);
        Assert.DoesNotContain("old entry", string.Join("\n", sections.Select(s => s.Text)));
    }

    [Fact]
    public void Since_WithoutInstalledVersion_AllEntries_AndUnknownFormatAsOneBlock()
    {
        Assert.Equal(3, UpdateChangelogs.Since(PveManager, null).Count);

        var plain = UpdateChangelogs.Since("Just some release notes\nwithout Debian headers.", "1.0");
        Assert.Single(plain);
        Assert.Null(plain[0].Version);
    }

    [Fact]
    public void Cache_RequestTakeStore_AndErrorsAreRetriedLater()
    {
        var cache = new UpdateChangelogs();
        var host = Guid.NewGuid();

        Assert.Equal(2, cache.Request(host, [("pve-manager", "8.3.1"), ("samba", "2:4.17.12")], Now));
        Assert.Equal(0, cache.Request(host, [("pve-manager", "8.3.1")], Now)); // schon vorgemerkt
        Assert.True(cache.HasPending(host));

        var taken = cache.TakePending(host, 40);
        Assert.Equal(2, taken.Count);
        Assert.False(cache.HasPending(host));

        cache.Store([new PackageChangelog("pve-manager", "8.3.1", PveManager, null), new PackageChangelog("samba", "2:4.17.12", null, "E: Failed to fetch changelog")], Now);
        Assert.NotNull(cache.Get("pve-manager", "8.3.1", Now)!.Text);
        Assert.Equal("E: Failed to fetch changelog", cache.Get("samba", "2:4.17.12", Now)!.Error);

        // vorhandene werden nicht erneut angefordert, Fehler erst nach einer Weile wieder
        Assert.Equal(0, cache.Request(host, [("pve-manager", "8.3.1"), ("samba", "2:4.17.12")], Now.AddHours(1)));
        Assert.Equal(1, cache.Request(host, [("samba", "2:4.17.12")], Now + UpdateChangelogs.RetryErrorsAfter));
    }

    private ManagedHost Host(bool supportsChangelog)
    {
        var actions = supportsChangelog
            ? new[] { ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot, ModuleActions.Changelog }
            : new[] { ModuleActions.Scan, ModuleActions.Install, ModuleActions.Reboot };
        return new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "pve", Hostname = "pve", CertificateThumbprint = "x",
            CertificateExpiresAt = Now.AddYears(1), OsFamily = OsFamily.Linux,
            ReportedModules = JsonSerializer.Serialize(new List<ModuleInfo> { new(AgentModules.Updates, true, false, actions) }, ModuleJson.Default.ListModuleInfo),
        };
    }

    private ModuleContext Context(UpdateChangelogs cache, double lastScanHoursAgo = 1) => new(_db, new Notifier(), _features, _downloads, null, Now,
        new Dictionary<(string, string), DateTime> { [(AgentModules.Updates, ModuleActions.Scan)] = Now.AddHours(-lastScanHoursAgo) }, null, cache);

    [Fact]
    public async Task AfterScan_ChangelogsAreFetchedWithTheNextPoll_OnlyWhenTheAgentSupportsIt()
    {
        var cache = new UpdateChangelogs();
        var module = new UpdatesServerModule();
        var host = Host(supportsChangelog: true);
        _db.Hosts.Add(host);
        await _db.SaveChangesAsync();

        var scan = new UpdateScanResult([new AvailableUpdate("pve-manager", "8.2.10", "8.3.1", false, null)], null);
        await module.HandleAsync(Context(cache), host, new AgentTaskRecord { HostId = host.Id, Module = AgentModules.Updates, Action = ModuleActions.Scan },
            new TaskResultRequest(JobOutcome.Succeeded, "ok", PullJsonContext.ToElement(scan, PullJsonContext.Default.UpdateScanResult)), CancellationToken.None);

        var planned = module.Due(host, Context(cache), null);
        Assert.Equal(ModuleActions.Changelog, planned?.Action);
        var request = PullJsonContext.FromElement(planned!.Payload, PullJsonContext.Default.ChangelogRequest)!;
        Assert.Equal(new ChangelogPackage("pve-manager", "8.3.1"), Assert.Single(request.Packages));

        // Antwort landet im Speicher, danach ist nichts mehr fällig
        var answer = new ChangelogResult([new PackageChangelog("pve-manager", "8.3.1", PveManager, null)]);
        await module.HandleAsync(Context(cache), host, new AgentTaskRecord { HostId = host.Id, Module = AgentModules.Updates, Action = ModuleActions.Changelog },
            new TaskResultRequest(JobOutcome.Succeeded, "ok", PullJsonContext.ToElement(answer, PullJsonContext.Default.ChangelogResult)), CancellationToken.None);
        Assert.NotNull(cache.Get("pve-manager", "8.3.1", Now));
        Assert.Null(module.Due(host, Context(cache), null));

        // ein Scan hat Vorrang vor Changelogs
        cache.Request(host.Id, [("samba", "2:4.17.12")], Now);
        Assert.Equal(ModuleActions.Scan, module.Due(host, Context(cache, lastScanHoursAgo: 7), null)?.Action);

        // älterer Agent ohne changelog: nichts vormerken, nichts anfordern
        var old = Host(supportsChangelog: false);
        var oldCache = new UpdateChangelogs();
        await module.HandleAsync(Context(oldCache), old, new AgentTaskRecord { HostId = old.Id, Module = AgentModules.Updates, Action = ModuleActions.Scan },
            new TaskResultRequest(JobOutcome.Succeeded, "ok", PullJsonContext.ToElement(scan, PullJsonContext.Default.UpdateScanResult)), CancellationToken.None);
        Assert.False(oldCache.HasPending(old.Id));
    }

    [Theory]
    [InlineData("pve-manager", "8.3.1", true)]
    [InlineData("libldb2", "2:2.6.2+samba4.17.12+dfsg-0+deb12u1", true)]
    [InlineData("linux-image-6.8.12-4-pve", "6.8.12-4", true)]
    [InlineData("-o", "1.0", false)]                 // keine Optionen einschleusen
    [InlineData("pve-manager", "-oFoo=bar", false)]
    [InlineData("pve manager", "1.0", false)]
    [InlineData("pve-manager", "1.0;rm", false)]
    public void Agent_AcceptsOnlyRealPackageNamesAndVersions(string name, string version, bool valid) =>
        Assert.Equal(valid, AptChangelog.IsValid(new ChangelogPackage(name, version)));
}
