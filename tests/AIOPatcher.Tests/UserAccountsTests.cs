using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class UserAccountsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly UserAccount _admin;

    public UserAccountsTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _admin = User("alice", UserRole.Admin);
        _db.Users.Add(_admin);
        _db.SaveChanges();
    }

    private static UserAccount User(string name, UserRole role, bool active = true) => new()
    {
        Username = name, PasswordHash = active ? "hash" : "", ProtectedTotpSecret = active ? "secret" : "", IsSetupComplete = active, Role = role,
    };

    [Fact]
    public async Task Invite_ResetsCredentials_AndIsFoundOnlyWithMatchingToken()
    {
        var oldStamp = _admin.SecurityStamp;
        var now = DateTime.UtcNow;
        var token = UserAccounts.StartInvite(_admin, now);
        await _db.SaveChangesAsync();

        Assert.False(_admin.IsSetupComplete);
        Assert.Equal("", _admin.PasswordHash);
        Assert.Equal("", _admin.ProtectedTotpSecret);
        Assert.NotEqual(oldStamp, _admin.SecurityStamp);
        Assert.NotEqual(token, _admin.InviteTokenHash); // nur der Hash wird gespeichert

        Assert.Same(_admin, await UserAccounts.FindByInviteAsync(_db, token, now, CancellationToken.None));
        Assert.Null(await UserAccounts.FindByInviteAsync(_db, token + "x", now, CancellationToken.None));
        Assert.Null(await UserAccounts.FindByInviteAsync(_db, "", now, CancellationToken.None));
    }

    [Fact]
    public async Task Invite_Expires_AndIsRejectedForDisabledUsers()
    {
        var now = DateTime.UtcNow;
        var token = UserAccounts.StartInvite(_admin, now);
        await _db.SaveChangesAsync();

        Assert.Null(await UserAccounts.FindByInviteAsync(_db, token, now + UserAccounts.InviteLifetime + TimeSpan.FromSeconds(1), CancellationToken.None));

        _admin.IsDisabled = true;
        await _db.SaveChangesAsync();
        Assert.Null(await UserAccounts.FindByInviteAsync(_db, token, now, CancellationToken.None));
    }

    [Fact]
    public async Task LastActiveAdmin_CannotBeDemotedDisabledOrRemoved()
    {
        var ct = CancellationToken.None;
        Assert.NotNull(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Operator, false, false, ct));
        Assert.NotNull(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Admin, true, false, ct));
        Assert.NotNull(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Admin, false, removesLogin: true, ct));
        Assert.Null(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Admin, false, false, ct));
    }

    [Fact]
    public async Task InvitedOrDisabledAdmins_DoNotCountAsBackup()
    {
        var ct = CancellationToken.None;
        _db.Users.Add(User("bob", UserRole.Admin, active: false));            // Einladung offen
        _db.Users.Add(Disabled(User("carol", UserRole.Admin)));             // deaktiviert
        await _db.SaveChangesAsync();
        Assert.NotNull(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Viewer, false, false, ct));

        _db.Users.Add(User("dave", UserRole.Admin));
        await _db.SaveChangesAsync();
        Assert.Null(await UserAccounts.CheckLastAdminAsync(_db, _admin, UserRole.Viewer, false, false, ct));
    }

    [Fact]
    public async Task NonAdmins_CanAlwaysBeChanged()
    {
        var op = User("eve", UserRole.Operator);
        _db.Users.Add(op);
        await _db.SaveChangesAsync();

        Assert.Null(await UserAccounts.CheckLastAdminAsync(_db, op, UserRole.Viewer, true, true, CancellationToken.None));
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("with space", false)]
    [InlineData("max.mustermann", true)]
    public void Username_Validation(string name, bool ok) => Assert.Equal(ok, UserAccounts.ValidateUsername(name) is null);

    [Fact]
    public void InviteUrl_UsesPublicHostnameAndDashboardPort()
    {
        var url = UserAccounts.InviteUrl(new AIOPatcherOptions { PublicHostname = "patch.example.com", DashboardPort = 9443 }, "abc");
        Assert.Equal("https://patch.example.com:9443/invite/abc", url);
    }

    [Fact]
    public async Task AdminCli_Invite_CreatesUserAndPrintsLink()
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var code = await AdminCli.RunAsync(["admin", "invite", "frank", "--role", "viewer"], _db, new AIOPatcherOptions { PublicHostname = "h" }, CancellationToken.None);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(original);
        }

        var frank = await _db.Users.SingleAsync(u => u.Username == "frank");
        Assert.Equal(UserRole.Viewer, frank.Role);
        Assert.Contains("https://h:", output.ToString());
        Assert.NotNull(frank.InviteTokenHash);
    }

    private static UserAccount Disabled(UserAccount u)
    {
        u.IsDisabled = true;
        return u;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}


public sealed class LanguageTests
{
    [Theory]
    [InlineData("de-DE,de;q=0.9,en;q=0.8", "de")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("fr-FR,fr;q=0.9,de;q=0.5", "de")]
    [InlineData("fr", null)]
    [InlineData("", null)]
    [InlineData("en;q=0.5,de;q=0.9", "de")]
    public void AcceptLanguage_PicksBestSupported(string header, string? expected) => Assert.Equal(expected, L.FromAcceptLanguage(header));

    [Fact]
    public void T_FollowsCurrentLanguage()
    {
        L.Use("de");
        Assert.Equal("Hallo", L.T("Hallo", "Hello"));
        L.Use("en");
        Assert.Equal("Hello", L.T("Hallo", "Hello"));
        L.Use(null);
    }
}
