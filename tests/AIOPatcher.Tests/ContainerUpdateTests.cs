using AIOPatcher.Agent;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Tests;

/// <summary>Container aktualisieren: Compose-Herkunft aus Labels, Aufruf, was nicht geht.</summary>
public sealed class ContainerUpdateTests
{
    private const string ComposeLabels =
        """{"com.docker.compose.project":"shortener","com.docker.compose.service":"shlink","com.docker.compose.project.working_dir":"/opt/shortener","com.docker.compose.project.config_files":"/opt/shortener/docker-compose.yml,override.yml","maintainer":"a|b"}""";

    [Fact]
    public void Inventory_ReadsComposeLabels_EvenWithPipesInLabels()
    {
        var output = $$$"""
            [
              {"Name":"/shortener_shlink_1","Image":"sha256:abc","State":{"Status":"running"},"Config":{"Image":"shlinkio/shlink:stable","Labels":{{{ComposeLabels}}}}},
              {"Name":"/jellyfin","Image":"sha256:def","State":{"Status":"running"},"Config":{"Image":"jellyfin/jellyfin:latest","Labels":null}},
              {"Name":"/goskiraorg_database_1","Image":"sha256:91a5024e053f","State":{"Status":"running"},"Config":{"Image":"91a5024e053f"}}
            ]
            """;
        var containers = DockerInventory.ParseInspect(output, path => path.StartsWith("/opt/shortener/", StringComparison.Ordinal));

        var shlink = containers[0].Compose!;
        Assert.Equal(("shortener", "shlink", "/opt/shortener"), (shlink.Project, shlink.Service, shlink.WorkingDir));
        Assert.Equal(["/opt/shortener/docker-compose.yml", "/opt/shortener/override.yml"], shlink.ConfigFiles); // relativ → im Projektordner
        Assert.True(shlink.FilesFound);
        Assert.Null(containers[1].Compose); // docker run
        Assert.Null(containers[2].Compose); // ohne Labels
        Assert.Equal(("91a5024e053f", "running"), (containers[2].Image, containers[2].State)); // nur Image-ID: trotzdem dabei
    }

    [Fact]
    public void Update_Arguments_And_Blockers()
    {
        var compose = new ComposeInfo("shortener", "shlink", "/opt/shortener", ["/opt/shortener/docker-compose.yml"], true);
        Assert.Equal(["-p", "shortener", "--project-directory", "/opt/shortener", "-f", "/opt/shortener/docker-compose.yml"], DockerUpdater.ProjectArguments(compose));
        Assert.Null(DockerUpdater.NotUpdatable(compose));

        Assert.Contains("Docker Compose", DockerUpdater.NotUpdatable(null));
        Assert.Contains("Portainer", DockerUpdater.NotUpdatable(new ComposeInfo("app", "web", "/data/compose/12", ["/data/compose/12/docker-compose.yml"], false)));
        Assert.Contains("not found", DockerUpdater.NotUpdatable(compose with { FilesFound = false, ConfigFiles = ["/srv/gone.yml"] }));

        Assert.True(DockerUpdater.IsValidName("goskiraorg_database_1"));
        Assert.False(DockerUpdater.IsValidName("-rm"));
        Assert.False(DockerUpdater.IsValidName("a b"));
    }

    [Fact]
    public void Policy_LikeProgramUpdates()
    {
        Assert.Null(DockerModule.PolicyRejects(new AgentPolicy()));
        Assert.NotNull(DockerModule.PolicyRejects(new AgentPolicy(AllowAppUpdates: false)));
        Assert.NotNull(DockerModule.PolicyRejects(new AgentPolicy(AllowPatch: PatchPermission.Security)));
        Assert.NotNull(DockerModule.PolicyRejects(new AgentPolicy(AllowPatch: PatchPermission.None))); // lokaler Test-Agent
    }

    [Theory]
    [InlineData(null, null, false, null, "notCompose")]
    [InlineData("app", "web", true, "/srv/app/compose.yml", null)]
    [InlineData("app", "web", false, "/data/compose/3/docker-compose.yml", "portainer")]
    [InlineData("app", "web", false, "/srv/gone.yml", "noFile")]
    public void Server_UpdateBlocker(string? project, string? service, bool found, string? files, string? expected)
    {
        var container = new ContainerInfo { Name = "x", Image = "nginx", ComposeProject = project, ComposeService = service, ComposeFilesFound = found, ComposeFiles = files };
        Assert.Equal(expected, container.UpdateBlocker);
    }

    [Fact]
    public void UpdateMany_GroupsByProject_KeepsStoppedStopped_SkipsWithoutComposeFile()
    {
        var cloud = new ComposeInfo("cloud", "app", "/srv/cloud", ["/srv/cloud/compose.yml"], true);
        var kuma = new ComposeInfo("kuma", "uptime-kuma", "/srv/kuma", ["/srv/kuma/compose.yml"], true);
        var (projects, skipped) = DockerUpdater.Plan(
        [
            ("cloud-app-1", cloud, "running", "sha256:a", "nextcloud"),
            ("uptime-kuma", kuma, "running", "sha256:k", "louislam/uptime-kuma"),
            ("cloud-db-1", cloud with { Service = "db" }, "exited", "sha256:d", "mariadb"),
            ("cloud-app-2", cloud, "running", "sha256:a", "nextcloud"), // skaliert: gleicher Dienst
            ("minecraft", new ComposeInfo("mc", "server", "/srv/mc", ["/srv/mc/compose.yml"], false), "exited", null, "itzg/minecraft-server"),
            ("standalone", null, "running", null, "nginx"),
            ("cloud-app-1", cloud, "running", "sha256:a", "nextcloud"), // doppelt genannt
        ]);

        Assert.Equal(["cloud", "kuma"], projects.Select(p => p.Compose.Project));
        var c = projects[0];
        Assert.Equal(["cloud-app-1", "cloud-db-1", "cloud-app-2"], c.Targets.Select(t => t.Name));
        Assert.Equal(["app"], DockerUpdater.Services(c.Targets.Where(t => t.Running)));
        Assert.Equal(["db"], DockerUpdater.Services(c.Targets.Where(t => !t.Running))); // gestoppt: up --no-start
        Assert.Equal(["minecraft", "standalone"], skipped.Select(s => s.Name));
        Assert.Contains("not found", skipped[0].Reason);

        Assert.True(DockerUpdater.WasRunning("paused"));
        Assert.False(DockerUpdater.WasRunning("created"));
    }

    [Fact]
    public void UpdateMany_Summary()
    {
        Assert.Equal("Containers: 3 updated, 1 already current, 1 skipped (minecraft), 1 failed (db).",
            DockerUpdater.Summarize(3, 1, ["minecraft"], ["db"]));
        Assert.Equal("Containers: 7 skipped (a, b, c, d, e +2).", DockerUpdater.Summarize(0, 0, ["a", "b", "c", "d", "e", "f", "g"], []));
    }

    [Fact]
    public void Server_UpdateAll_SelectsNewImages_PerStack()
    {
        var recipe = new AIOPatcher.Server.Endpoints.HostEndpoints.ContainerRecipeDto("mailcow", "update.sh", "mailcow", null, null, true);
        AIOPatcher.Server.Endpoints.HostEndpoints.ContainerDto C(string name, string? compose, bool update, string? blocker = null,
            AIOPatcher.Server.Endpoints.HostEndpoints.ContainerRecipeDto? r = null) =>
            new(name, "img", "running", update, null, null, false, Compose: compose, UpdateBlocker: blocker, Recipe: r);
        var containers = new[]
        {
            C("jellyfin", "jellyfin/jellyfin", true),
            C("kuma", "kuma/uptime-kuma", true),
            C("db", "cloud/db", false),
            C("minecraft", "mc/server", true, "noFile"),
            C("postfix", "mailcow/postfix", true, r: recipe),
        };

        var (all, skipped) = AIOPatcher.Server.Endpoints.HostEndpoints.UpdateAllSelection(containers, null);
        Assert.Equal(["jellyfin", "kuma"], all);
        Assert.Equal([("minecraft", "noFile"), ("postfix", "recipe")], skipped.Select(s => (s.Name, s.Reason)));

        var (stack, _) = AIOPatcher.Server.Endpoints.HostEndpoints.UpdateAllSelection(containers, "kuma");
        Assert.Equal(["kuma"], stack);
    }
}
