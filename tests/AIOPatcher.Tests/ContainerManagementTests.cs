using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services.Modules;

namespace AIOPatcher.Tests;

/// <summary>Mini-Docker-Manager: Policy-Sperren, Prüfung der Aufträge, Befehle als Argumentlisten, Pfade unter dem Basisordner.</summary>
public sealed class ContainerManagementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    public ContainerManagementTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    // --- Policy: ab Werk aus, nur lokal per "policy set" ------------------------------------------------

    [Fact]
    public void Policy_OffByDefault_OnlyLocalSet()
    {
        var policy = AgentPolicy.Load(_dir);
        Assert.False(policy.AllowContainerManagement);
        Assert.False(policy.AllowComposeEdit);
        Assert.Equal("/opt/stacks", policy.EffectiveComposeRoot);

        policy = AgentPolicy.Set(_dir, "allowContainerManagement", "true");
        Assert.True(policy.AllowContainerManagement);
        Assert.False(policy.AllowComposeEdit);
        policy = AgentPolicy.Set(_dir, "allowComposeEdit", "true");
        policy = AgentPolicy.Set(_dir, "composeRoot", "/srv/stacks/../docker//");
        Assert.Equal("/srv/docker", AgentPolicy.Load(_dir).ComposeRoot);
        Assert.True(AgentPolicy.Load(_dir).AllowComposeEdit);

        Assert.Throws<ArgumentException>(() => AgentPolicy.Set(_dir, "composeRoot", "/"));
        Assert.Throws<ArgumentException>(() => AgentPolicy.Set(_dir, "composeRoot", "/.."));
        Assert.Throws<ArgumentException>(() => AgentPolicy.Set(_dir, "composeRoot", "stacks"));
        Assert.Throws<ArgumentException>(() => AgentPolicy.Set(_dir, "allowContainerManagement", "yes"));

        // gemeldet wird camelCase (darauf prüft "Erste Schritte")
        var json = JsonSerializer.Serialize(policy.ToReport(), PullJsonContext.Default.AgentPolicyReport);
        Assert.Contains("\"allowContainerManagement\":true", json);
        Assert.Contains("\"composeRoot\":\"/srv/docker\"", json);
    }

    [Fact]
    public void Agent_RejectsWithoutLocalPolicy()
    {
        var off = new AgentPolicy();
        Assert.Contains("allowContainerManagement", DockerManager.PolicyRejects(off, ContainerOps.Start));
        Assert.Contains("allowContainerManagement", DockerManager.PolicyRejects(off, ContainerOps.Prune));

        var manage = off with { AllowContainerManagement = true };
        Assert.Null(DockerManager.PolicyRejects(manage, ContainerOps.Stop));
        Assert.Null(DockerManager.PolicyRejects(manage, ContainerOps.Down));
        Assert.Contains("allowComposeEdit", DockerManager.PolicyRejects(manage, ContainerOps.ReadCompose));
        Assert.Contains("allowComposeEdit", DockerManager.PolicyRejects(manage, ContainerOps.WriteCompose));
        Assert.Contains("allowComposeEdit", DockerManager.PolicyRejects(manage, ContainerOps.Create));

        Assert.Null(DockerManager.PolicyRejects(manage with { AllowComposeEdit = true }, ContainerOps.Create));
        // Compose allein reicht nicht
        Assert.NotNull(DockerManager.PolicyRejects(off with { AllowComposeEdit = true }, ContainerOps.ReadCompose));
    }

    [Fact]
    public async Task Agent_PolicyRejectionStopsBeforeAnyCommand()
    {
        var outcome = await DockerManager.RunAsync(new ContainerManageRequest(ContainerOps.Stop, ["web"]), new AgentPolicy(), _ => { }, CancellationToken.None);
        Assert.Equal(JobOutcome.Rejected, outcome.Outcome);
    }

    // --- Aufträge prüfen (Server und Agent) -------------------------------------------------------------

    [Theory]
    [InlineData("start", "web", null, null)]
    [InlineData("stop", "-rm", null, "Invalid container name")]
    [InlineData("stop", "web;reboot", null, "Invalid container name")]
    [InlineData("restart", "$(id)", null, "Invalid container name")]
    [InlineData("down", null, "web", null)]
    [InlineData("down", null, "../etc", "Invalid stack name")]
    [InlineData("down", null, "Web", "Invalid stack name")]
    [InlineData("read", null, "a b", "Invalid stack name")]
    [InlineData("shell", "web", null, "Unknown operation")]
    [InlineData("prune", null, null, null)]
    public void Requests_AreValidated(string op, string? container, string? project, string? error)
    {
        var request = new ContainerManageRequest(op, container is null ? null : [container], project);
        if (error is null)
        {
            Assert.Null(ContainerOps.Invalid(request));
        }
        else
        {
            Assert.Contains(error, ContainerOps.Invalid(request));
        }
    }

    [Fact]
    public void Requests_ComposeContentRules()
    {
        Assert.Contains("empty", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Create, Project: "web", Content: " ")));
        Assert.Contains("larger", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.WriteCompose, Project: "web", Content: new string('x', ContainerOps.MaxComposeBytes + 1))));
        Assert.Null(ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Create, Project: "web", Content: "services: {}")));
        Assert.Contains("1 to", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Stop, [])));
        Assert.Contains("1 to", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Stop, Enumerable.Range(0, 101).Select(i => $"c{i}").ToList())));
    }

    // --- Befehle: immer Argumentlisten, nie eine Shell ---------------------------------------------------

    [Fact]
    public void Commands_AreArgumentLists()
    {
        Assert.Equal(["stop", "web", "db"], DockerManager.LifecycleArguments("stop", ["web", "db"]));
        Assert.Equal(["rm", "web"], DockerManager.RemoveArguments(["web"], false));
        Assert.Equal(["rm", "-v", "web"], DockerManager.RemoveArguments(["web"], true));

        var compose = new ComposeInfo("web", "app", "/opt/stacks/web", ["/opt/stacks/web/compose.yaml"], true);
        Assert.Equal(["-p", "web", "--project-directory", "/opt/stacks/web", "-f", "/opt/stacks/web/compose.yaml", "down"], DockerManager.DownArguments(compose, false));
        Assert.Equal("--volumes", DockerManager.DownArguments(compose, true)[^1]);

        Assert.Equal(2, DockerManager.PruneCommands(false).Count);
        Assert.DoesNotContain(DockerManager.PruneCommands(false), c => c[0] == "volume"); // Volumes nie ohne Haken
        Assert.Equal(["volume", "prune", "--force"], DockerManager.PruneCommands(true)[^1]);

        Assert.Equal(["-p", "web", "--project-directory", "/opt/stacks/web", "-f", "/tmp/x.yaml", "config", "--quiet"],
            DockerManager.ValidateArguments("web", "/opt/stacks/web", ["/tmp/x.yaml"]));
    }

    [Theory]
    [InlineData("/opt/stacks", "/opt/stacks/web/compose.yaml", true)]
    [InlineData("/opt/stacks/", "/opt/stacks/web/compose.yaml", true)]
    [InlineData("/opt/stacks", "/opt/stacks/../../etc/passwd", false)]
    [InlineData("/opt/stacks", "/opt/stacks/web/../../etc/shadow", false)]
    [InlineData("/opt/stacks", "/opt/stacksx/web/compose.yaml", false)]
    [InlineData("/opt/stacks", "/opt/stacks", false)]
    [InlineData("/opt/stacks", "opt/stacks/web/compose.yaml", false)]
    [InlineData("/home/docker", "/home/docker/water/compose.yaml", true)]
    public void Paths_StayBelowComposeRoot(string root, string path, bool inside) => Assert.Equal(inside, DockerManager.IsInside(root, path));

    [Fact]
    public void Paths_Normalize() => Assert.Equal("/opt/web", DockerManager.Normalize("/opt//stacks/./../web/"));

    // --- Platz freigeben: Größen aus "docker system df" -------------------------------------------------

    [Theory]
    [InlineData("0B", 0)]
    [InlineData("512kB", 512_000)]
    [InlineData("1.5GB", 1_500_000_000)]
    [InlineData("3.4MiB", 3_565_158)]
    [InlineData("junk", 0)]
    public void Sizes_Parse(string value, long bytes) => Assert.Equal(bytes, DockerManager.ParseSize(value));

    [Fact]
    public void DiskUsage_Parse()
    {
        var disk = DockerManager.ParseDiskUsage(
            "{\"Active\":\"5\",\"Reclaimable\":\"1.1GB (34%)\",\"Size\":\"3.2GB\",\"TotalCount\":\"12\",\"Type\":\"Images\"}\n" +
            "{\"Active\":\"0\",\"Reclaimable\":\"250MB\",\"Size\":\"250MB\",\"TotalCount\":\"40\",\"Type\":\"Build Cache\"}\nkaputt\n");
        Assert.Equal([new DockerDiskUsage("Images", 3_200_000_000, 1_100_000_000, 12), new DockerDiskUsage("Build Cache", 250_000_000, 250_000_000, 40)], disk);
        Assert.Equal("1.1 GB", DockerManager.FormatSize(1_100_000_000));
    }

    // --- Platz freigeben mit Auswahl: Scan und Löschplan ---------------------------------------------------

    private const string UsedId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string OldId = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string NoneId = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void Scan_ListsOnlyImagesNoContainerUses()
    {
        var output =
            $$"""{"ID":"{{UsedId}}","Repository":"nginx","Tag":"latest","Size":"187MB","CreatedAt":"2024-01-02 10:00:00 +0100 CET"}""" + "\n" +
            $$"""{"ID":"{{OldId}}","Repository":"postgres","Tag":"15","Size":"1.2GB","CreatedAt":"2023-05-01 08:00:00 +0000 UTC"}""" + "\n" +
            $$"""{"ID":"{{OldId}}","Repository":"registry.local/postgres","Tag":"15","Size":"1.2GB","CreatedAt":"2023-05-01 08:00:00 +0000 UTC"}""" + "\n" +
            $$"""{"ID":"{{NoneId}}","Repository":"<none>","Tag":"<none>","Size":"20MB","CreatedAt":"2024-02-01 08:00:00 +0000 UTC"}""" + "\nkaputt\n";
        var images = DockerManager.ParseUnusedImages(output, new HashSet<string> { UsedId });

        Assert.Equal([OldId, NoneId], images.Select(i => i.Id)); // genutztes nginx fehlt, größtes zuerst
        Assert.Equal(["postgres:15", "registry.local/postgres:15"], images[0].References);
        Assert.Equal(1_200_000_000, images[0].Size);
        Assert.Empty(images[1].References); // ohne Namen: über die ID
    }

    [Fact]
    public void Scan_VolumesWithComposeProjectAndSizes()
    {
        var sizes = DockerManager.ParseVolumeSizes("""{"Images":[],"Volumes":[{"Name":"web_db","Links":"0","Size":"785.7MB"}],"BuildCache":[]}""");
        Assert.Equal(785_700_000, sizes!["web_db"]);
        Assert.Null(DockerManager.ParseVolumeSizes("Error: unknown flag"));

        var volumes = DockerManager.ParseUnusedVolumes(
            """{"Driver":"local","Labels":"com.docker.compose.project=web,com.docker.compose.volume=db","Name":"web_db"}""" + "\n" +
            """{"Driver":"local","Labels":"","Name":"0123abcd"}""" + "\n" +
            """{"Driver":"local","Labels":"","Name":"-bad"}""" + "\n", sizes);
        Assert.Equal([new UnusedVolume("web_db", 785_700_000, "web", "local"), new UnusedVolume("0123abcd", null, null, "local")], volumes);
    }

    [Fact]
    public void Clean_DeletesOnlyWhatTheFreshScanStillLists()
    {
        var scan = new PruneScan(
            [new UnusedImage(OldId, ["postgres:15", "registry.local/postgres:15"], 1_200_000_000, null), new UnusedImage(NoneId, [], 20_000_000, null)],
            [new UnusedVolume("web_db", 1, "web", "local")], 0, 0);
        // UsedId wird inzwischen genutzt (nicht im Scan), old_data gibt es nicht mehr
        var request = new ContainerManageRequest(ContainerOps.Clean, Images: [OldId, NoneId, UsedId], VolumeNames: ["web_db", "old_data"]);

        var (refs, images, volumes, skipped) = DockerManager.PlanClean(scan, request);
        Assert.Equal(["postgres:15", "registry.local/postgres:15", NoneId], refs); // Namen entfernen, ohne Namen die ID
        Assert.Equal(2, images);
        Assert.Equal(["web_db"], volumes);
        Assert.Equal([UsedId, "old_data"], skipped);

        // nichts ausgewählt = nichts gelöscht
        var none = DockerManager.PlanClean(scan, new ContainerManageRequest(ContainerOps.Clean, BuildCache: true));
        Assert.Empty(none.ImageRefs);
        Assert.Empty(none.Volumes);
    }

    [Fact]
    public void Clean_RequestValidation()
    {
        Assert.Contains("Nothing", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean)));
        Assert.Null(ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean, BuildCache: true)));
        Assert.Null(ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean, Images: [OldId], VolumeNames: ["web_db"])));
        Assert.Contains("image ID", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean, Images: ["nginx;rm -rf /"])));
        Assert.Contains("image ID", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean, Images: ["--force"])));
        Assert.Contains("volume name", ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Clean, VolumeNames: ["-f"])));
        Assert.Null(ContainerOps.Invalid(new ContainerManageRequest(ContainerOps.Scan)));
    }

    // --- Server -------------------------------------------------------------------------------------------

    private static ManagedHost Host(AgentPolicyReport? policy, bool manageAction = true) => new()
    {
        Name = "docker", Hostname = "docker", CertificateThumbprint = "x", OsFamily = OsFamily.Linux,
        ReportedModules = JsonSerializer.Serialize(new List<ModuleInfo>
        {
            new(AgentModules.Docker, true, false, manageAction ? [ModuleActions.Inventory, ModuleActions.Manage] : [ModuleActions.Inventory]),
        }, ModuleJsonContext.Default.ListModuleInfo),
        ReportedPolicy = policy is null ? null : PolicyReport.Serialize(policy),
    };

    [Fact]
    public void Server_BlocksUntilEverythingAllows()
    {
        var stop = new ContainerManageRequest(ContainerOps.Stop, ["web"]);
        var edit = new ContainerManageRequest(ContainerOps.WriteCompose, Project: "web", Content: "services: {}");
        var allowed = new AgentPolicyReport("All", AllowContainerManagement: true, AllowComposeEdit: false);

        Assert.NotNull(ContainerManagementEndpoints.Blocked(Host(allowed), stop, featureOn: false));
        Assert.NotNull(ContainerManagementEndpoints.Blocked(Host(allowed, manageAction: false), stop, true));
        Assert.Contains("allowContainerManagement", ContainerManagementEndpoints.Blocked(Host(allowed with { AllowContainerManagement = false }), stop, true));
        Assert.Null(ContainerManagementEndpoints.Blocked(Host(allowed), stop, true));
        Assert.Contains("allowComposeEdit", ContainerManagementEndpoints.Blocked(Host(allowed), edit, true));
        Assert.Null(ContainerManagementEndpoints.Blocked(Host(allowed with { AllowComposeEdit = true }), edit, true));

        var management = ContainerManagementEndpoints.Management(Host(allowed));
        Assert.True(management.AgentSupports);
        Assert.Equal((true, false), (management.Allowed, management.ComposeEdit));
    }

    [Fact]
    public void Server_RemovesComposeContentOnHandover()
    {
        var job = new PatchJob
        {
            RequestedBy = "admin:x", Type = JobType.ContainerManage,
            Payload = JsonSerializer.Serialize(new ContainerManageRequest(ContainerOps.WriteCompose, Project: "web", Content: "password: secret", ExpectedSha256: "abc"),
                PullJsonContext.Default.ContainerManageRequest),
        };

        var request = ContainerManagementEndpoints.TakeRequest(job);
        Assert.Equal("password: secret", request!.Content); // an den Agent
        Assert.DoesNotContain("secret", job.Payload); // im Job nicht mehr
        Assert.Contains("abc", job.Payload);
        Assert.Null(ContainerManagementEndpoints.TakeRequest(new PatchJob { RequestedBy = "x", Type = JobType.ContainerUpdate, Payload = "{}" }));
    }
}
