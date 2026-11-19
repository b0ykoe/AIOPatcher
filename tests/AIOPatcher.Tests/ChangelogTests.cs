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
    public async Task Store_RequestTake_PersistsAndRetriesErrorsLater()
    {
        var cache = new UpdateChangelogs();
        var host = Guid.NewGuid();
        var ct = CancellationToken.None;

        Assert.Equal(2, await cache.RequestAsync(_db, host, [("pve-manager", "8.3.1"), ("samba", "2:4.17.12")], Now, ct));
        Assert.Equal(0, await cache.RequestAsync(_db, host, [("pve-manager", "8.3.1")], Now, ct)); // schon vorgemerkt
        Assert.Equal(2, cache.TakePending(host, 40).Count);
        Assert.False(cache.HasPending(host));

        await cache.StoreAsync(_db, host, [new PackageChangelog("pve-manager", "8.3.1", PveManager, null), new PackageChangelog("samba", "2:4.17.12", null, "E: Failed to fetch changelog")], Now, ct);

        // dauerhaft in der Datenbank, auch für einen neuen Dienst (Neustart)
        var restarted = new UpdateChangelogs();
        Assert.NotNull((await UpdateChangelogs.GetAsync(_db, "pve-manager", "8.3.1", ct))!.Text);
        Assert.Equal("E: Failed to fetch changelog", (await UpdateChangelogs.GetAsync(_db, "samba", "2:4.17.12", ct))!.Error);
        Assert.Equal(0, await restarted.RequestAsync(_db, host, [("pve-manager", "8.3.1"), ("samba", "2:4.17.12")], Now.AddHours(1), ct));
        Assert.Equal(1, await restarted.RequestAsync(_db, host, [("samba", "2:4.17.12")], Now + UpdateChangelogs.RetryErrorsAfter, ct));

        // ein späterer Fehler ersetzt vorhandenen Text nicht; dieselbe Antwort zweimal legt keinen zweiten Eintrag an
        await cache.StoreAsync(_db, host, [new PackageChangelog("pve-manager", "8.3.1", null, "E: timeout"), new PackageChangelog("pve-manager", "8.3.1", PveManager, null)], Now, ct);
        Assert.Equal(1, await _db.PackageChangelogs.CountAsync(c => c.Name == "pve-manager"));
        Assert.NotNull((await UpdateChangelogs.GetAsync(_db, "pve-manager", "8.3.1", ct))!.Text);
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
        Assert.NotNull(await UpdateChangelogs.GetAsync(_db, "pve-manager", "8.3.1", CancellationToken.None));
        Assert.Null(module.Due(host, Context(cache), null));

        // ein Scan hat Vorrang vor Changelogs
        await cache.RequestAsync(_db, host.Id, [("samba", "2:4.17.12")], Now, CancellationToken.None);
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

    [Fact]
    public async Task Describe_RequestsMissingChangelogs_ForUpdatesFoundBeforeTheAgentCouldFetchThem()
    {
        var cache = new UpdateChangelogs();
        var host = Host(supportsChangelog: true);
        _db.Hosts.Add(host);
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "apt", CurrentVersion = "2.0.9", AvailableVersion = "2.0.11" });
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "pve-manager", CurrentVersion = "8.2.10", AvailableVersion = "8.3.1" });
        _db.PackageChangelogs.Add(new PackageChangelogRecord { Name = "pve-manager", Version = "8.3.1", Text = PveManager, FetchedAt = Now });
        await _db.SaveChangesAsync();

        Assert.Equal(1, await cache.RequestMissingAsync(_db, host, Now, CancellationToken.None));
        Assert.Equal(new ChangelogPackage("apt", "2.0.11"), Assert.Single(cache.TakePending(host.Id, 40)));
    }

    [Fact]
    public async Task JobSnapshot_KeepsTheUpdatesOfAPatchJob_ForTheHistory()
    {
        var host = Host(supportsChangelog: true);
        _db.Hosts.Add(host);
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "openssl", CurrentVersion = "3.0.15", AvailableVersion = "3.0.16", IsSecurity = true });
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "vim", CurrentVersion = "9.0.1", AvailableVersion = "9.0.2" });
        await _db.SaveChangesAsync();

        var security = AIOPatcher.Server.Endpoints.ChangelogEndpoints.SnapshotAsync(_db, host.Id, PatchScope.Security, CancellationToken.None);
        var items = JsonSerializer.Deserialize<List<AIOPatcher.Server.Endpoints.ChangelogEndpoints.JobUpdateItem>>(await security, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("openssl", Assert.Single(items).Name);

        var all = await AIOPatcher.Server.Endpoints.ChangelogEndpoints.SnapshotAsync(_db, host.Id, PatchScope.All, CancellationToken.None);
        Assert.Contains("\"vim\"", all);
    }

    private const string OpenSsl = """
        openssl (3.0.16-1~deb12u1) bookworm-security; urgency=medium

          * Import 3.0.16
            - CVE-2024-13176 (Timing side-channel in ECDSA signature computation).
            - CVE-2024-9143 (Low-level invalid GF(2^m) parameters lead to OOB memory access).
            - Fix possible use-after-free in SSL_select_next_proto when the client sends an empty list.

         -- Sebastian Andrzej Siewior <sebastian@breakpoint.cc>  Sat, 01 Feb 2025 12:00:00 +0100

        openssl (3.0.15-1~deb12u1) bookworm-security; urgency=medium

          * old entry the host already has.

         -- Sebastian Andrzej Siewior <sebastian@breakpoint.cc>  Mon, 02 Sep 2024 12:00:00 +0100
        """;

    /// <summary>
    /// Nachgebaute OpenAI-kompatible KI (z. B. Ollama): beantwortet Paket-Bündel mit "name: Package summary" je Paket
    /// (ohne die Pakete in <see cref="Skip"/>) und meldet 100 Eingabe- und 20 Ausgabe-Tokens.
    /// </summary>
    private sealed class FakeChat : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public HashSet<string> Skip { get; } = [];

        private static readonly System.Text.RegularExpressions.Regex Package = new(@"### ([a-z0-9.+-]+)");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            var answer = body.Contains("pending updates on one server")
                ? "- Host summary"
                : string.Join("\n", Package.Matches(body).Select(m => m.Groups[1].Value).Where(n => !Skip.Contains(n)).Select(n => $"{n}: Package summary"));
            var json = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = answer } } },
                usage = new { prompt_tokens = 100, completion_tokens = 20 },
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static AIOPatcher.Server.Services.Ai.UpdateSummaries Summaries(FakeChat chat)
    {
        var summaries = new AIOPatcher.Server.Services.Ai.UpdateSummaries(null!, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            new AIOPatcher.Server.Services.Ai.AiClient(new Factory(chat)), new UpdateChangelogs(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AIOPatcher.Server.Services.Ai.UpdateSummaries>.Instance);
        summaries.Use(new AiSettingsRecord { Enabled = true, Provider = "compatible", BaseUrl = "http://ollama:11434/v1", Model = "llama3.1", Language = "en" });
        return summaries;
    }

    [Fact]
    public async Task Summaries_BundlePackages_SkipShortChangelogs_AndCountTokens()
    {
        var chat = new FakeChat();
        var summaries = Summaries(chat);
        var host = Host(supportsChangelog: true);
        _db.Hosts.Add(host);
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "pve-manager", CurrentVersion = "8.2.10", AvailableVersion = "8.3.1" });
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "openssl", CurrentVersion = "3.0.15-1~deb12u1", AvailableVersion = "3.0.16-1~deb12u1", IsSecurity = true });
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "libssl3", CurrentVersion = "3.0.15-1~deb12u1", AvailableVersion = "3.0.16-1~deb12u1", IsSecurity = true });
        _db.PackageChangelogs.Add(new PackageChangelogRecord { Name = "pve-manager", Version = "8.3.1", Text = PveManager, FetchedAt = Now });
        _db.PackageChangelogs.Add(new PackageChangelogRecord { Name = "openssl", Version = "3.0.16-1~deb12u1", Text = OpenSsl, FetchedAt = Now });
        _db.PackageChangelogs.Add(new PackageChangelogRecord { Name = "libssl3", Version = "3.0.16-1~deb12u1", Text = OpenSsl, FetchedAt = Now });
        await _db.SaveChangesAsync();

        await summaries.RunHostAsync(_db, host.Id, Now, CancellationToken.None);

        // eine Anfrage für beide langen Changelogs, eine für den Host
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("### libssl3", chat.Requests[0]);
        Assert.Contains("### openssl", chat.Requests[0]);
        Assert.DoesNotContain("pve-manager", chat.Requests[0]);
        Assert.Contains("CVE-2024-13176", chat.Requests[0]);
        Assert.DoesNotContain("old entry the host already has", chat.Requests[0]);
        Assert.DoesNotContain("\"pve\"", string.Join("", chat.Requests)); // kein Host-Name

        var openssl = await _db.PackageChangelogs.SingleAsync(c => c.Name == "openssl");
        Assert.Equal("Package summary", openssl.Summary);
        Assert.Equal("compatible/llama3.1", openssl.SummaryModel);
        Assert.Equal("3.0.15-1~deb12u1", openssl.SummaryFrom);
        Assert.Equal("Package summary", (await _db.PackageChangelogs.SingleAsync(c => c.Name == "libssl3")).Summary);

        // kurzer Changelog: ohne KI, der Text selbst
        var pve = await _db.PackageChangelogs.SingleAsync(c => c.Name == "pve-manager");
        Assert.Equal(AIOPatcher.Server.Services.Ai.UpdateSummaries.FromChangelog, pve.SummaryModel);
        Assert.Equal("ui: fix backup job editor. api: guard against empty node list. add new SDN features.", pve.Summary);

        Assert.Equal("- Host summary", (await _db.HostUpdateSummaries.SingleAsync(s => s.HostId == host.Id)).Text);
        var usage = await _db.AiUsage.SingleAsync();
        Assert.Equal((2, 200L, 40L), (usage.Requests, usage.InputTokens, usage.OutputTokens));
        Assert.Equal((2, 200L, 40L), await AIOPatcher.Server.Services.Ai.UpdateSummaries.UsageAsync(_db, Now, 30, CancellationToken.None));

        // nichts geändert: kein neuer Aufruf
        await summaries.RunHostAsync(_db, host.Id, Now, CancellationToken.None);
        Assert.Equal(2, chat.Requests.Count);
    }

    [Fact]
    public async Task Summaries_MissingAnswer_IsRetriedOnlyAfterAWhile()
    {
        var chat = new FakeChat { Skip = { "libssl3" } };
        var summaries = Summaries(chat);
        var host = Host(supportsChangelog: true);
        _db.Hosts.Add(host);
        _db.PendingUpdates.Add(new PendingUpdate { Host = host, Name = "libssl3", CurrentVersion = "3.0.15-1~deb12u1", AvailableVersion = "3.0.16-1~deb12u1" });
        _db.PackageChangelogs.Add(new PackageChangelogRecord { Name = "libssl3", Version = "3.0.16-1~deb12u1", Text = OpenSsl, FetchedAt = Now });
        await _db.SaveChangesAsync();

        await summaries.RunHostAsync(_db, host.Id, Now, CancellationToken.None);
        var record = await _db.PackageChangelogs.SingleAsync();
        Assert.Null(record.Summary);
        Assert.NotNull(record.SummaryError);

        // gleich danach (z. B. nächster Scan): nicht erneut an die KI
        var before = chat.Requests.Count(r => r.Contains("### libssl3"));
        await summaries.RunHostAsync(_db, host.Id, Now.AddHours(1), CancellationToken.None);
        Assert.Equal(before, chat.Requests.Count(r => r.Contains("### libssl3")));

        chat.Skip.Clear();
        await summaries.RunHostAsync(_db, host.Id, Now + AIOPatcher.Server.Services.Ai.UpdateSummaries.RetryErrorsAfter + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal("Package summary", (await _db.PackageChangelogs.SingleAsync()).Summary);
    }

    [Fact]
    public void ParsePackageAnswer_AcceptsCommonFormats()
    {
        var parsed = AIOPatcher.Server.Services.Ai.UpdateSummaries.ParsePackageAnswer("""
            Here are the summaries:
            - **openssl**: Fixes CVE-2024-13176.
            Note: restart services that use TLS.
            `libssl3`: Same fixes as openssl.
            ### vim
            vim: New upstream release.
            unknown-package: ignored
            """, ["openssl", "libssl3", "vim", "curl"]);

        Assert.Equal("Fixes CVE-2024-13176. Note: restart services that use TLS.", parsed["openssl"]);
        Assert.Equal("Same fixes as openssl.", parsed["libssl3"]);
        Assert.Equal("New upstream release.", parsed["vim"]);
        Assert.False(parsed.ContainsKey("curl"));
        Assert.Equal(3, parsed.Count);
    }

    [Fact]
    public void Brief_ShortChangelogsNeedNoAi()
    {
        Assert.Equal("New upstream release.", AIOPatcher.Server.Services.Ai.UpdateSummaries.Brief(
            [new UpdateChangelogs.Section("1.2-1", null, "  [ Jane Doe ]\n  * New upstream release.")]));
        Assert.Equal("", AIOPatcher.Server.Services.Ai.UpdateSummaries.Brief([]));
        Assert.Null(AIOPatcher.Server.Services.Ai.UpdateSummaries.Brief(UpdateChangelogs.Since(OpenSsl, "3.0.15-1~deb12u1")));
    }
}
