using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Portainer: Stacks und Container lesen, Update-Anfragen (Datei- und Git-Stacks), Job-Ablauf, Update-Badge.</summary>
public sealed class PortainerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly IntegrationHttp _http = new(new EphemeralDataProtectionProvider());
    private readonly FakePortainer _portainer = new();

    public PortainerTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Integration NewIntegration() => new()
    {
        Name = "portainer01",
        Type = IntegrationType.Portainer,
        BaseUrl = "https://portainer.example:9443",
        ProtectedSecret = _http.Protect("ptr_secret"),
    };

    private PortainerClient Client(Integration integration) => new PortainerClientFactory(_http, () => _portainer).For(integration);

    private const string Stacks = """
        [{"Id":3,"Name":"shortener","Type":2,"EndpointId":1,"Status":1,"UpdateDate":1795000000,"Env":[{"name":"TAG","value":"stable"}]},
         {"Id":7,"Name":"wiki","Type":2,"EndpointId":1,"Status":1,"GitConfig":{"URL":"https://git.example/wiki.git","ReferenceName":"refs/heads/main",
           "ConfigFilePath":"docker-compose.yml","Authentication":{"Username":"deploy","Password":""}},"Option":{"Prune":true},
           "Env":[{"name":"DOMAIN","value":"wiki.example"}]},
         {"Id":9,"Name":"k8s-app","Type":3,"EndpointId":2,"Status":1}]
        """;

    private void SetupRead()
    {
        _portainer.On("GET", "/api/system/status", """{"Version":"2.21.4","InstanceID":"x"}""");
        _portainer.On("GET", "/api/endpoints", """[{"Id":1,"Name":"docker01","Type":2,"Status":1,"URL":"tcp://10.0.0.5:9001"},{"Id":2,"Name":"k8s","Type":6,"Status":1}]""");
        _portainer.On("GET", "/api/stacks", Stacks);
        _portainer.On("GET", "/api/endpoints/1/docker/containers/json", """
            [{"Id":"c1","Names":["/shortener-shlink-1"],"Image":"shlinkio/shlink:stable","ImageID":"sha256:img1","State":"running",
              "Labels":{"com.docker.compose.project":"shortener","com.docker.compose.service":"shlink"}},
             {"Id":"c2","Names":["/wiki-app-1"],"Image":"sha256:img2","ImageID":"sha256:img2","State":"exited",
              "Labels":{"com.docker.compose.project":"wiki","com.docker.compose.service":"app"}},
             {"Id":"c3","Names":["/wiki-build-1"],"Image":"wiki-build","ImageID":"sha256:img3","State":"running",
              "Labels":{"com.docker.compose.project":"wiki","com.docker.compose.service":"build"}},
             {"Id":"c4","Names":["/standalone"],"Image":"nginx","ImageID":"sha256:img4","State":"running","Labels":{}}]
            """);
        _portainer.On("GET", "/api/endpoints/1/docker/images/json", $$"""
            [{"Id":"sha256:img1","RepoTags":["shlinkio/shlink:stable"],"RepoDigests":["shlinkio/shlink@{{Digest}}"]},
             {"Id":"sha256:img2","RepoTags":["<none>:<none>","ghcr.io/requarks/wiki:2"],"RepoDigests":["ghcr.io/requarks/wiki@{{Digest}}"]},
             {"Id":"sha256:img3","RepoTags":["wiki-build:latest"],"RepoDigests":[]},
             {"Id":"sha256:img4","RepoTags":["nginx:latest"],"RepoDigests":["nginx@{{Digest}}"]}]
            """);
    }

    [Fact]
    public async Task Status_ReadsStacksAndAssignsContainers()
    {
        SetupRead();
        using var client = Client(NewIntegration());
        var status = await client.GetStatusAsync(Now, CancellationToken.None);

        Assert.Equal("2.21.4", status.Version);
        Assert.Equal(["docker01", "k8s"], status.Environments.Select(e => e.Name));
        Assert.Equal(["k8s-app", "shortener", "wiki"], status.Stacks.Select(s => s.Name));
        Assert.All(_portainer.Requests, r => Assert.Equal("ptr_secret", r.ApiKey));
        Assert.DoesNotContain(_portainer.Requests, r => r.Path.StartsWith("/api/endpoints/2/", StringComparison.Ordinal)); // Kubernetes: kein Docker-Proxy

        var shortener = status.Stacks.Single(s => s.Name == "shortener");
        Assert.False(shortener.Git);
        var shlink = Assert.Single(shortener.Containers);
        Assert.Equal(("shlink", "docker.io/shlinkio/shlink:stable", Digest), (shlink.Service, shlink.Reference, shlink.LocalDigest));

        var wiki = status.Stacks.Single(s => s.Name == "wiki");
        Assert.True(wiki.Git);
        Assert.Equal("refs/heads/main", wiki.GitRef);
        Assert.Equal(2, wiki.Containers.Count);
        var app = wiki.Containers.Single(c => c.Service == "app");
        Assert.Equal("ghcr.io/requarks/wiki:2", app.Image); // Image-ID → erster echter Tag
        Assert.Equal("ghcr.io/requarks/wiki:2", app.Reference);
        Assert.Null(wiki.Containers.Single(c => c.Service == "build").Reference); // lokal gebaut: nicht prüfbar
    }

    [Fact]
    public async Task Status_OldPortainer_FallsBackToApiStatus_AndKeepsStacksWhenDockerFails()
    {
        SetupRead();
        _portainer.Remove("GET", "/api/system/status");
        _portainer.On("GET", "/api/status", """{"Version":"2.19.5"}""");
        _portainer.On("GET", "/api/endpoints/1/docker/containers/json", """{"message":"Unable to reach environment","details":"dial tcp: timeout"}""", HttpStatusCode.BadGateway);

        using var client = Client(NewIntegration());
        var status = await client.GetStatusAsync(Now, CancellationToken.None);

        Assert.Equal("2.19.5", status.Version);
        Assert.Contains("dial tcp: timeout", status.Environments.Single(e => e.Id == 1).Error);
        Assert.Empty(status.Stacks.Single(s => s.Name == "shortener").Containers);
    }

    [Fact]
    public async Task Errors_AreReadable()
    {
        _portainer.On("GET", "/api/system/status", "", HttpStatusCode.Unauthorized);
        using var client = Client(NewIntegration());
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => client.TestAsync(CancellationToken.None));
        Assert.Contains("Access denied (401)", ex.Message);
    }

    [Fact]
    public async Task Redeploy_FileStack_SendsFileEnvAndPull()
    {
        SetupRead();
        _portainer.On("GET", "/api/stacks/3", JsonNode.Parse(Stacks)![0]!.ToJsonString());
        _portainer.On("GET", "/api/stacks/3/file", """{"StackFileContent":"services:\n  shlink:\n    image: shlinkio/shlink:${TAG}\n"}""");
        _portainer.On("PUT", "/api/stacks/3", """{"Id":3,"Name":"shortener","Status":1}""");

        var log = new List<string>();
        using var client = Client(NewIntegration());
        await client.RedeployAsync(3, 1, log.Add, CancellationToken.None);

        var put = _portainer.Requests.Single(r => r.Method == "PUT");
        Assert.Equal("?endpointId=1", put.Query);
        var body = JsonNode.Parse(put.Body!)!;
        Assert.StartsWith("services:", (string?)body["StackFileContent"]);
        Assert.Equal("TAG", (string?)body["Env"]![0]!["name"]);
        Assert.Equal("stable", (string?)body["Env"]![0]!["value"]);
        Assert.True((bool)body["PullImage"]!);
        Assert.True((bool)body["RepullImageAndRedeploy"]!);
        Assert.False((bool)body["Prune"]!);
        Assert.Contains(log, l => l.Contains("redeployed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redeploy_GitStack_UsesGitRedeploy_AndWaitsWhileDeploying()
    {
        _portainer.On("GET", "/api/stacks/7", JsonNode.Parse(Stacks)![1]!.ToJsonString());
        _portainer.On("PUT", "/api/stacks/7/git/redeploy", """{"Id":7,"Name":"wiki","Status":3}""");
        // erste Abfrage nach dem PUT: fertig
        _portainer.OnSequence("GET", "/api/stacks/7", JsonNode.Parse(Stacks)![1]!.ToJsonString(), """{"Id":7,"Name":"wiki","Status":1}""");

        using var client = Client(NewIntegration());
        await client.RedeployAsync(7, 1, _ => { }, CancellationToken.None);

        var put = _portainer.Requests.Single(r => r.Method == "PUT");
        Assert.Equal("/api/stacks/7/git/redeploy", put.Path);
        Assert.Equal("?endpointId=1", put.Query);
        Assert.DoesNotContain(_portainer.Requests, r => r.Path == "/api/stacks/7/file");
        var body = JsonNode.Parse(put.Body!)!;
        Assert.Equal("refs/heads/main", (string?)body["RepositoryReferenceName"]);
        Assert.True((bool)body["RepositoryAuthentication"]!);
        Assert.Equal("deploy", (string?)body["RepositoryUsername"]);
        Assert.Equal("", (string?)body["RepositoryPassword"]); // Portainer nimmt das gespeicherte
        Assert.True((bool)body["Prune"]!); // gespeicherte Einstellung bleibt
        Assert.True((bool)body["PullImage"]!);
        Assert.Equal("wiki.example", (string?)body["Env"]![0]!["value"]);
    }

    [Fact]
    public async Task Redeploy_ErrorStatus_And_Kubernetes_Fail()
    {
        _portainer.On("GET", "/api/stacks/3", """{"Id":3,"Name":"shortener","Type":2,"EndpointId":1,"Status":1}""");
        _portainer.On("GET", "/api/stacks/3/file", """{"StackFileContent":"services: {}"}""");
        _portainer.On("PUT", "/api/stacks/3", """{"Id":3,"Name":"shortener","Status":4,"DeploymentStatus":[{"Status":4,"Message":"pull access denied"}]}""");
        _portainer.On("GET", "/api/stacks/9", """{"Id":9,"Name":"k8s-app","Type":3,"EndpointId":2,"Status":1}""");

        using var client = Client(NewIntegration());
        var failed = await Assert.ThrowsAsync<IntegrationException>(() => client.RedeployAsync(3, 1, _ => { }, CancellationToken.None));
        Assert.Contains("pull access denied", failed.Message);

        var k8s = await Assert.ThrowsAsync<IntegrationException>(() => client.RedeployAsync(9, 2, _ => { }, CancellationToken.None));
        Assert.Contains("Kubernetes", k8s.Message);
        Assert.Single(_portainer.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void Stacks_UpdateStateFromRegistryDigest()
    {
        var status = new PortainerStatus("2.21", [new PortainerEnvironment(1, "docker01", 2, 1, null), new PortainerEnvironment(2, "old", 1, 2, null)],
        [
            new PortainerStack(1, "app", 2, 1, 1, false, null, null, null, [new PortainerContainer("app-web-1", "web", "nginx:1.27", "docker.io/library/nginx:1.27", Digest, "running")]),
            new PortainerStack(2, "db", 2, 1, 1, false, null, null, null,
                [new PortainerContainer("db-1", "db", "postgres:16", "docker.io/library/postgres:16", Digest, "running"),
                 new PortainerContainer("db-tool", "tool", "tool", null, null, "running")]),
            new PortainerStack(3, "offline", 1, 2, 1, false, null, null, null, []),
        ], Now);
        var checks = new Dictionary<string, ImageCheck>
        {
            ["docker.io/library/nginx:1.27"] = new() { Reference = "docker.io/library/nginx:1.27", RemoteDigest = NewDigest },
            ["docker.io/library/postgres:16"] = new() { Reference = "docker.io/library/postgres:16", RemoteDigest = Digest },
        };
        var jobs = new List<StackUpdateJob>
        {
            new() { StackId = 1, StackName = "app", RequestedBy = "alice", Status = JobStatus.Failed, CreatedAt = Now.AddHours(-2) },
            new() { StackId = 1, StackName = "app", RequestedBy = "bob", Status = JobStatus.Succeeded, CreatedAt = Now.AddHours(-1) },
        };

        var rows = PortainerEndpoints.BuildStacks(status, checks, jobs);

        Assert.Equal(["app", "db", "offline"], rows.Select(r => r.Name)); // veraltete zuerst
        Assert.Equal(("outdated", 1), (rows[0].UpdateState, rows[0].OutdatedImages));
        Assert.Equal("bob", rows[0].LastJob!.RequestedBy);
        Assert.Equal("current", rows[1].UpdateState);
        Assert.Equal(["current", "local"], rows[1].Containers.Select(c => c.ImageState));
        Assert.Equal(("unknown", "offline", "swarm"), (rows[2].UpdateState, rows[2].Blocker, rows[2].Type));
    }

    [Fact]
    public async Task Job_RunsRedeploy_LogsAndRefreshes()
    {
        SetupRead();
        _portainer.On("GET", "/api/stacks/3", JsonNode.Parse(Stacks)![0]!.ToJsonString());
        _portainer.On("GET", "/api/stacks/3/file", """{"StackFileContent":"services: {}"}""");
        _portainer.On("PUT", "/api/stacks/3", """{"Id":3,"Name":"shortener","Status":1}""");

        var integration = NewIntegration();
        _db.Integrations.Add(integration);
        var job = new StackUpdateJob { IntegrationId = integration.Id, StackId = 3, EnvironmentId = 1, StackName = "shortener", RequestedBy = "alice" };
        _db.StackUpdateJobs.Add(job);
        await _db.SaveChangesAsync();

        var factory = new PortainerClientFactory(_http, () => _portainer);
        var notifier = new Notifier();
        var monitor = new PortainerMonitor(null!, factory, notifier, new FeatureFlags(new AIOPatcherOptions()), NullLogger<PortainerMonitor>.Instance);
        var updates = new PortainerStackUpdates(null!, factory, monitor, notifier, NullLogger<PortainerStackUpdates>.Instance);
        await updates.RunAsync(_db, job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
        Assert.Contains("Start: stack shortener", job.Log);
        Assert.Contains("redeployed", job.Summary);
        Assert.Equal(3, PortainerMonitor.Parse(integration.StatusJson)!.Stacks.Count); // danach neu eingelesen

        // Zweiter Lauf desselben Jobs: nichts mehr tun
        await updates.RunAsync(_db, job.Id, CancellationToken.None);
        Assert.Single(_portainer.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task Job_Failure_IsRecorded()
    {
        _portainer.On("GET", "/api/stacks/3", """{"Id":3,"Name":"shortener","Type":2,"EndpointId":1,"Status":1}""");
        _portainer.On("GET", "/api/stacks/3/file", "", HttpStatusCode.Conflict);

        var integration = NewIntegration();
        _db.Integrations.Add(integration);
        var job = new StackUpdateJob { IntegrationId = integration.Id, StackId = 3, EnvironmentId = 1, StackName = "shortener", RequestedBy = "alice" };
        _db.StackUpdateJobs.Add(job);
        await _db.SaveChangesAsync();

        var factory = new PortainerClientFactory(_http, () => _portainer);
        var monitor = new PortainerMonitor(null!, factory, new Notifier(), new FeatureFlags(new AIOPatcherOptions()), NullLogger<PortainerMonitor>.Instance);
        await new PortainerStackUpdates(null!, factory, monitor, new Notifier(), NullLogger<PortainerStackUpdates>.Instance).RunAsync(_db, job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("HTTP 409", job.Summary);
        Assert.Null(integration.StatusJson); // nach Fehler nicht neu eingelesen
    }

    private sealed record Seen(string Method, string Path, string Query, string? ApiKey, string? Body);

    /// <summary>Portainer-API mit festen Antworten je Methode und Pfad (ohne Query).</summary>
    private sealed class FakePortainer : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(string Body, HttpStatusCode Code)>> _routes = [];
        public List<Seen> Requests { get; } = [];

        public void On(string method, string path, string body, HttpStatusCode code = HttpStatusCode.OK) =>
            _routes[$"{method} {path}"] = new Queue<(string, HttpStatusCode)>([(body, code)]);

        /// <summary>Antworten nacheinander, die letzte bleibt.</summary>
        public void OnSequence(string method, string path, params string[] bodies) =>
            _routes[$"{method} {path}"] = new Queue<(string, HttpStatusCode)>(bodies.Select(b => (b, HttpStatusCode.OK)));

        public void Remove(string method, string path) => _routes.Remove($"{method} {path}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new Seen(request.Method.Method, uri.AbsolutePath, uri.Query,
                request.Headers.TryGetValues("X-API-Key", out var key) ? key.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (!_routes.TryGetValue($"{request.Method.Method} {uri.AbsolutePath}", out var queue))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not found"}""") };
            }

            var (body, code) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
