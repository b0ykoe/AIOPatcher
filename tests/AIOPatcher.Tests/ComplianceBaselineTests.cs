using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Compliance;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Compliance-Vorgaben: Soll/Ist, Reihenfolge beim Angleichen, Zuordnung, Schalter, Sammel-Job im Agent.</summary>
public sealed class ComplianceBaselineTests
{
    private const string AdminKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIIrZ1ds9i3zqA5JY12ZH4g5tX0/S1F1jEAhHelelcH1D test@homelab";
    private const string AdminFingerprint = "SHA256:pnIk4Obq8WxN/vcOemRNwfTYMGfp7lessHef7fybw6o";
    private static readonly SshKeyInfo Foreign = new("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "ssh-rsa", "old laptop");

    private static ComplianceReport LinuxReport(IReadOnlyList<LocalUser> users, string permitRootLogin = "yes", string? hash = null) => new(
        new FirewallStatus("ufw", true, true, "deny", "allow", [], 0, hash, true),
        [
            new RemoteAccessSetting("ssh.permitRootLogin", permitRootLogin, permitRootLogin != "yes", true),
            new RemoteAccessSetting("ssh.passwordAuthentication", "yes", false, true),
            new RemoteAccessSetting("ssh.maxAuthTries", "3", true, true),
            new RemoteAccessSetting("ssh.port", "22", true, true),
        ],
        users,
        DateTime.UtcNow);

    private static LocalUser User(string name, bool admin = false, bool enabled = true, bool builtIn = false, string password = "set", IReadOnlyList<SshKeyInfo>? keys = null) =>
        new(name, enabled, admin, builtIn, password, null, keys?.Count ?? 0, null, keys ?? []);

    private static readonly BaselineRules Strict = new(
        [new SettingRule("ssh.permitRootLogin", "prohibit-password"), new SettingRule("ssh.passwordAuthentication", "no"), new SettingRule("ssh.maxAuthTries", "4"),
         new SettingRule("rdp.nla", "1")],
        [new UserRule("ops", UserRequirement.Admin), new UserRule("guest", UserRequirement.Disabled), new UserRule("winadmin", UserRequirement.Admin, BaselineOs.Windows)],
        OnlyListedAdmins: true,
        [new KeyRule("ops", AdminKey), new KeyRule("alice", AdminKey)],
        ForeignKeyPolicy.Remove,
        null);

    [Fact]
    public void Evaluate_Linux_ViolationsWithFixes_InSafeOrder()
    {
        L.Use("en");
        var report = LinuxReport([
            User("root", admin: true, builtIn: true),
            User("alice", admin: true, keys: [Foreign]),
            User("guest"),
        ]);

        var results = ComplianceBaselines.Evaluate(Strict, OsFamily.Linux, report, null, new Dictionary<Guid, FirewallTarget>());
        var byKey = results.ToDictionary(r => r.Key);

        Assert.Equal(RuleState.Violation, byKey["setting:ssh.permitRootLogin"].State);
        Assert.Equal(RuleState.Violation, byKey["setting:ssh.passwordAuthentication"].State);
        Assert.Equal(RuleState.Ok, byKey["setting:ssh.maxAuthTries"].State); // 3 ≤ 4
        Assert.DoesNotContain("setting:rdp.nla", byKey.Keys); // Windows-Regel
        Assert.DoesNotContain("user:winadmin", byKey.Keys);
        Assert.Equal(("missing", true), (byKey["user:ops"].Actual, byKey["user:ops"].Fixable));
        Assert.Equal(RuleState.Violation, byKey["user:guest"].State);
        Assert.Equal(RuleState.Violation, byKey["admin:alice"].State); // nicht in der Liste; root als eingebautes Konto ausgenommen
        Assert.DoesNotContain("admin:root", byKey.Keys);
        Assert.True(byKey[$"key:ops:{AdminFingerprint}"].Fixable); // ops wird angelegt
        Assert.Equal(RuleState.Violation, byKey[$"key:alice:{AdminFingerprint}"].State);
        Assert.True(byKey[$"foreignKey:alice:{Foreign.Fingerprint}"].Fixable);

        var steps = ComplianceBaselines.Steps(results);
        Assert.Equal(
            [SystemChangeKind.UserCreate, SystemChangeKind.UserAddSshKey, SystemChangeKind.UserAddSshKey, SystemChangeKind.RemoteAccessSet,
             SystemChangeKind.UserRemoveSshKey, SystemChangeKind.UserRevokeAdmin, SystemChangeKind.UserLock],
            steps.Select(s => s.Kind));
        Assert.True(steps[0].Admin);
        var set = steps.Single(s => s.Kind == SystemChangeKind.RemoteAccessSet).Values!;
        Assert.Equal("prohibit-password", set["ssh.permitRootLogin"]);
        Assert.Equal("no", set["ssh.passwordAuthentication"]);
        Assert.Equal(Foreign.Fingerprint, steps.Single(s => s.Kind == SystemChangeKind.UserRemoveSshKey).SshKey);
    }

    [Fact]
    public void Evaluate_Compliant_OldAgentAndReportOnly()
    {
        var compliant = LinuxReport([User("ops", admin: true, keys: [new SshKeyInfo(AdminFingerprint, "ssh-ed25519", null)]), User("guest", enabled: false)],
            permitRootLogin: "without-password");
        var rules = Strict with { Settings = [new SettingRule("ssh.permitRootLogin", "prohibit-password")], Keys = [new KeyRule("ops", AdminKey)] };
        Assert.All(ComplianceBaselines.Evaluate(rules, OsFamily.Linux, compliant, null, new Dictionary<Guid, FirewallTarget>()), r => Assert.Equal(RuleState.Ok, r.State));

        // Älterer Agent meldet keine Schlüssel: unbekannt statt Fehlalarm
        var old = LinuxReport([new LocalUser("ops", true, true, false, "set", null, 1)]);
        Assert.Equal("oldAgent", ComplianceBaselines.Evaluate(rules, OsFamily.Linux, old, null, new Dictionary<Guid, FirewallTarget>()).Single(r => r.Category == "key").Reason);

        // Fremde Schlüssel nur melden: Abweichung, aber kein Schritt
        var report = LinuxReport([User("ops", admin: true, keys: [new SshKeyInfo(AdminFingerprint, "ssh-ed25519", null), Foreign])]);
        var results = ComplianceBaselines.Evaluate(rules with { ForeignKeys = ForeignKeyPolicy.Report }, OsFamily.Linux, report, null, new Dictionary<Guid, FirewallTarget>());
        var foreign = results.Single(r => r.Category == "foreignKey");
        Assert.Equal(("reportOnly", false), (foreign.Reason, foreign.Fixable));
        Assert.DoesNotContain(ComplianceBaselines.Steps(results), s => s.Kind == SystemChangeKind.UserRemoveSshKey);

        Assert.All(ComplianceBaselines.Evaluate(rules, OsFamily.Linux, null, null, new Dictionary<Guid, FirewallTarget>()), r => Assert.Equal("noReport", r.Reason));
    }

    [Fact]
    public void Evaluate_Windows_RdpGroupPolicyAndFirewall()
    {
        var profile = Guid.NewGuid();
        var version = Guid.NewGuid();
        var config = new FirewallConfig(true, FirewallAction.Deny, FirewallAction.Allow, []);
        var target = new FirewallTarget(profile, "Server", version, 3, FirewallRules.Hash(config), config);
        var report = new ComplianceReport(
            new FirewallStatus("windows", true, true, "deny", "allow", [], 0, "oldhash", true),
            [new RemoteAccessSetting("rdp.nla", "0", false, false, "group-policy"), new RemoteAccessSetting("rdp.enabled", "on", true, false)],
            [User("Administrator", admin: true, builtIn: true), User("winadmin", admin: false)],
            DateTime.UtcNow);
        var rules = Strict with { Settings = [new SettingRule("rdp.nla", "1"), new SettingRule("rdp.enabled", "off"), new SettingRule("ssh.passwordAuthentication", "no")], FirewallProfileId = profile };

        var results = ComplianceBaselines.Evaluate(rules, OsFamily.Windows, report, null, new Dictionary<Guid, FirewallTarget> { [profile] = target }).ToDictionary(r => r.Key);

        Assert.Equal(("groupPolicy", false), (results["setting:rdp.nla"].Reason, results["setting:rdp.nla"].Fixable));
        Assert.True(results["setting:rdp.enabled"].Fixable);
        Assert.DoesNotContain("setting:ssh.passwordAuthentication", results.Keys);
        Assert.Equal("present", results["user:winadmin"].Actual);
        Assert.Equal(SystemChangeKind.UserGrantAdmin, Assert.Single(results["user:winadmin"].Fixes).Kind);
        Assert.Equal(("other", version), (results["firewall"].Actual, results["firewall"].FirewallVersionId));
        Assert.DoesNotContain(results.Keys, k => k.StartsWith("key:", StringComparison.Ordinal)); // Schlüssel nur Linux

        var applied = report with { Firewall = report.Firewall with { AppliedConfigHash = target.LatestHash } };
        Assert.Equal(RuleState.Ok, ComplianceBaselines.Evaluate(rules, OsFamily.Windows, applied, version, new Dictionary<Guid, FirewallTarget> { [profile] = target })
            .Single(r => r.Key == "firewall").State);
    }

    [Fact]
    public void Validate_RejectsUnknownSettingsBadValuesAndKeys()
    {
        L.Use("en");
        Assert.Empty(ComplianceBaselines.Validate(Strict));
        var bad = Strict with
        {
            Settings = [new SettingRule("ssh.permitRootLogin", "sometimes"), new SettingRule("ssh.bogus", "x"), new SettingRule("ssh.port", "0")],
            Keys = [new KeyRule("ops", "not a key")],
        };
        Assert.Equal(4, ComplianceBaselines.Validate(bad).Count);
    }

    [Fact]
    public void Resolve_SelectedThenRuleThenOthers()
    {
        var site = Guid.NewGuid();
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var hosts = new[] { new ComplianceBaselines.HostInfo(a, site, "web"), new(b, site, "db"), new(c, Guid.NewGuid(), "web"), new(d, Guid.NewGuid(), null) };
        var older = new ComplianceBaseline { Name = "Web", RulesJson = "{}", MemberTags = "web", CreatedAt = DateTime.UtcNow.AddDays(-2) };
        var others = new ComplianceBaseline { Name = "Rest", RulesJson = "{}", IncludeOthers = true, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var explicitOne = new ComplianceBaseline { Name = "Pinned", RulesJson = "{}", CreatedAt = DateTime.UtcNow };
        explicitOne.Hosts.Add(new ComplianceBaselineHost { HostId = a, BaselineId = explicitOne.Id });

        var members = ComplianceBaselines.Resolve([older, others, explicitOne], hosts);

        Assert.Equal(("Pinned", PlanVia.Host), (members[a].BaselineName, members[a].Via));
        Assert.Equal(("Web", PlanVia.Rule), (members[c].BaselineName, members[c].Via));
        Assert.Equal(("Rest", PlanVia.Others), (members[b].BaselineName, members[b].Via));
        Assert.Equal("Rest", members[d].BaselineName);
    }

    [Fact]
    public async Task BatchJob_BlockedWhileAnyStepFeatureIsOff()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var features = new FeatureFlags(new AIOPatcherOptions());
        await features.LoadAsync(db, CancellationToken.None);
        var batch = new SystemChange(SystemChangeKind.Batch, Steps: [
            new SystemChange(SystemChangeKind.UserCreate, UserName: "ops"),
            new SystemChange(SystemChangeKind.RemoteAccessSet, Values: new Dictionary<string, string> { ["ssh.passwordAuthentication"] = "no" })]);
        var job = new PatchJob { RequestedBy = "admin:x", Payload = JsonSerializer.Serialize(batch, AgentJsonContext.Default.SystemChange) };

        Assert.Equal([Feature.UserManagement, Feature.RemoteAccessFixes], ComplianceEndpoints.FeaturesFor(batch));
        Assert.Equal(Feature.UserManagement, ComplianceEndpoints.BlockedFeature(job, features));
        await features.SetAsync(db, Feature.UserManagement, true, "test", CancellationToken.None);
        Assert.Equal(Feature.RemoteAccessFixes, ComplianceEndpoints.BlockedFeature(job, features));
        await features.SetAsync(db, Feature.RemoteAccessFixes, true, "test", CancellationToken.None);
        Assert.Null(ComplianceEndpoints.BlockedFeature(job, features));
        Assert.Equal(AgentModules.Users, AgentModules.ForChange(SystemChangeKind.Batch));
        Assert.Equal(AgentModules.RemoteAccess, AgentModules.ForChange(SystemChangeKind.RemoteAccessSet));
    }

    // --- Agent: Sammel-Job ----------------------------------------------------------------------------

    private sealed class RecordingProvider(SystemChangeKind failOn) : IComplianceProvider
    {
        public List<SystemChangeKind> Applied { get; } = [];

        public Task<FirewallStatus> FirewallAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteAccessSetting>> RemoteAccessAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<LocalUser>> UsersAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<JobExecutionResult> ApplyAsync(SystemChange change, Func<CancellationToken, Task<bool>> serverReachable, Action<string> log, CancellationToken ct)
        {
            Applied.Add(change.Kind);
            return Task.FromResult(change.Kind == failOn
                ? new JobExecutionResult(JobOutcome.Rejected, "No administrator has an SSH key.")
                : new JobExecutionResult(JobOutcome.Succeeded, $"{change.Kind} ok."));
        }
    }

    private sealed class NoInventory : IPlatformInventory
    {
        public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct) => Task.FromResult(new SystemInfo("Debian", "13", "6.12", false, false));

        public Task<IReadOnlyList<AvailableUpdate>?> ScanUpdatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AvailableUpdate>?>([]);
    }

    private sealed class NoPatcher : IPatchPlatform
    {
        public Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, bool fixBrokenSources, Action<string> log, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Agent_BatchStopsAtFirstFailure_AndNeedsPolicy()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        using var cert = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var state = new AgentState
        {
            Directory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")),
            Config = new AgentConfig("https://localhost:1", Guid.NewGuid(), Guid.NewGuid()),
            CaCertificate = cert,
            ClientCertificate = cert,
        };
        var provider = new RecordingProvider(SystemChangeKind.RemoteAccessSet);
        var executor = new JobExecutor(new NoInventory(), new NoPatcher(), NullLogger<JobExecutor>.Instance, provider, state);
        var batch = new SystemChange(SystemChangeKind.Batch, Steps: [
            new SystemChange(SystemChangeKind.UserCreate, UserName: "ops"),
            new SystemChange(SystemChangeKind.RemoteAccessSet, Values: new Dictionary<string, string> { ["ssh.passwordAuthentication"] = "no" }),
            new SystemChange(SystemChangeKind.UserLock, UserName: "guest")]);
        var job = new AgentJob(Guid.NewGuid(), JobType.SystemChange, PatchScope.Security, false, Change: batch);

        var denied = await executor.ExecuteAsync(job, new AgentPolicy(), _ => { }, CancellationToken.None);
        Assert.Equal(JobOutcome.Rejected, denied.Outcome);
        Assert.Empty(provider.Applied); // policy.json allowSystemChanges fehlt

        var result = await executor.ExecuteAsync(job, new AgentPolicy(AllowSystemChanges: true), _ => { }, CancellationToken.None);
        Assert.Equal(JobOutcome.Rejected, result.Outcome);
        Assert.Equal([SystemChangeKind.UserCreate, SystemChangeKind.RemoteAccessSet], provider.Applied);
        Assert.Contains("Step 2/3", result.Summary);
        Assert.Contains("UserCreate ok.", result.Summary);
    }
}
