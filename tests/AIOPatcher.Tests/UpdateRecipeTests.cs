using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Update-Rezepte: mitgelieferte Dateien gültig, Erkennung (mailcow), Policy, Vorrang und Job-Zuordnung.</summary>
public sealed class UpdateRecipeTests
{
    private const string MailcowDir = "/opt/mailcow-dockerized";

    private static readonly HashSet<string> MailcowFiles =
        ["/opt/mailcow-dockerized/mailcow.conf", "/opt/mailcow-dockerized/update.sh", "/opt/mailcow-dockerized/generate_config.sh", "/opt/mailcow-dockerized/docker-compose.yml"];

    private static ContainerReport Container(string name, string image, string project, string dir, string state = "running") =>
        new(name, image, "sha256:" + name, [], state, new ComposeInfo(project, name, dir, [$"{dir}/docker-compose.yml"], true));

    /// <summary>Gilt für jede Datei in src/AIOPatcher.Contracts/Recipes: Beiträge der Community fallen hier auf.</summary>
    [Fact]
    public void EveryShippedRecipe_IsValid_AndLoaded()
    {
        var files = UpdateRecipeCatalog.Files().ToList();
        Assert.NotEmpty(files);
        foreach (var (name, json) in files)
        {
            var recipe = UpdateRecipeCatalog.Parse(json);
            Assert.NotNull(recipe);
            Assert.Empty(UpdateRecipeCatalog.Validate(recipe, name).Select(e => $"{name}: {e}"));
        }

        Assert.Equal(files.Count, UpdateRecipeCatalog.All.Count);
        Assert.Equal(UpdateRecipeCatalog.All.Count, UpdateRecipeCatalog.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Mailcow_RunsUpdateShWithoutQuestions()
    {
        var mailcow = UpdateRecipeCatalog.Find("mailcow")!;
        Assert.Equal(["./update.sh", "--force"], mailcow.Command); // --force: keine Rückfragen (offizielle Doku)
        Assert.Equal("mailcow update.sh", mailcow.Name);
        Assert.True(mailcow.IsSuccessExitCode(0));
        Assert.False(mailcow.IsSuccessExitCode(1));
        Assert.NotNull(mailcow.FailureIn("Oh no, something went wrong. Please check the error message above."));
        Assert.Null(mailcow.FailureIn("Starting mailcow..."));
        Assert.Contains("mailcow", mailcow.NoteFor("de"));
        Assert.Equal(mailcow.NoteFor("en"), mailcow.NoteFor("fr")); // unbekannte Sprache → englisch
    }

    [Theory]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["/usr/bin/rm","-rf","/"],"notes":{"en":"n"}}""", "command[0]")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["./../../bin/sh"],"notes":{"en":"n"}}""", "command[0]")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["./bash","-c","x"],"notes":{"en":"n"}}""", "shell")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["/etc/passwd"]},"command":["./u.sh"],"notes":{"en":"n"}}""", "match.files")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":[]},"command":["./u.sh"],"notes":{"en":"n"}}""", "match.files")]
    [InlineData("""{"id":"X Y","name":"X","app":"X","match":{"files":["a"]},"command":["./u.sh"],"notes":{"en":"n"}}""", "id")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["./u.sh"],"timeoutMinutes":0,"notes":{"en":"n"}}""", "timeoutMinutes")]
    [InlineData("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["./u.sh"]}""", "notes.en")]
    [InlineData("""{"id":"x","name":"X","app":"X","docs":"http://x","match":{"files":["a"]},"command":["./u.sh"],"notes":{"en":"n"}}""", "docs")]
    public void Validate_RejectsUnsafeOrIncompleteRecipes(string json, string expected)
    {
        var errors = UpdateRecipeCatalog.Validate(UpdateRecipeCatalog.Parse(json)!);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsUnknownFields() =>
        Assert.Throws<JsonException>(() => UpdateRecipeCatalog.Parse("""{"id":"x","name":"X","app":"X","match":{"files":["a"]},"command":["./u.sh"],"shell":"rm -rf /"}"""));

    [Fact]
    public void Inventory_MarksEveryContainerOfTheMailcowProject()
    {
        var containers = new List<ContainerReport>
        {
            Container("mailcowdockerized-dovecot-mailcow-1", "ghcr.io/mailcow/dovecot:2.3.21.1-5", "mailcowdockerized", MailcowDir),
            Container("mailcowdockerized-mysql-mailcow-1", "mariadb:10.11", "mailcowdockerized", MailcowDir),
            Container("shlink", "shlinkio/shlink:stable", "shortener", "/opt/shortener"),
            new("jellyfin", "jellyfin/jellyfin", "sha256:j", [], "running"),
        };

        var marked = DockerRecipes.WithRecipes(containers, allowed: false, MailcowFiles.Contains);

        Assert.Equal(new RecipeRef("mailcow", false), marked[0].Recipe);
        Assert.Equal(new RecipeRef("mailcow", false), marked[1].Recipe); // auch die Datenbank gehört dazu (kein Einzel-Pull)
        Assert.Null(marked[2].Recipe);
        Assert.Null(marked[3].Recipe);
        Assert.True(DockerRecipes.WithRecipes(containers, allowed: true, MailcowFiles.Contains)[0].Recipe!.Allowed);
    }

    [Fact]
    public void Match_NeedsFilesAndImage()
    {
        string[] images = ["ghcr.io/mailcow/phpfpm:8.2.35-2", "redis:7.4.12-alpine"];
        Assert.Equal("mailcow", DockerRecipes.Match(MailcowDir, images, MailcowFiles.Contains)?.Id);
        Assert.Null(DockerRecipes.Match(MailcowDir, ["redis:7"], MailcowFiles.Contains)); // update.sh allein reicht nicht
        Assert.Null(DockerRecipes.Match(MailcowDir, images, f => f.EndsWith("update.sh", StringComparison.Ordinal))); // mailcow.conf fehlt
        Assert.Null(DockerRecipes.Match(null, images, MailcowFiles.Contains));
        Assert.Null(DockerRecipes.Match("relative/dir", images, _ => true));
    }

    [Fact]
    public void Glob_IsAnchoredAndLiteral()
    {
        Assert.True(UpdateRecipeCatalog.Glob("ghcr.io/mailcow/*", "ghcr.io/mailcow/nginx:1.30.7-1"));
        Assert.False(UpdateRecipeCatalog.Glob("ghcr.io/mailcow/*", "evil.io/ghcr.io/mailcow/x"));
        Assert.False(UpdateRecipeCatalog.Glob("mailcow/*", "ghcrXio/mailcow/x"));
        Assert.False(UpdateRecipeCatalog.Glob("ghcr.io/*", "ghcrXio/x")); // "." ist kein Platzhalter
    }

    [Fact]
    public void Policy_OffByDefault_AndNeedsContainerUpdates()
    {
        Assert.Contains("allowUpdateRecipes", DockerRecipes.PolicyRejects(new AgentPolicy()));
        Assert.Null(DockerRecipes.PolicyRejects(new AgentPolicy(AllowUpdateRecipes: true)));
        Assert.Contains("allowAppUpdates", DockerRecipes.PolicyRejects(new AgentPolicy(AllowUpdateRecipes: true, AllowAppUpdates: false)));
        Assert.NotNull(DockerRecipes.PolicyRejects(new AgentPolicy(AllowPatch: PatchPermission.None, AllowUpdateRecipes: true))); // lokaler Test-Agent

        var dir = Path.Combine(Path.GetTempPath(), "aiop-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(AgentPolicy.Set(dir, "allowUpdateRecipes", "true").AllowUpdateRecipes);
            Assert.True(AgentPolicy.Load(dir).AllowUpdateRecipes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Agent_RejectsRecipeWithoutLocalPermission()
    {
        var log = new List<string>();
        var payload = PullJsonContext.ToElement(new RecipeUpdateRequest("mailcow", "mailcowdockerized-nginx-mailcow-1"), PullJsonContext.Default.RecipeUpdateRequest);
        var outcome = await new DockerModule().ExecuteAsync(new AgentTask(Guid.NewGuid(), AgentModules.Docker, ModuleActions.Recipe, payload),
            new TaskContext(new AgentPolicy(), log.Add), default);
        Assert.Equal(JobOutcome.Rejected, outcome.Outcome);
        Assert.Contains("allowUpdateRecipes", outcome.Summary);
        Assert.Contains(ModuleActions.Recipe, new DockerModule().Actions);
    }

    [Fact]
    public async Task Agent_RejectsUnknownRecipe()
    {
        var result = await DockerRecipes.RunAsync(new RecipeUpdateRequest("does-not-exist", "web"), _ => { }, default);
        Assert.Equal(JobOutcome.Rejected, result.Outcome);
        Assert.Contains("does-not-exist", result.Summary);
        Assert.Equal(JobOutcome.Rejected, (await DockerRecipes.RunAsync(new RecipeUpdateRequest("mailcow", "-rm"), _ => { }, default)).Outcome);
    }

    [Fact]
    public void Verify_AllPreviouslyRunningContainersMustRunAgain()
    {
        string[] before = ["a", "b", "c"];
        var ok = DockerRecipes.CheckProject(before, [
            new ContainerReport("a", "i", null, [], "running"),
            new ContainerReport("b", "i", null, [], "running", Health: "healthy"),
            new ContainerReport("c", "i", null, [], "running", Health: "starting"),
        ]);
        Assert.Empty(ok.Problems);
        Assert.Equal(["c"], ok.Starting);

        var bad = DockerRecipes.CheckProject(before, [
            new ContainerReport("a", "i", null, [], "exited"),
            new ContainerReport("b", "i", null, [], "running", Health: "unhealthy"),
        ]);
        Assert.Equal([("a", "exited"), ("b", "unhealthy"), ("c", "missing")], bad.Problems);
    }

    [Fact]
    public void Script_MustExist_AndNotBeWritableByOthers()
    {
        Assert.Contains("not found", DockerRecipes.ScriptProblem(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "update.sh")));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
            Assert.Contains("writable", DockerRecipes.ScriptProblem(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Null(DockerRecipes.ScriptProblem(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Server_StoresRecipe_AndMapsRecipeJobsToDockerRecipe()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        var host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "mail", Hostname = "mail", CertificateThumbprint = "x",
            CertificateExpiresAt = DateTime.UtcNow.AddYears(1), OsFamily = OsFamily.Linux,
        };
        db.Hosts.Add(host);
        await db.SaveChangesAsync();

        var report = Container("mailcowdockerized-nginx-mailcow-1", "ghcr.io/mailcow/nginx:1.30.7-1", "mailcowdockerized", MailcowDir) with
        {
            Recipe = new RecipeRef("mailcow", true),
        };
        await ContainerUpdateService.ReplaceContainersAsync(db, host, [report], DateTime.UtcNow, default);
        await db.SaveChangesAsync();
        var stored = await db.Containers.SingleAsync();
        Assert.Equal(("mailcow", true), (stored.UpdateRecipe, stored.UpdateRecipeAllowed));

        var job = new PatchJob
        {
            HostId = host.Id, Type = JobType.ContainerUpdate, RequestedBy = "admin:test", Packages = stored.Name,
            Payload = JsonSerializer.Serialize(new RecipeUpdateRequest("mailcow", stored.Name), PullJsonContext.Default.RecipeUpdateRequest),
        };
        Assert.Equal(new RecipeUpdateRequest("mailcow", stored.Name), HostEndpoints.RecipeOf(job));
        Assert.Null(HostEndpoints.RecipeOf(new PatchJob { HostId = host.Id, Type = JobType.ContainerUpdate, RequestedBy = "x", Packages = "web" })); // generisch
        Assert.Null(HostEndpoints.RecipeOf(new PatchJob { HostId = host.Id, Type = JobType.SystemChange, RequestedBy = "x", Payload = job.Payload }));
    }
}
