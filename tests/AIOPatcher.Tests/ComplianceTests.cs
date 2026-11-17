using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Compliance;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Compliance: Parser, Prüfung der Server-Eingaben, Befehlsaufbau, Aussperr-Schutz, Server-Durchsetzung.</summary>
public sealed class ComplianceTests
{
    // --- Firewall-Konfiguration ---------------------------------------------------------------------

    private static FirewallConfig Config(params FirewallRule[] rules) => new(true, FirewallAction.Deny, FirewallAction.Allow, rules);

    [Fact]
    public void Validate_AcceptsTypicalRules()
    {
        var config = Config(
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "22", "10.99.0.0/24", "SSH VPN"),
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "80,443", null, "Web"),
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Udp, "60000-61000", null, null),
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Any, null, "192.168.1.5", "Admin PC"));
        Assert.Empty(FirewallRules.Validate(config));
    }

    [Theory]
    [InlineData("22; rm -rf /", null, null)] // Ports mit Shell-Zeichen
    [InlineData("0", null, null)]
    [InlineData("70000", null, null)]
    [InlineData("22", "evil.example.com", null)] // nur IPs/Netze
    [InlineData("22", "10.0.0.0/33", null)]
    [InlineData("22", null, "x'; Remove-Item C:\\")] // Kommentar mit Anführungszeichen
    public void Validate_RejectsInjectionAndNonsense(string? ports, string? source, string? comment)
    {
        var config = Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, ports, source, comment));
        Assert.NotEmpty(FirewallRules.Validate(config));
    }

    [Fact]
    public void Validate_PortsNeedProtocol()
    {
        Assert.NotEmpty(FirewallRules.Validate(Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Any, "443", null, null))));
    }

    [Fact]
    public void AllowsPort_DetectsLockout()
    {
        Assert.False(FirewallRules.AllowsPort(Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "443", null, null)), 22));
        Assert.True(FirewallRules.AllowsPort(Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "20-25", "10.0.0.0/8", null)), 22));
        Assert.True(FirewallRules.AllowsPort(Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Any, null, "10.0.0.0/8", null)), 3389));
        Assert.True(FirewallRules.AllowsPort(Config() with { Enabled = false }, 22));
    }

    [Fact]
    public void Hash_IsStableAndChangesWithContent()
    {
        var a = Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "22", null, null));
        var b = Config(new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "22", null, null));
        Assert.Equal(FirewallRules.Hash(a), FirewallRules.Hash(b));
        Assert.NotEqual(FirewallRules.Hash(a), FirewallRules.Hash(a with { IncomingDefault = FirewallAction.Allow }));
    }

    // --- ufw ------------------------------------------------------------------------------------------

    [Fact]
    public void UfwStatus_ParsesDefaultsAndManagedRules()
    {
        const string output = """
            Status: active
            Logging: on (low)
            Default: deny (incoming), allow (outgoing), disabled (routed)
            New profiles: skip

            To                         Action      From
            --                         ------      ----
            22/tcp                     ALLOW IN    10.99.0.0/24               # aiopatcher: SSH VPN
            443                        ALLOW IN    Anywhere
            22/tcp (v6)                ALLOW IN    Anywhere (v6)
            """;
        var status = ComplianceParsers.ParseUfwStatus(output);
        Assert.True(status.Active);
        Assert.Equal("deny", status.Incoming);
        Assert.Equal("allow", status.Outgoing);
        Assert.Equal(3, status.Rules.Count);
        Assert.True(status.Rules[0].Managed);
        Assert.False(status.Rules[1].Managed);
        Assert.False(ComplianceParsers.ParseUfwStatus("Status: inactive\n").Active);
    }

    [Fact]
    public void UfwCommands_BuildCompleteRuleSet()
    {
        var commands = ComplianceParsers.UfwCommands(Config(
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "8000-8100", "10.0.0.0/8", "Apps"),
            new FirewallRule(FirewallAction.Deny, FirewallProtocol.Any, null, null, null)));
        Assert.Equal(["--force", "reset"], commands[0]);
        Assert.Equal(["default", "deny", "incoming"], commands[1]);
        Assert.Equal(["allow", "proto", "tcp", "from", "10.0.0.0/8", "to", "any", "port", "8000:8100", "comment", "aiopatcher: Apps"], commands[3]);
        Assert.Equal(["deny", "from", "any", "to", "any", "comment", "aiopatcher"], commands[4]);
        Assert.Equal(["--force", "enable"], commands[^1]);
    }

    [Fact]
    public void WindowsScript_QuotesAndGroupsRules()
    {
        var script = ComplianceParsers.WindowsFirewallScript(Config(
            new FirewallRule(FirewallAction.Allow, FirewallProtocol.Tcp, "3389", "10.99.0.0/24", "RDP VPN")) with { Exclusive = true }, @"C:\ProgramData\it's\disabled.txt");
        Assert.Contains("New-NetFirewallRule -Group 'AIOPatcher' -DisplayName 'AIOPatcher 001: RDP VPN' -Direction Inbound -Profile Any -Action Allow -Protocol TCP -LocalPort '3389' -RemoteAddress '10.99.0.0/24'", script);
        Assert.Contains("'C:\\ProgramData\\it''s\\disabled.txt'", script);
        Assert.Contains("Disable-NetFirewallRule", script);
        Assert.Contains("Set-NetFirewallProfile -All -Enabled True -DefaultInboundAction Block -DefaultOutboundAction Allow", script);
    }

    // --- SSH ------------------------------------------------------------------------------------------

    [Fact]
    public void Ssh_EvaluatesEffectiveConfig()
    {
        const string sshdT = "port 22\npermitrootlogin yes\npasswordauthentication yes\nchallengeresponseauthentication no\npermitemptypasswords no\nmaxauthtries 6\nx11forwarding no\n";
        var settings = ComplianceParsers.EvaluateSsh(ComplianceParsers.ParseSshdEffective(sshdT), fixable: true, null).ToDictionary(s => s.Id);
        Assert.False(settings["ssh.permitRootLogin"].Compliant);
        Assert.False(settings["ssh.passwordAuthentication"].Compliant);
        Assert.True(settings["ssh.kbdInteractiveAuthentication"].Compliant); // aus ChallengeResponse übernommen
        Assert.False(settings["ssh.maxAuthTries"].Compliant);
        Assert.True(settings["ssh.x11Forwarding"].Compliant);

        var proxmoxStyle = ComplianceParsers.EvaluateSsh(ComplianceParsers.ParseSshdEffective("permitrootlogin without-password\n"), true, null);
        Assert.True(proxmoxStyle.Single(s => s.Id == "ssh.permitRootLogin").Compliant);
    }

    [Fact]
    public void Ssh_DropInContainsOnlyChosenFixedSettings()
    {
        var dropIn = ComplianceParsers.SshDropIn(["ssh.permitRootLogin", "ssh.maxAuthTries", "ssh.unknown"]);
        Assert.Contains("PermitRootLogin prohibit-password\n", dropIn);
        Assert.Contains("MaxAuthTries 3\n", dropIn);
        Assert.Equal(["ssh.permitRootLogin", "ssh.maxAuthTries"], ComplianceParsers.SshDropInIds(dropIn));
        Assert.True(ComplianceParsers.SshConfigIncludesDropIns("# x\nInclude /etc/ssh/sshd_config.d/*.conf\nPort 22\n"));
        Assert.False(ComplianceParsers.SshConfigIncludesDropIns("Port 22\n"));
    }

    // --- Benutzer -------------------------------------------------------------------------------------

    private const string Passwd = "root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\nalice:x:1000:1000:Alice Admin,,,:/home/alice:/bin/bash\nbob:x:1001:1001::/home/bob:/bin/bash\nnobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\n";
    private const string Shadow = "root:$y$j9T$abc:19000:0:99999:7:::\ndaemon:*:19000::::::\nalice:$6$xyz:19000:0:99999:7:::\nbob:!$6$old:19000:0:99999:7::1:\n";
    private const string Group = "root:x:0:\nsudo:x:27:alice\nalice:x:1000:\nbob:x:1001:\n";

    [Fact]
    public void LinuxUsers_RootAdminsAndLockedAccounts()
    {
        var users = ComplianceParsers.BuildLinuxUsers(Passwd, Shadow, Group, new Dictionary<string, int> { ["alice"] = 2 },
            new Dictionary<string, DateTime?>(), new DateTime(2026, 11, 17));
        Assert.Equal(["root", "alice", "bob"], users.Select(u => u.Name));

        var root = users[0];
        Assert.True(root.IsBuiltIn && root.IsAdmin && root.Enabled);
        Assert.Equal("set", root.PasswordState);

        var alice = users[1];
        Assert.True(alice.IsAdmin);
        Assert.Equal("Alice Admin", alice.FullName);
        Assert.Equal(2, alice.SshKeys);

        var bob = users[2];
        Assert.False(bob.Enabled); // Ablaufdatum gesetzt (usermod -e 1)
        Assert.Equal("locked", bob.PasswordState);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("!", "none")]
    [InlineData("*", "none")]
    [InlineData("!$6$x", "locked")]
    [InlineData("$y$j9T$x", "set")]
    public void PasswordState_FromShadowField(string field, string expected) =>
        Assert.Equal(expected, ComplianceParsers.PasswordState(field));

    [Fact]
    public void Lastlog_ParsesDatesAndNever()
    {
        const string output = "Username         Port     From             Latest\nroot             pts/0    10.99.0.2        Mon Nov 17 08:05:14 +0100 2025\nbob                                        **Never logged in**\n";
        var result = ComplianceParsers.ParseLastlog(output);
        Assert.Equal(new DateTime(2025, 11, 17, 7, 5, 14, DateTimeKind.Utc), result["root"]);
        Assert.Null(result["bob"]);
    }

    [Fact]
    public void LastAdmin_IsProtected()
    {
        var users = new[]
        {
            new LocalUser("root", true, true, true, "locked", null, 0),
            new LocalUser("alice", true, true, false, "set", null, 0),
            new LocalUser("bob", true, false, false, "set", null, 0),
        };
        Assert.False(ComplianceParsers.HasOtherUsableAdmin(users, "alice")); // root hat kein Passwort und keinen Schlüssel
        Assert.True(ComplianceParsers.HasOtherUsableAdmin(users.Append(new LocalUser("carol", true, true, false, "none", null, 1)), "alice"));
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIB5 user@laptop", true)]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNh", true)]
    [InlineData("command=\"/bin/sh\" ssh-ed25519 AAAAC3Nz", false)] // keine Optionen
    [InlineData("ssh-ed25519 AAAA\nssh-rsa BBBB", false)]
    [InlineData("hello", false)]
    public void PublicKey_OnlyPlainKeys(string key, bool valid) => Assert.Equal(valid, ComplianceParsers.IsValidPublicKey(key));

    [Theory]
    [InlineData("alice", false, true)]
    [InlineData("root", false, true)]
    [InlineData("../etc", false, false)]
    [InlineData("al ice", false, false)]
    [InlineData("Administrator", true, true)]
    [InlineData("x'; Remove-Item", true, false)]
    public void UserNames_AreValidated(string name, bool windows, bool valid) => Assert.Equal(valid, ComplianceParsers.IsValidUserName(name, windows));

    // --- Agent-Policy ---------------------------------------------------------------------------------

    [Fact]
    public void SystemChanges_AreOffByDefaultOnTheHost()
    {
        var job = new AgentJob(Guid.NewGuid(), JobType.SystemChange, PatchScope.Security, false, Change: new SystemChange(SystemChangeKind.UserLock, UserName: "bob"));
        Assert.Equal(JobOutcome.Rejected, JobExecutor.CheckPolicy(job, new AgentPolicy())?.Outcome);
        Assert.Null(JobExecutor.CheckPolicy(job, new AgentPolicy(AllowSystemChanges: true)));
    }

    [Fact]
    public void PolicySet_ChangesOnlyOneKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, AgentPolicy.FileName), "{ \"allowPatch\": \"Security\", \"allowReboot\": false }");
            var policy = AgentPolicy.Set(dir, "allowSystemChanges", "true");
            Assert.True(policy.AllowSystemChanges);
            Assert.Equal(PatchPermission.Security, AgentPolicy.Load(dir).AllowPatch);
            Assert.False(AgentPolicy.Load(dir).AllowReboot);
            Assert.Throws<ArgumentException>(() => AgentPolicy.Set(dir, "allowEverything", "true"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Server ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Server_PasswordIsHandedOverOnceAndWiped_FeaturesEnforced()
    {
        L.Use("en");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var host = new ManagedHost { Site = new Site { Name = "Home" }, Name = "web1", Hostname = "web1", CertificateThumbprint = "x" };
        db.Hosts.Add(host);

        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        var change = new SystemChange(SystemChangeKind.UserSetPassword, UserName: "alice");
        var job = new PatchJob
        {
            HostId = host.Id, Type = JobType.SystemChange, RequestedBy = "admin:test",
            Payload = JsonSerializer.Serialize(change, AgentJsonContext.Default.SystemChange),
            ProtectedSecret = protector.Protect("correct horse battery"),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var features = new FeatureFlags(new AIOPatcherOptions());
        await features.LoadAsync(db, CancellationToken.None);

        // Ab Werk aus: Job wird abgelehnt und das Passwort gelöscht.
        var rejected = await AgentEndpoints.DispatchJobAsync(db, host, null, DateTime.UtcNow, CancellationToken.None, features, protector);
        await db.SaveChangesAsync();
        Assert.Null(rejected);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Null(job.ProtectedSecret);
        Assert.DoesNotContain("correct horse", job.Payload);

        // Eingeschaltet: einmalige Übergabe, danach nichts mehr in der DB.
        await features.SetAsync(db, Feature.UserManagement, true, "test", CancellationToken.None);
        var second = new PatchJob { HostId = host.Id, Type = JobType.SystemChange, RequestedBy = "admin:test", Payload = job.Payload, ProtectedSecret = protector.Protect("correct horse battery") };
        db.Jobs.Add(second);
        await db.SaveChangesAsync();
        var dispatched = await AgentEndpoints.DispatchJobAsync(db, host, null, DateTime.UtcNow, CancellationToken.None, features, protector);
        await db.SaveChangesAsync();
        Assert.Equal("correct horse battery", dispatched?.Change?.Secret);
        Assert.Null((await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == second.Id)).ProtectedSecret);
    }

    [Fact]
    public void Server_FeatureMapping()
    {
        PatchJob Job(SystemChangeKind kind) => new() { RequestedBy = "x", Payload = JsonSerializer.Serialize(new SystemChange(kind), AgentJsonContext.Default.SystemChange) };
        Assert.Equal(Feature.FirewallManagement, ComplianceEndpoints.FeatureFor(Job(SystemChangeKind.FirewallApply)));
        Assert.Equal(Feature.FirewallManagement, ComplianceEndpoints.FeatureFor(Job(SystemChangeKind.FirewallRestoreOriginal)));
        Assert.Equal(Feature.RemoteAccessFixes, ComplianceEndpoints.FeatureFor(Job(SystemChangeKind.RemoteAccessRevert)));
        Assert.Equal(Feature.UserManagement, ComplianceEndpoints.FeatureFor(Job(SystemChangeKind.UserAddSshKey)));
        Assert.All(new[] { Feature.ComplianceChecks, Feature.FirewallManagement, Feature.RemoteAccessFixes, Feature.UserManagement },
            f => Assert.False(FeatureFlags.Definitions.Single(d => d.Feature == f).Default));
    }
}
