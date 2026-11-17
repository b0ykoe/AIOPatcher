using System.Security.Claims;
using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Sso;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>SSO: Gruppen aus dem Login, Rolle aus der Zuordnung, Verknüpfung mit lokalen Benutzern.</summary>
public sealed class SsoTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public SsoTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    private static SsoSettingsRecord Settings(bool autoCreate = true, bool link = true, UserRole? defaultRole = null) => new()
    {
        Enabled = true,
        Authority = "https://sso.example.com/realms/homelab",
        ClientId = "aiopatcher",
        AutoCreateUsers = autoCreate,
        LinkByUsername = link,
        DefaultRole = defaultRole,
        RoleMappingsJson = JsonSerializer.Serialize(new List<SsoRoleMapping>
        {
            new("/aiopatcher-admins", UserRole.Admin),
            new("aiopatcher-ops", UserRole.Operator),
            new("homelab", UserRole.Viewer),
        }, SsoService.JsonOptions),
    };

    private static ClaimsPrincipal Login(string sub, string username, params string[] groups) =>
        new(new ClaimsIdentity([new Claim("sub", sub), new Claim("preferred_username", username), .. groups.Select(g => new Claim("groups", g))], "test"));

    [Fact]
    public void Groups_KeycloakPathsJsonArraysAndRealmRoles()
    {
        Assert.Equal(["aiopatcher-admins", "homelab"], SsoService.Groups(Login("1", "a", "/aiopatcher-admins", "/homelab"), "groups"));

        var jsonArray = new ClaimsPrincipal(new ClaimsIdentity([new Claim("groups", "[\"/a\",\"b\"]")]));
        Assert.Equal(["a", "b"], SsoService.Groups(jsonArray, "groups"));

        var realm = new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access", "{\"roles\":[\"offline_access\",\"aiopatcher-ops\"]}")]));
        Assert.Equal(["offline_access", "aiopatcher-ops"], SsoService.Groups(realm, "realm_access.roles"));
    }

    [Fact]
    public void Role_HighestWins_DefaultOrDeny()
    {
        var mappings = SsoService.Mappings(Settings());
        Assert.Equal(UserRole.Admin, SsoService.RoleFor(["homelab", "aiopatcher-admins"], mappings, null));
        Assert.Equal(UserRole.Operator, SsoService.RoleFor(["AIOPATCHER-OPS"], mappings, null)); // Groß/klein egal
        Assert.Null(SsoService.RoleFor(["family"], mappings, null));
        Assert.Equal(UserRole.Viewer, SsoService.RoleFor(["family"], mappings, UserRole.Viewer));
    }

    [Fact]
    public async Task FirstLogin_CreatesSsoOnlyUser_LaterLoginsFollowGroups()
    {
        var first = await SsoService.ResolveUserAsync(_db, Settings(), Login("kc-1", "alice", "/homelab"), CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.True(first.Created);
        Assert.Equal(UserRole.Viewer, first.User!.Role);
        Assert.Equal("", first.User.PasswordHash); // nur SSO
        var stamp = first.User.SecurityStamp;

        // Gruppe beim Anbieter geändert → Rolle beim nächsten Login, alte Sitzungen ungültig.
        var second = await SsoService.ResolveUserAsync(_db, Settings(), Login("kc-1", "alice", "/aiopatcher-admins"), CancellationToken.None);
        Assert.Equal(first.User.Id, second.User!.Id);
        Assert.Equal(UserRole.Admin, second.User.Role);
        Assert.NotEqual(stamp, second.User.SecurityStamp);

        // Aus allen Gruppen entfernt → abgelehnt.
        var removed = await SsoService.ResolveUserAsync(_db, Settings(), Login("kc-1", "alice"), CancellationToken.None);
        Assert.Null(removed.User);
    }

    [Fact]
    public async Task ExistingLocalUser_IsLinkedByUsername_OnlyWhenAllowed()
    {
        _db.Users.Add(new UserAccount { Username = "Bob", PasswordHash = "hash", ProtectedTotpSecret = "x", IsSetupComplete = true, Role = UserRole.Viewer });
        await _db.SaveChangesAsync();

        var refused = await SsoService.ResolveUserAsync(_db, Settings(link: false), Login("kc-2", "bob", "aiopatcher-ops"), CancellationToken.None);
        Assert.Null(refused.User);
        Assert.Contains("already exists", refused.Error);

        var linked = await SsoService.ResolveUserAsync(_db, Settings(), Login("kc-2", "bob", "aiopatcher-ops"), CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.True(linked.Linked);
        Assert.Equal("Bob", linked.User!.Username);
        Assert.Equal(UserRole.Operator, linked.User.Role);
        Assert.Equal("hash", linked.User.PasswordHash); // lokaler Zugang bleibt
        Assert.Equal("https://sso.example.com/realms/homelab|kc-2", linked.User.SsoSubject);
    }

    [Fact]
    public async Task DisabledOrUnknownUsers_AreDenied()
    {
        var noCreate = await SsoService.ResolveUserAsync(_db, Settings(autoCreate: false), Login("kc-3", "carol", "homelab"), CancellationToken.None);
        Assert.Null(noCreate.User);

        _db.Users.Add(new UserAccount { Username = "dave", PasswordHash = "", ProtectedTotpSecret = "", IsSetupComplete = true, IsDisabled = true, SsoSubject = "https://sso.example.com/realms/homelab|kc-4" });
        await _db.SaveChangesAsync();
        var disabled = await SsoService.ResolveUserAsync(_db, Settings(), Login("kc-4", "dave", "aiopatcher-admins"), CancellationToken.None);
        Assert.Null(disabled.User);
        Assert.Contains("disabled", disabled.Error);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
