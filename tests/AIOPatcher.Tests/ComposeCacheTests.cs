using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Compose-Editor ohne Wartezeit: Hash in der Inventur, Inhalt nur bei neuem Hash, verschlüsselt, ohne Freigabe nichts.</summary>
public sealed class ComposeCacheTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector = ComposeCache.Protector(new EphemeralDataProtectionProvider());
    private readonly ManagedHost _host;
    private static readonly DateTime Now = new(2026, 11, 25, 20, 0, 0, DateTimeKind.Utc);
    private const string Content = "services:\n  db:\n    image: mariadb\n    environment:\n      MYSQL_ROOT_PASSWORD: geheim123\n";

    public ComposeCacheTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "docker", Hostname = "docker", CertificateThumbprint = "x", OsFamily = OsFamily.Linux,
            CertificateExpiresAt = Now.AddYears(1),
            ReportedModules = JsonSerializer.Serialize(new List<ModuleInfo> { new(AgentModules.Docker, true, false, [ModuleActions.Inventory, ModuleActions.ComposeCache]) },
                ModuleJsonContext.Default.ListModuleInfo),
            ReportedPolicy = PolicyReport.Serialize(new AgentPolicyReport("All", AllowContainerManagement: true, AllowComposeEdit: true)),
        };
        _db.Hosts.Add(_host);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static ComposeFileHash Hash(string content, string project = "web") =>
        new(project, $"/opt/stacks/{project}/compose.yaml", ComposeCache.Sha256(content), content.Length);

    [Fact]
    public async Task ContentIsFetchedOnlyForNewHashes_AndStoredEncrypted()
    {
        // Erste Meldung: Inhalt fehlt → anfordern
        Assert.Equal(["web"], await ComposeCache.SyncAsync(_db, _host, [Hash(Content)], featureOn: true, Now, CancellationToken.None));
        await _db.SaveChangesAsync();
        Assert.Equal(["web"], ComposeCache.TakeRequest(_host)!.Projects);

        // Inhalt passt nicht zum Hash: nicht übernehmen
        Assert.Equal(0, await ComposeCache.StoreAsync(_db, _host, [new ComposeFileResult("web", "/opt/stacks/web/compose.yaml", Content, new string('0', 64))], _protector, Now, CancellationToken.None));
        Assert.Equal(1, await ComposeCache.StoreAsync(_db, _host, [new ComposeFileResult("web", "/opt/stacks/web/compose.yaml", Content, ComposeCache.Sha256(Content))], _protector, Now, CancellationToken.None));
        await _db.SaveChangesAsync();

        var row = await _db.ComposeCache.SingleAsync();
        Assert.DoesNotContain("geheim123", row.ProtectedContent); // verschlüsselt
        var cached = await ComposeCache.ReadAsync(_db, _host.Id, "web", _protector, CancellationToken.None);
        Assert.Equal(Content, cached!.Value.File.Content);

        // Gleicher Hash: nichts anfordern
        Assert.Empty(await ComposeCache.SyncAsync(_db, _host, [Hash(Content)], true, Now.AddMinutes(30), CancellationToken.None));
        Assert.Null(ComposeCache.TakeRequest(_host));

        // Datei auf dem Host geändert: neuer Hash → erneut anfordern, alter Stand gilt nicht mehr als aktuell
        var changed = Content.Replace("mariadb", "mariadb:11");
        Assert.Equal(["web"], await ComposeCache.SyncAsync(_db, _host, [Hash(changed)], true, Now.AddMinutes(60), CancellationToken.None));
        await _db.SaveChangesAsync();
        var entry = await _db.ComposeCache.AsNoTracking().SingleAsync();
        Assert.NotEqual(entry.ReportedSha256, entry.Sha256);
    }

    [Fact]
    public async Task NoCacheWithoutPermission()
    {
        await ComposeCache.SyncAsync(_db, _host, [Hash(Content)], true, Now, CancellationToken.None);
        await ComposeCache.StoreAsync(_db, _host, [new ComposeFileResult("web", "/opt/stacks/web/compose.yaml", Content, ComposeCache.Sha256(Content))], _protector, Now, CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _db.ComposeCache.CountAsync());

        // Agent meldet keine Hashes (Policy aus) → Vorrat weg
        await ComposeCache.SyncAsync(_db, _host, null, true, Now, CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _db.ComposeCache.CountAsync());

        // Funktionsschalter aus → Vorrat weg, nichts anfordern
        Assert.Empty(await ComposeCache.SyncAsync(_db, _host, [Hash(Content)], featureOn: false, Now, CancellationToken.None));
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _db.ComposeCache.CountAsync());
        Assert.Null(ComposeCache.TakeRequest(_host));

        // Gemeldete Policy erlaubt Compose nicht mehr → sofort löschen
        await ComposeCache.SyncAsync(_db, _host, [Hash(Content)], true, Now, CancellationToken.None);
        await _db.SaveChangesAsync();
        _host.ReportedPolicy = PolicyReport.Serialize(new AgentPolicyReport("All", AllowContainerManagement: true, AllowComposeEdit: false));
        await ComposeCache.ForgetIfNotAllowedAsync(_db, _host, CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _db.ComposeCache.CountAsync());
    }

    [Fact]
    public async Task RemovedStacksAndHostsLoseTheirCache()
    {
        await ComposeCache.SyncAsync(_db, _host, [Hash(Content, "web"), Hash(Content, "db")], true, Now, CancellationToken.None);
        await _db.SaveChangesAsync();
        await ComposeCache.SyncAsync(_db, _host, [Hash(Content, "web")], true, Now, CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.Equal(["web"], await _db.ComposeCache.Select(c => c.Project).ToListAsync());

        _db.Hosts.Remove(_host);
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _db.ComposeCache.CountAsync()); // mit dem Host gelöscht
    }

    // --- Agent -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agent_SendsNoHashesWithoutBothPermissions()
    {
        var compose = new ComposeInfo("web", "app", "/opt/stacks/web", ["/opt/stacks/web/compose.yaml"], true);
        List<ContainerReport> containers = [new("web-app-1", "nginx", "sha256:x", [], "running", compose)];
        Assert.Null(await DockerManager.ComposeHashesAsync(containers, new AgentPolicy(), CancellationToken.None));
        Assert.Null(await DockerManager.ComposeHashesAsync(containers, new AgentPolicy(AllowContainerManagement: true), CancellationToken.None));
        Assert.Null(await DockerManager.ComposeHashesAsync(containers, new AgentPolicy(AllowComposeEdit: true), CancellationToken.None));
        Assert.Equal(JobOutcome.Rejected, (await DockerManager.CacheAsync(new ComposeCacheRequest(["web"]), new AgentPolicy(AllowContainerManagement: true), CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Agent_OnlyFilesBelowComposeRoot()
    {
        var compose = new ComposeInfo("web", "app", "/opt/stacks/web", ["/opt/stacks/web/compose.yaml"], true);
        Assert.Equal("/opt/stacks/web/compose.yaml", DockerManager.EditablePath(compose, "/opt/stacks", p => p));
        Assert.Null(DockerManager.EditablePath(compose, "/srv", p => p));
        Assert.Null(DockerManager.EditablePath(compose, "/opt/stacks", _ => "/etc/shadow")); // Symlink nach draußen
        Assert.Null(DockerManager.EditablePath(compose with { FilesFound = false }, "/opt/stacks", p => p));
    }

    [Fact]
    public void Agent_RejectsSaveWhenFileChangedSinceOpened()
    {
        Assert.False(DockerManager.ChangedSinceOpened(Content, DockerManager.Sha256(Content)));
        Assert.True(DockerManager.ChangedSinceOpened(Content + "# fremde Änderung\n", DockerManager.Sha256(Content)));
        Assert.True(DockerManager.ChangedSinceOpened(Content, null)); // ohne Stand beim Öffnen nie blind überschreiben
        Assert.Equal(ComposeCache.Sha256(Content), DockerManager.Sha256(Content)); // Server und Agent rechnen gleich
    }
}
