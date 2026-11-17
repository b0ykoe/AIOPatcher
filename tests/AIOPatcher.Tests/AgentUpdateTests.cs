using System.Security.Cryptography;
using AIOPatcher.Agent.Maintenance;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Agent-Self-Update: wem der Agent vertraut, Angebot des Servers, Rollback und Bestätigung.</summary>
public sealed class AgentUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    public AgentUpdateTests() => Directory.CreateDirectory(_dir);

    // --- Vertrauen ------------------------------------------------------------------------------------

    [Fact]
    public void ReleaseKeyAgent_AcceptsOnlyReleaseSignatures()
    {
        Assert.Equal("REL", SelfMaintenance.ChooseKey("REL", UpdateSigners.Release, true, "SRV").Key);

        // Ein kompromittierter Server kann einen Release-Agent nicht auf Server-Signaturen herabstufen.
        var (key, reason) = SelfMaintenance.ChooseKey("REL", UpdateSigners.Server, true, "SRV");
        Assert.Null(key);
        Assert.Contains("release key", reason);
    }

    [Fact]
    public void AgentWithoutReleaseKey_UsesRememberedServerKey_UnlessPolicyForbids()
    {
        Assert.Equal("SRV", SelfMaintenance.ChooseKey(null, UpdateSigners.Server, true, "SRV").Key);
        Assert.Contains("trustServerUpdates", SelfMaintenance.ChooseKey(null, UpdateSigners.Server, false, "SRV").Reason);
        Assert.Contains("not known", SelfMaintenance.ChooseKey(null, UpdateSigners.Server, true, null).Reason);
        Assert.Contains("install command", SelfMaintenance.ChooseKey(null, UpdateSigners.Release, true, "SRV").Reason);
        Assert.Contains("unknown signer", SelfMaintenance.ChooseKey(null, "evil", true, "SRV").Reason);
    }

    // --- Angebot des Servers ------------------------------------------------------------------------

    private AgentDownloads Downloads(UpdateSigningKey? key, bool releaseSignature)
    {
        var target = Path.Combine(_dir, "agents", "linux-x64");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "aiopatcher-agent"), "new agent");
        if (releaseSignature)
        {
            File.WriteAllText(Path.Combine(target, "aiopatcher-agent.sig"), "UkVMRUFTRQ==\n");
        }

        return new AgentDownloads(new AIOPatcherOptions { DataDirectory = _dir }, key);
    }

    [Fact]
    public void Offer_WithoutReleaseSignature_IsSignedByServer_AndVerifies()
    {
        using var key = UpdateSigningKey.LoadOrCreate(Path.Combine(_dir, "ca"), NullLogger.Instance);
        var offer = Downloads(key, releaseSignature: false).OfferFor(OsFamily.Linux, "x64", "0.0.1");

        Assert.NotNull(offer);
        Assert.Equal(UpdateSigners.Server, offer.SignedBy);
        var hash = SHA256.HashData("new agent"u8.ToArray());
        Assert.True(SelfMaintenance.VerifySignature(key.PublicKeySpkiBase64, hash, offer.Signature));
        Assert.False(SelfMaintenance.VerifySignature(key.PublicKeySpkiBase64, SHA256.HashData("other"u8.ToArray()), offer.Signature));
    }

    [Fact]
    public void Offer_ReleaseSignatureWins_AndNoKeyMeansNoOffer()
    {
        using var key = UpdateSigningKey.LoadOrCreate(Path.Combine(_dir, "ca"), NullLogger.Instance);
        var release = Downloads(key, releaseSignature: true).OfferFor(OsFamily.Linux, "x64", "0.0.1");
        Assert.Equal(UpdateSigners.Release, release?.SignedBy);
        Assert.Equal("UkVMRUFTRQ==", release?.Signature);

        File.Delete(Path.Combine(_dir, "agents", "linux-x64", "aiopatcher-agent.sig"));
        Assert.Null(new AgentDownloads(new AIOPatcherOptions { DataDirectory = _dir }).OfferFor(OsFamily.Linux, "x64", "0.0.1"));
    }

    [Fact]
    public void Offer_BlockedVersion_IsNotOfferedAgain()
    {
        using var key = UpdateSigningKey.LoadOrCreate(Path.Combine(_dir, "ca"), NullLogger.Instance);
        var downloads = Downloads(key, releaseSignature: false);
        Assert.NotNull(downloads.OfferFor(OsFamily.Linux, "x64", "0.0.1", blockedVersion: "0.0.0-other"));
        Assert.Null(downloads.OfferFor(OsFamily.Linux, "x64", "0.0.1", blockedVersion: AgentDownloads.AgentVersion));
    }

    [Fact]
    public void ServerKey_IsCreatedOnce_AndReloaded()
    {
        string first;
        using (var key = UpdateSigningKey.LoadOrCreate(Path.Combine(_dir, "ca"), NullLogger.Instance))
        {
            first = key.PublicKeySpkiBase64;
        }

        using var again = UpdateSigningKey.LoadOrCreate(Path.Combine(_dir, "ca"), NullLogger.Instance);
        Assert.Equal(first, again.PublicKeySpkiBase64);
    }

    // --- Rollback und Bestätigung auf dem Agent -----------------------------------------------------

    [Fact]
    public void Startup_NewVersionRunning_WaitsForConfirmation()
    {
        var backup = Path.Combine(_dir, "agent.bak");
        File.WriteAllText(backup, "old");
        UpdateState.SavePending(_dir, new PendingSelfUpdate("0.1.0", "0.2.0", DateTime.UtcNow));

        var pending = UpdateState.Resume(_dir, "0.2.0", backup);

        Assert.Equal("0.1.0", pending?.FromVersion);
        Assert.True(File.Exists(backup)); // bis zur Bestätigung behalten, der Wächter braucht sie
        Assert.Null(UpdateState.LoadResult(_dir));

        UpdateState.Confirm(_dir, backup);
        Assert.Null(UpdateState.LoadPending(_dir));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Startup_OldVersionAfterWatchdog_ReportsRollback()
    {
        var backup = Path.Combine(_dir, "agent.bak");
        File.WriteAllText(backup, "old");
        UpdateState.SavePending(_dir, new PendingSelfUpdate("0.1.0", "0.2.0", DateTime.UtcNow));

        Assert.Null(UpdateState.Resume(_dir, "0.1.0", backup));

        var result = UpdateState.LoadResult(_dir);
        Assert.Equal(AgentUpdateOutcome.RolledBack, result?.Outcome);
        Assert.Equal("0.2.0", result?.ToVersion);
        Assert.Null(UpdateState.LoadPending(_dir));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Watchdogs_OnlyActWhenUpdateIsUnconfirmed()
    {
        var linux = SelfMaintenance.LinuxWatchdog("/usr/local/bin/aiopatcher-agent", "/usr/local/bin/aiopatcher-agent.bak", "/var/lib/aiopatcher-agent/update-pending.json");
        Assert.StartsWith("test -f '/var/lib/aiopatcher-agent/update-pending.json' || exit 0;", linux);
        Assert.Contains("mv -f '/usr/local/bin/aiopatcher-agent.rollback' '/usr/local/bin/aiopatcher-agent'", linux);

        var windows = SelfMaintenance.WindowsWatchdog(@"C:\Program Files\AIOPatcher\aiopatcher-agent.exe", @"C:\Program Files\AIOPatcher\aiopatcher-agent.exe.bak", @"C:\ProgramData\it's\update-pending.json");
        Assert.Contains(@"if (Test-Path -LiteralPath 'C:\ProgramData\it''s\update-pending.json')", windows);
        Assert.Contains(@"Copy-Item -LiteralPath 'C:\Program Files\AIOPatcher\aiopatcher-agent.exe.bak' -Destination 'C:\Program Files\AIOPatcher\aiopatcher-agent.exe'", windows);
    }

    // --- Server verarbeitet den Bericht -------------------------------------------------------------

    [Fact]
    public async Task Report_RollbackBlocksVersion_InstalledClearsBlock()
    {
        L.Use("en");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var host = new ManagedHost { Site = new Site { Name = "Home" }, Name = "pbs", Hostname = "pbs", CertificateThumbprint = "x" };
        db.Hosts.Add(host);
        await db.SaveChangesAsync();
        var notifier = new Notifier();

        AgentEndpoints.RecordUpdateReport(db, notifier, host, new AgentUpdateReport("0.1.0", "0.2.0", AgentUpdateOutcome.RolledBack, "crashed", DateTime.UtcNow), DateTime.UtcNow);
        await db.SaveChangesAsync();

        Assert.Equal("0.2.0", host.AgentUpdateBlockedVersion);
        Assert.Equal(AgentUpdateOutcome.RolledBack, host.AgentUpdateOutcome);
        Assert.Contains(db.AuditEntries, a => a.Action == "agent.update.rolled-back");

        AgentEndpoints.RecordUpdateReport(db, notifier, host, new AgentUpdateReport("0.1.0", "0.2.1", AgentUpdateOutcome.Installed, null, DateTime.UtcNow), DateTime.UtcNow);
        await db.SaveChangesAsync();

        Assert.Null(host.AgentUpdateBlockedVersion);
        Assert.Equal(AgentUpdateOutcome.Installed, (await db.Hosts.AsNoTracking().SingleAsync()).AgentUpdateOutcome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
