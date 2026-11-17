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
        var output = $"/shortener_shlink_1|shlinkio/shlink:stable|sha256:abc|running|{ComposeLabels}\n" +
                     "/jellyfin|jellyfin/jellyfin:latest|sha256:def|running|null\n" +
                     "/old|nginx:1.27|sha256:123|running\n";
        var containers = DockerInventory.ParseContainers(output, path => path.StartsWith("/opt/shortener/", StringComparison.Ordinal));

        var shlink = containers[0].Compose!;
        Assert.Equal(("shortener", "shlink", "/opt/shortener"), (shlink.Project, shlink.Service, shlink.WorkingDir));
        Assert.Equal(["/opt/shortener/docker-compose.yml", "/opt/shortener/override.yml"], shlink.ConfigFiles); // relativ → im Projektordner
        Assert.True(shlink.FilesFound);
        Assert.Null(containers[1].Compose); // docker run
        Assert.Null(containers[2].Compose); // altes Format ohne Labels
        Assert.Equal("running", containers[2].State);
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
}
