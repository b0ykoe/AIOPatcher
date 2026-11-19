using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>"Erste Schritte": Status je Schritt, Admin-Schritte nur für Admins, Überspringen pro Benutzer.</summary>
public sealed class GettingStartedTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());

    public GettingStartedTests()
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

    private static UserAccount User(UserRole role) => new() { Username = "u-" + role, PasswordHash = "x", ProtectedTotpSecret = "x", Role = role, IsSetupComplete = true };

    private async Task<Dictionary<string, string>> StatusAsync(UserAccount user, bool publicAccess = false, bool sso = false) =>
        (await GettingStartedEndpoints.ComputeAsync(user, _db, _features, publicAccess, sso, default)).Steps.ToDictionary(s => s.Key, s => s.Status);

    [Fact]
    public async Task FreshServer_EverythingOpen()
    {
        var status = await StatusAsync(User(UserRole.Admin));
        Assert.Equal(GettingStartedEndpoints.Steps.Count, status.Count);
        Assert.All(status.Values, s => Assert.Equal("open", s));
    }

    [Fact]
    public async Task Basics_DoneFromData()
    {
        var site = new Site { Name = "Home" };
        _db.Hosts.Add(new ManagedHost { Site = site, Name = "nas", Hostname = "nas", CertificateThumbprint = "a" });
        await _db.SaveChangesAsync();

        var status = await StatusAsync(User(UserRole.Admin), publicAccess: true, sso: true);
        Assert.Equal("done", status["site"]);
        Assert.Equal("done", status["host"]);
        Assert.Equal("open", status["plan"]);
        Assert.Equal("done", status["publicAccess"]);
        Assert.Equal("done", status["sso"]);
    }

    [Fact]
    public async Task RevokedHost_DoesNotCount()
    {
        _db.Hosts.Add(new ManagedHost { Site = new Site { Name = "Home" }, Name = "old", Hostname = "old", CertificateThumbprint = "a", IsRevoked = true });
        await _db.SaveChangesAsync();
        Assert.Equal("open", (await StatusAsync(User(UserRole.Admin)))["host"]);
    }

    [Fact]
    public async Task NonAdmins_DoNotSeeAdminSteps()
    {
        var keys = (await StatusAsync(User(UserRole.Operator))).Keys;
        Assert.Contains("host", keys);
        Assert.Contains("passkey", keys);
        Assert.DoesNotContain("notifications", keys);
        Assert.DoesNotContain("publicAccess", keys);
        Assert.DoesNotContain("sso", keys);
    }

    [Fact]
    public async Task Skip_AndShowAgain()
    {
        var user = User(UserRole.Admin);
        Assert.True(GettingStartedEndpoints.SetSkipped(user, "sso", true));
        Assert.True(GettingStartedEndpoints.SetSkipped(user, "monitoring", true));
        Assert.Equal("monitoring,sso", user.SkippedSteps);
        Assert.Equal("skipped", (await StatusAsync(user))["sso"]);

        // Erledigt schlägt übersprungen
        Assert.Equal("done", (await StatusAsync(user, sso: true))["sso"]);

        Assert.True(GettingStartedEndpoints.SetSkipped(user, "sso", false));
        Assert.True(GettingStartedEndpoints.SetSkipped(user, "monitoring", false));
        Assert.Null(user.SkippedSteps);
    }

    [Fact]
    public void Skip_UnknownStepIsRejected() =>
        Assert.False(GettingStartedEndpoints.SetSkipped(User(UserRole.Admin), "nope", true));
}
