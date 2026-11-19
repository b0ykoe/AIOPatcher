using System.Text.Json;
using AIOPatcher.Agent.Platforms.Monitoring;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Monitoring;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Monitoring-Clients (Wazuh, Checkmk): Konfiguration lesen/umschreiben, Zustand bewerten, Aufträge vorbereiten.</summary>
public sealed class MonitoringTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    public MonitoringTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- Wazuh: ossec.conf ---------------------------------------------------------------------------

    private const string Wazuh4Config = """
        <!-- Wazuh - Agent -->
        <ossec_config>
          <client>
            <server>
              <address>old-manager.example.com</address>
              <port>1514</port>
              <protocol>tcp</protocol>
            </server>
            <server>
              <address>10.0.0.9</address>
            </server>
            <config-profile>ubuntu, ubuntu22</config-profile>
            <enrollment>
              <enabled>yes</enabled>
              <manager_address>old-manager.example.com</manager_address>
            </enrollment>
          </client>
        </ossec_config>

        <ossec_config>
          <localfile>
            <log_format>syslog</log_format>
            <location>/var/log/auth.log &amp; more</location>
          </localfile>
        </ossec_config>
        """;

    [Fact]
    public void WazuhConfig_ReadsAllVariants()
    {
        var wazuh = WazuhConfig.Parse(Wazuh4Config);
        Assert.True(wazuh.IsAgent);
        Assert.Equal(MonitoringVariants.Wazuh, wazuh.Variant);
        Assert.Equal(["old-manager.example.com:1514", "10.0.0.9"], wazuh.Addresses);
        Assert.Equal("old-manager.example.com", wazuh.EnrollmentAddress);

        var ossec = WazuhConfig.Parse("<ossec_config><client><server-ip>192.168.1.5</server-ip><port>1514</port></client></ossec_config>");
        Assert.Equal(MonitoringVariants.Ossec, ossec.Variant);
        Assert.Equal(["192.168.1.5:1514"], ossec.Addresses);

        var five = WazuhConfig.Parse("<ossec_config><agent><manager><endpoint>https://mgr.example.com:1517</endpoint></manager></agent></ossec_config>");
        Assert.Equal(MonitoringVariants.Wazuh5, five.Variant);
        Assert.Equal(["https://mgr.example.com:1517"], five.Addresses);

        Assert.False(WazuhConfig.Parse("<ossec_config><remote><connection>secure</connection></remote></ossec_config>").IsAgent);
        Assert.Throws<InvalidOperationException>(() => WazuhConfig.Parse("<ossec_config><client>"));
    }

    [Fact]
    public void WazuhConfig_KeepKeys_ChangesOnlyTheManager()
    {
        var result = WazuhConfig.Rewrite(Wazuh4Config, new MonitoringSpec(MonitoringProducts.Wazuh, "new-manager.example.com", 1514), newEnrollment: false, enrollmentBlock: true);

        var parsed = WazuhConfig.Parse(result);
        Assert.Equal(["new-manager.example.com:1514"], parsed.Addresses);
        Assert.Equal("new-manager.example.com", parsed.EnrollmentAddress); // sonst meldet er sich später beim alten an
        Assert.Contains("<!-- Wazuh - Agent -->", result);
        Assert.Contains("<config-profile>ubuntu, ubuntu22</config-profile>", result);
        // zweiter Block bleibt Zeichen für Zeichen gleich (inklusive Entity)
        Assert.Contains("""
              <localfile>
                <log_format>syslog</log_format>
                <location>/var/log/auth.log &amp; more</location>
              </localfile>
            """.Replace("\r\n", "\n"), result.Replace("\r\n", "\n"));
        Assert.DoesNotContain("10.0.0.9", result);
    }

    [Fact]
    public void WazuhConfig_NewEnrollment_WritesEnrollmentBlock()
    {
        const string plain = "<ossec_config>\n  <client>\n    <server>\n      <address>MANAGER_IP</address>\n    </server>\n  </client>\n</ossec_config>\n";
        var result = WazuhConfig.Rewrite(plain, new MonitoringSpec(MonitoringProducts.Wazuh, "10.1.2.3", Protocol: "tcp", EnrollmentPort: 1515, Groups: "linux,web"),
            newEnrollment: true, enrollmentBlock: true);

        Assert.Contains("<address>10.1.2.3</address>", result);
        Assert.Contains("<protocol>tcp</protocol>", result);
        Assert.Contains("<manager_address>10.1.2.3</manager_address>", result);
        Assert.Contains("<groups>linux,web</groups>", result);
        Assert.Contains("<port>1515</port>", result);
        Assert.Contains("<enabled>yes</enabled>", result);
        Assert.Equal("10.1.2.3", WazuhConfig.Parse(result).EnrollmentAddress);

        // vor 4.0: kein <enrollment>, Anmeldung per agent-auth
        var old = WazuhConfig.Rewrite(plain, new MonitoringSpec(MonitoringProducts.Wazuh, "10.1.2.3"), newEnrollment: true, enrollmentBlock: false);
        Assert.DoesNotContain("<enrollment>", old);
    }

    [Fact]
    public void WazuhConfig_OssecAndWazuh5()
    {
        var ossec = WazuhConfig.Rewrite("<ossec_config><client><server-ip>192.168.1.5</server-ip></client></ossec_config>",
            new MonitoringSpec(MonitoringProducts.Wazuh, "wazuh.example.com"), newEnrollment: true, enrollmentBlock: false);
        Assert.Contains("<server-hostname>wazuh.example.com</server-hostname>", ossec);
        Assert.DoesNotContain("server-ip", ossec);

        const string five = "<ossec_config><agent><manager><endpoint>https://old:1517</endpoint></manager></agent></ossec_config>";
        Assert.Contains("<endpoint>https://new.example.com:1517</endpoint>",
            WazuhConfig.Rewrite(five, new MonitoringSpec(MonitoringProducts.Wazuh, "new.example.com", 1517), newEnrollment: false, enrollmentBlock: true));
        Assert.Throws<InvalidOperationException>(() =>
            WazuhConfig.Rewrite(five, new MonitoringSpec(MonitoringProducts.Wazuh, "new.example.com"), newEnrollment: true, enrollmentBlock: true));
    }

    [Fact]
    public void WazuhState_AndValidation()
    {
        Assert.True(WazuhConfig.ParseState("# State file\nstatus='connected'\nlast_keepalive='2026-11-19 10:00:00'\n"));
        Assert.False(WazuhConfig.ParseState("status='pending'\n"));
        Assert.Equal("disconnected", WazuhConfig.StateValue("status='disconnected'"));
        Assert.Null(WazuhConfig.ParseState(null));

        Assert.Null(WazuhClient.Validate(new MonitoringSpec(MonitoringProducts.Wazuh, "wazuh.example.com", 1514, "tcp", "new"), "secret"));
        Assert.NotNull(WazuhClient.Validate(new MonitoringSpec(MonitoringProducts.Wazuh, "bad host;rm"), null));
        Assert.NotNull(WazuhClient.Validate(new MonitoringSpec(MonitoringProducts.Wazuh, "ok.example.com", Protocol: "http"), null));
        Assert.NotNull(WazuhClient.Validate(new MonitoringSpec(MonitoringProducts.Wazuh, "ok.example.com"), "a\nb"));
    }

    [Fact]
    public void Versions_AreCompared()
    {
        Assert.Equal(0, MonitoringVersion.Compare("4.7.2-1", "v4.7.2"));
        Assert.True(MonitoringVersion.Compare("v4.8.0", "4.7.9") > 0);
        Assert.True(MonitoringVersion.Compare("2.2.0p3", "2.2.0p12") < 0);
        Assert.Equal("2.1.0", MonitoringVersion.Display("1:2.1.0-1"));
        Assert.Equal("2.3.0p12", MonitoringVersion.Display("2.3.0p12-1"));
        Assert.Equal("4.9.2", MonitoringVersion.Display("v4.9.2"));
        Assert.Equal(0, MonitoringVersion.CompareMinor("2.3.0p1", "2.3.0p12"));
        Assert.Equal(0, MonitoringVersion.Compare("unknown", "4.0.0"));
    }

    // --- Checkmk ------------------------------------------------------------------------------------

    [Fact]
    public void Checkmk_Xinetd_OnlyFrom()
    {
        const string withLine = "service check_mk\n{\n\ttype           = UNLISTED\n\tport           = 6556\n\tonly_from      = 10.0.0.1 10.0.0.2\n\tdisable        = no\n}\n";
        Assert.Equal(["10.0.0.1", "10.0.0.2"], CheckmkConfig.ParseXinetdOnlyFrom(withLine));
        var replaced = CheckmkConfig.RewriteXinetdOnlyFrom(withLine, ["192.168.5.10", "192.168.5.0/24"]);
        Assert.Contains("\tonly_from      = 192.168.5.10 192.168.5.0/24\n", replaced);
        Assert.Contains("\tdisable        = no\n", replaced);

        const string without = "service check_mk\n{\n\ttype           = UNLISTED\n\t#only_from      = 127.0.0.1\n\tdisable        = no\n}\n";
        Assert.Empty(CheckmkConfig.ParseXinetdOnlyFrom(without));
        var added = CheckmkConfig.RewriteXinetdOnlyFrom(without, ["10.9.9.9"]);
        Assert.Equal(["10.9.9.9"], CheckmkConfig.ParseXinetdOnlyFrom(added));
        Assert.Contains("#only_from      = 127.0.0.1", added);
        Assert.EndsWith("= 10.9.9.9\n}\n", added);
    }

    [Fact]
    public void Checkmk_UserYaml_OnlyFrom()
    {
        const string inline = "# user settings\nglobal:\n    enabled: yes\n    only_from: 127.0.0.1 10.0.0.0/8\n    port: 6556\nps:\n    enabled: yes\n";
        Assert.Equal(["127.0.0.1", "10.0.0.0/8"], CheckmkConfig.ParseYamlOnlyFrom(inline));
        var replaced = CheckmkConfig.RewriteYamlOnlyFrom(inline, ["192.168.1.20"]);
        Assert.Equal("# user settings\nglobal:\n    enabled: yes\n    only_from: 192.168.1.20\n    port: 6556\nps:\n    enabled: yes\n", replaced);

        const string list = "global:\n  only_from:\n  - 10.0.0.1\n  - 10.0.0.2\n  port: 6556\n";
        Assert.Equal(["10.0.0.1", "10.0.0.2"], CheckmkConfig.ParseYamlOnlyFrom(list));
        Assert.Equal("global:\n  only_from: 10.5.5.5\n  port: 6556\n", CheckmkConfig.RewriteYamlOnlyFrom(list, ["10.5.5.5"]));

        Assert.Equal("global:\n  only_from: 10.5.5.5\n  port: 6556\n", CheckmkConfig.RewriteYamlOnlyFrom("global:\n  port: 6556\n", ["10.5.5.5"]));
        Assert.Equal("ps:\n  enabled: yes\nglobal:\n    only_from: 10.5.5.5\n", CheckmkConfig.RewriteYamlOnlyFrom("ps:\n  enabled: yes\n", ["10.5.5.5"]));
        Assert.Equal("global:\r\n    only_from: 10.5.5.5\r\n", CheckmkConfig.RewriteYamlOnlyFrom("", ["10.5.5.5"]).Replace("\n", "\r\n"));
    }

    [Fact]
    public void Checkmk_Connections_AreReadTolerantly()
    {
        const string status = """
            {"version":"2.3.0p12","agent_socket_operational":true,
             "connections":[{"site_id":"cmk.example.com/mysite","receiver_port":8000,"uuid":"0f8c9b3a-1b2c-4d5e-8f90-123456789abc","local":{"connection_type":"pull-agent"}}]}
            """;
        var connections = CheckmkConfig.ParseConnections(status);
        Assert.Single(connections);
        Assert.Equal("mysite", connections[0].Site);
        Assert.True(connections[0].Matches("CMK.example.com:8000", "mysite"));
        Assert.False(connections[0].Matches("cmk.example.com", "other"));

        const string registered = """
            {"push":{},"pull":{"old.example.com:8000/oldsite":{"trust":{"uuid":"11111111-2222-3333-4444-555555555555","certificate":"..."},"receiver_port":8000}},"pull_imported":[]}
            """;
        Assert.Equal("old.example.com:8000/oldsite", Assert.Single(CheckmkConfig.ParseConnections(registered)).SiteId);
        Assert.Empty(CheckmkConfig.ParseConnections("not json"));
        Assert.Equal("::1", CheckmkConfig.HostOf("[::1]:8000"));
    }

    // --- Server: Ziel und Zustand -------------------------------------------------------------------

    private static ManagedHost Host(Site site, string name = "web1", OsFamily os = OsFamily.Linux, string? osVersion = null, bool monitoring = true) => new()
    {
        Site = site, SiteId = site.Id, Name = name, Hostname = name, CertificateThumbprint = "x", CertificateExpiresAt = Now.AddYears(1), OsFamily = os,
        OsVersion = osVersion, Architecture = "x64",
        ReportedModules = JsonSerializer.Serialize<List<ModuleInfo>>(
        [
            new(AgentModules.System, true, false, [ModuleActions.Describe]),
            .. (monitoring ? new[] { new ModuleInfo(AgentModules.Monitoring, true, false, [ModuleActions.Status, ModuleActions.Change]) } : []),
        ], ModuleJson.Default.ListModuleInfo),
    };

    private static MonitoringTarget Wazuh(Guid? siteId = null) => new()
    {
        Name = "Wazuh new", Product = MonitoringProducts.Wazuh, SiteId = siteId, Address = "wazuh.example.com", Port = 1514, ManagerVersion = "4.9.2",
    };

    private static MonitoringTarget Checkmk() => new()
    {
        Name = "CMK", Product = MonitoringProducts.Checkmk, SiteUrl = "https://cmk.example.com/mysite", ReceiverPort = 8000, RegistrationUser = "agent_registration",
        OnlyFrom = "10.0.0.5", SiteVersion = "2.3.0p12",
    };

    private static MonitoringClientState Client(string product, string variant, string version, params string[] addresses) => new()
    {
        HostId = Guid.NewGuid(), Product = product, Variant = variant, Version = version, ServiceRunning = true, Addresses = string.Join('\n', addresses),
        Connected = true, CanConfigure = true,
    };

    [Fact]
    public void Evaluate_ComparesClientWithTarget()
    {
        var wazuh = Wazuh();
        Assert.Equal(MonitoringStates.Missing, MonitoringService.Evaluate(wazuh, null));
        Assert.Equal(MonitoringStates.NoTarget, MonitoringService.Evaluate(null, null));
        Assert.Equal(MonitoringStates.Ok, MonitoringService.Evaluate(wazuh, Client("wazuh", "wazuh", "4.9.2-1", "wazuh.example.com:1514")));
        Assert.Equal(MonitoringStates.OtherTarget, MonitoringService.Evaluate(wazuh, Client("wazuh", "wazuh", "4.9.2", "old.example.com:1514")));
        Assert.Equal(MonitoringStates.Outdated, MonitoringService.Evaluate(wazuh, Client("wazuh", "wazuh", "4.7.0", "WAZUH.example.com")));
        Assert.Equal(MonitoringStates.TooNew, MonitoringService.Evaluate(wazuh, Client("wazuh", "wazuh", "4.10.0", "wazuh.example.com")));
        Assert.Equal(MonitoringStates.Ok, MonitoringService.Evaluate(wazuh, Client("wazuh", "wazuh5", "4.9.2", "https://wazuh.example.com:1517")));

        var down = Client("wazuh", "wazuh", "4.9.2", "wazuh.example.com");
        down.Connected = false;
        Assert.Equal(MonitoringStates.Disconnected, MonitoringService.Evaluate(wazuh, down));
        var unsupported = Client("checkmk", MonitoringVariants.CheckmkIni, "1.6.0");
        unsupported.CanConfigure = false;
        Assert.Equal(MonitoringStates.Unsupported, MonitoringService.Evaluate(Checkmk(), unsupported));

        var cmk = Checkmk();
        Assert.Equal(MonitoringStates.Ok, MonitoringService.Evaluate(cmk, Client("checkmk", MonitoringVariants.CheckmkController, "2.3.0p12", "cmk.example.com:8000/mysite")));
        Assert.Equal(MonitoringStates.OtherTarget, MonitoringService.Evaluate(cmk, Client("checkmk", MonitoringVariants.CheckmkController, "2.3.0p12", "cmk.example.com/other")));
        Assert.Equal(MonitoringStates.Outdated, MonitoringService.Evaluate(cmk, Client("checkmk", MonitoringVariants.CheckmkController, "2.2.0p1", "cmk.example.com/mysite")));
        var legacy = Client("checkmk", MonitoringVariants.CheckmkLegacy, "2.3.0p12", "10.0.0.5");
        legacy.Connected = null;
        Assert.Equal(MonitoringStates.Ok, MonitoringService.Evaluate(cmk, legacy));
        legacy.Addresses = "10.0.0.4";
        Assert.Equal(MonitoringStates.OtherTarget, MonitoringService.Evaluate(cmk, legacy));
    }

    [Fact]
    public void Target_SiteBeforeGeneral_AndSpec()
    {
        var site = new Site { Name = "Office" };
        var host = Host(site);
        var general = Wazuh();
        var own = Wazuh(site.Id);
        own.Address = "office-wazuh.example.com";
        Assert.Same(own, MonitoringService.TargetFor([general, own], host, MonitoringProducts.Wazuh));
        Assert.Same(general, MonitoringService.TargetFor([general], host, MonitoringProducts.Wazuh));
        Assert.Null(MonitoringService.TargetFor([general], host, MonitoringProducts.Checkmk));

        var spec = MonitoringService.Spec(Checkmk(), host);
        Assert.Equal("cmk.example.com:8000", spec.Server);
        Assert.Equal("mysite", spec.Site);
        Assert.Equal("web1", spec.Hostname);
        Assert.Equal(["10.0.0.5"], spec.OnlyFrom);
        Assert.Equal("2.3.0p12", spec.SiteVersion);
    }

    [Fact]
    public async Task Store_NotifiesOnlyWhenAConnectedClientDrops()
    {
        var host = Host(new Site { Name = "Home" });
        _db.Hosts.Add(host);
        await _db.SaveChangesAsync();
        var notifier = new Notifier();
        MonitoringClient Wazuh(bool? connected) => new(MonitoringProducts.Wazuh, MonitoringVariants.Wazuh, "4.9.2", true, ["wazuh.example.com:1514"], connected, "status: x");

        await MonitoringService.StoreAsync(_db, host, new MonitoringStatus([Wazuh(false)], ChangesAllowed: true), Now, CancellationToken.None, notifier);
        await _db.SaveChangesAsync();
        Assert.False(notifier.Reader.TryRead(out _)); // neu und gleich getrennt: kein Wechsel
        Assert.True(host.MonitoringChangesAllowed);

        await MonitoringService.StoreAsync(_db, host, new MonitoringStatus([Wazuh(true)]), Now.AddHours(1), CancellationToken.None, notifier);
        await _db.SaveChangesAsync();
        await MonitoringService.StoreAsync(_db, host, new MonitoringStatus([Wazuh(false)]), Now.AddHours(2), CancellationToken.None, notifier);
        await _db.SaveChangesAsync();
        Assert.True(notifier.Reader.TryRead(out var message));
        Assert.Equal(NotificationEvents.MonitoringDisconnected, message!.Event);
        Assert.Equal(Now.AddHours(2), (await _db.MonitoringClients.SingleAsync()).DisconnectedSince);

        await MonitoringService.StoreAsync(_db, host, new MonitoringStatus([Wazuh(false)]), Now.AddHours(3), CancellationToken.None, notifier);
        Assert.False(notifier.Reader.TryRead(out _)); // bleibt getrennt: nicht erneut

        await MonitoringService.StoreAsync(_db, host, new MonitoringStatus([]), Now.AddHours(4), CancellationToken.None, notifier);
        await _db.SaveChangesAsync();
        Assert.Empty(_db.MonitoringClients); // deinstalliert
    }

    [Fact]
    public async Task Prepare_ChecksAgentPolicyAndTarget_AndCarriesTheSecret()
    {
        var site = new Site { Name = "Home" };
        var protection = new EphemeralDataProtectionProvider();
        var packages = new MonitoringPackages(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiop-tests", Guid.NewGuid().ToString("N")) });
        var target = Wazuh();
        target.ProtectedSecret = protection.CreateProtector(MonitoringEndpoints.SecretPurpose).Protect("enroll-pass");

        var old = await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringConfigure, "wazuh", "Wazuh", Host(site, monitoring: false), [target], protection, packages, default);
        Assert.Contains("cannot manage", old.Reason);

        var locked = Host(site);
        locked.MonitoringChangesAllowed = false;
        Assert.Contains("allowMonitoringChanges", (await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringConfigure, "wazuh", "Wazuh", locked, [target], protection, packages, default)).Reason);

        Assert.Contains("No Checkmk target", (await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringConfigure, "checkmk", "Checkmk", Host(site), [target], protection, packages, default)).Reason);

        var ok = await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringConfigure, "wazuh", "Wazuh", Host(site), [target], protection, packages, default);
        Assert.Null(ok.Reason);
        Assert.Equal("enroll-pass", ok.Secret);
        Assert.Equal(SystemChangeKind.MonitoringConfigure, ok.Change!.Kind);
        Assert.Equal("wazuh.example.com", ok.Change.Monitoring!.Address);
        Assert.Equal(AgentModules.Monitoring, AgentModules.ForChange(ok.Change.Kind));

        var uninstall = await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringUninstall, "checkmk", "Checkmk", Host(site), [], protection, packages, default);
        Assert.Null(uninstall.Reason);
        Assert.Null(uninstall.Secret);

        // Installieren ohne erreichbare Paketquelle: verständlicher Grund statt Ausnahme
        var noVersion = Wazuh();
        noVersion.ManagerVersion = null;
        Assert.Contains("manager version", (await MonitoringEndpoints.PrepareAsync(SystemChangeKind.MonitoringInstall, "wazuh", "Wazuh", Host(site), [noVersion], protection, packages, default)).Reason);
        Assert.Equal(Feature.MonitoringClients, ComplianceBaselines.FeatureFor(SystemChangeKind.MonitoringInstall));
    }

    [Fact]
    public void Packages_ComeFromTheOfficialSources()
    {
        var site = new Site { Name = "Home" };
        var linux = Host(site);
        var source = MonitoringPackages.Resolve(Wazuh(), linux);
        Assert.Equal("https://packages.wazuh.com/4.x/apt/pool/main/w/wazuh-agent/wazuh-agent_4.9.2-1_amd64.deb", source.Url);
        Assert.Equal("https://packages.wazuh.com/4.x/checksums/wazuh/4.9.2/wazuh-agent_4.9.2-1_amd64.deb.sha512", source.Sha512Url);
        linux.Architecture = "arm64";
        Assert.EndsWith("_arm64.deb", MonitoringPackages.Resolve(Wazuh(), linux).Url);

        var windows = Host(site, "win1", OsFamily.Windows, "21H2 (Build 20348.2340)");
        Assert.Equal("https://packages.wazuh.com/4.x/windows/wazuh-agent-4.9.2-1.msi", MonitoringPackages.Resolve(Wazuh(), windows).Url);

        var five = Wazuh();
        five.ManagerVersion = "5.0.0";
        Assert.Throws<IntegrationException>(() => MonitoringPackages.Resolve(five, linux));

        Assert.Equal("https://cmk.example.com/mysite/check_mk/agents/check-mk-agent_2.3.0p12-1_all.deb", MonitoringPackages.Resolve(Checkmk(), linux).Url);
        Assert.Equal("https://cmk.example.com/mysite/check_mk/agents/windows/check_mk_agent.msi", MonitoringPackages.Resolve(Checkmk(), windows).Url);

        // 2008 R2 mit einer 2.3-Site: nur mit Legacy-Paket
        var old = Host(site, "old", OsFamily.Windows, "Build 7601");
        Assert.Throws<IntegrationException>(() => MonitoringPackages.Resolve(Checkmk(), old));
        var withLegacy = Checkmk();
        withLegacy.LegacyPackageUrl = "https://files.example.com/cmk/check_mk_agent-2.1.msi";
        var legacy = MonitoringPackages.Resolve(withLegacy, old);
        Assert.Equal("check_mk_agent-2.1.msi", legacy.FileName);
        Assert.Null(legacy.Fingerprint); // anderer Server: kein angepinntes Zertifikat der Site

        Assert.Equal(7601, MonitoringPackages.MinimumWindowsBuild("2.1.0p40"));
        Assert.Equal(9200, MonitoringPackages.MinimumWindowsBuild("2.2.0p1"));
        Assert.Equal(14393, MonitoringPackages.MinimumWindowsBuild("2.4.0"));

        var packages = new MonitoringPackages(new AIOPatcherOptions { DataDirectory = Path.GetTempPath() });
        Assert.Null(packages.Find("../aiopatcher.db"));
        Assert.Null(packages.Find(new string('a', 64)));
    }

    [Fact]
    public void TargetValidation()
    {
        MonitoringEndpoints.TargetRequest Request(string product) => new("Main", product, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        Assert.Contains("address", MonitoringEndpoints.Validate(Request("wazuh")).Keys);
        Assert.Empty(MonitoringEndpoints.Validate(Request("wazuh") with { Address = "10.0.0.1", ManagerVersion = "4.9.2", Groups = "default,linux" }));
        Assert.Contains("protocol", MonitoringEndpoints.Validate(Request("wazuh") with { Address = "x", Protocol = "http" }).Keys);
        Assert.Contains("siteUrl", MonitoringEndpoints.Validate(Request("checkmk") with { SiteUrl = "https://cmk.example.com/" }).Keys);
        Assert.Contains("onlyFrom", MonitoringEndpoints.Validate(Request("checkmk") with { SiteUrl = "https://cmk/s", OnlyFrom = "10.0.0.1 nope" }).Keys);
        Assert.Empty(MonitoringEndpoints.Validate(Request("checkmk") with { SiteUrl = "https://cmk/s", OnlyFrom = "10.0.0.1, 10.1.0.0/16", SiteVersion = "2.3.0p12" }));
        Assert.Contains("product", MonitoringEndpoints.Validate(Request("zabbix")).Keys);
    }

    [Fact]
    public async Task LastAction_NewestMonitoringJobPerProduct()
    {
        var host = Host(new Site { Name = "Home" });
        _db.Hosts.Add(host);
        PatchJob Job(SystemChangeKind kind, string product, JobStatus status, int minutesAgo, string summary) => new()
        {
            HostId = host.Id, Type = JobType.SystemChange, Status = status, RequestedBy = "admin:test", Summary = summary,
            CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo), FinishedAt = DateTime.UtcNow.AddMinutes(-minutesAgo + 1),
            Payload = JsonSerializer.Serialize(new SystemChange(kind, Monitoring: new MonitoringSpec(product)), AgentJsonContext.Default.SystemChange),
        };
        _db.Jobs.AddRange(
            Job(SystemChangeKind.MonitoringInstall, "wazuh", JobStatus.Succeeded, 60, "installed"),
            Job(SystemChangeKind.MonitoringConfigure, "wazuh", JobStatus.Failed, 10, "Registration failed"),
            Job(SystemChangeKind.MonitoringUninstall, "checkmk", JobStatus.Running, 2, "Uninstall Checkmk client"),
            Job(SystemChangeKind.UserLock, "wazuh", JobStatus.Failed, 1, "other change"));
        await _db.SaveChangesAsync();

        var actions = await MonitoringEndpoints.LastActionsAsync(_db, host.Id, default);
        Assert.Equal(2, actions.Count);
        Assert.Equal(("configure", JobStatus.Failed, "Registration failed"), (actions[(host.Id, "wazuh")].Action, actions[(host.Id, "wazuh")].Status, actions[(host.Id, "wazuh")].Summary));
        Assert.Equal(JobStatus.Running, actions[(host.Id, "checkmk")].Status);

        var row = MonitoringEndpoints.Row(host, [], [], actions);
        Assert.Equal("configure", row.Products.Single(p => p.Product == "wazuh").LastAction!.Action);
    }

    [Fact]
    public void ServerModule_AsksForStatusHourlyAndAfterChanges()
    {
        var features = new FeatureFlags(new AIOPatcherOptions());
        var downloads = new AgentDownloads(new AIOPatcherOptions { DataDirectory = Path.GetTempPath() });
        ModuleContext Context(params (string, string, DateTime)[] last) =>
            new(_db, new Notifier(), features, downloads, null, Now, last.ToDictionary(l => (l.Item1, l.Item2), l => l.Item3));

        var module = new MonitoringServerModule();
        var host = Host(new Site { Name = "Home" });
        Assert.Equal(ModuleActions.Status, module.Due(host, Context(), null)?.Action);
        Assert.Null(module.Due(host, Context((AgentModules.Monitoring, ModuleActions.Status, Now.AddMinutes(-10))), null));
        Assert.NotNull(module.Due(host, Context((AgentModules.Monitoring, ModuleActions.Status, Now.AddMinutes(-10)), (AgentModules.Monitoring, ModuleActions.Change, Now.AddMinutes(-5))), null));
        Assert.Null(module.Due(Host(new Site { Name = "Home" }, monitoring: false), Context(), null));
    }
}
