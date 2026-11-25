using System.Text.Json;
using AIOPatcher.Agent.Platforms.Docker;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Container: Status auch gestoppter Container, selbst gebaute Images (Dockerfile), Paketlisten für den CVE-Abgleich.</summary>
public sealed class ContainerStatusTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FeatureFlags _features = new(new AIOPatcherOptions());
    private readonly AgentDownloads _downloads = new(new AIOPatcherOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N")) });
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    public ContainerStatusTests()
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

    // --- Agent: Auswertung ---------------------------------------------------------------------------

    [Fact]
    public void Inventory_ReadsStateExitCodeHealthAndRestartPolicy()
    {
        // wie "docker inspect" ohne --format; worker ohne Healthcheck hat gar kein "Health" (daran scheiterte das Go-Template)
        var output = """
            [
              {"Id":"aaa111","Name":"/web","Image":"sha256:img1",
               "State":{"Status":"running","ExitCode":0,"StartedAt":"2026-11-19T07:00:00.123456789Z","FinishedAt":"0001-01-01T00:00:00Z","Health":{"Status":"healthy","FailingStreak":0}},
               "HostConfig":{"RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0}},
               "Config":{"Image":"nginx:1.27","Labels":{"a":"b|c"}}},
              {"Id":"bbb222","Name":"/worker","Image":"sha256:img2",
               "State":{"Status":"exited","ExitCode":137,"StartedAt":"2026-11-18T07:00:00Z","FinishedAt":"2026-11-19T08:30:15.5Z"},
               "HostConfig":{"RestartPolicy":{"Name":"always"}},
               "Config":{"Image":"app:latest","Labels":null}},
              {"Id":"ccc333","Name":"/broken","State":{}}
            ]
            """;
        var containers = DockerInventory.ParseInspect(output);

        Assert.Equal(2, containers.Count);
        var web = containers[0];
        Assert.Equal(("web", "running", "aaa111", 0, "healthy", "unless-stopped"), (web.Name, web.State, web.Id, web.ExitCode, web.Health, web.RestartPolicy));
        Assert.Equal(new DateTime(2026, 11, 19, 7, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567), web.StartedAt);
        Assert.Null(web.FinishedAt); // 0001-01-01: läuft seitdem

        var worker = containers[1];
        Assert.Equal(("exited", 137, (string?)null), (worker.State, worker.ExitCode, worker.Health));
        Assert.Equal(new DateTime(2026, 11, 19, 8, 30, 15, 500, DateTimeKind.Utc), worker.FinishedAt);
        Assert.Equal("always", worker.RestartPolicy);
        Assert.Null(worker.Compose);

        Assert.Empty(DockerInventory.ParseInspect("Error: No such object: x"));
        Assert.Empty(DockerInventory.ParseInspect("[]"));
    }

    [Fact]
    public void Dockerfile_FromLines_MultiStageArgsAndScratch()
    {
        var (bases, final) = DockerBuild.ParseFrom("""
            # syntax=docker/dockerfile:1
            ARG NODE_VERSION=20
            ARG RUNTIME
            FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-alpine AS build
            RUN npm ci && \
                npm run build
            FROM build AS test
            FROM ${RUNTIME:-nginx:1.27-alpine}
            COPY --from=build /app/dist /usr/share/nginx/html
            """);
        Assert.Equal(["node:20-alpine", "nginx:1.27-alpine"], bases);
        Assert.Equal("nginx:1.27-alpine", final);

        // Build-Args aus Compose haben Vorrang; letzte Stufe auf einer früheren Stufe aufgebaut
        (bases, final) = DockerBuild.ParseFrom("ARG BASE=debian:12\nFROM $BASE AS base\nRUN apt-get update\nFROM base\n", new Dictionary<string, string> { ["BASE"] = "ubuntu:24.04" });
        Assert.Equal(["ubuntu:24.04"], bases);
        Assert.Equal("ubuntu:24.04", final);

        (bases, final) = DockerBuild.ParseFrom("FROM golang:1.23 AS build\nFROM scratch\nCOPY --from=build /app /app\n");
        Assert.Equal(["golang:1.23"], bases);
        Assert.Null(final); // läuft auf scratch: kein Basis-Image zu prüfen
    }

    [Fact]
    public void ComposeConfig_FindsBuildServices()
    {
        const string json = """
            {"name":"shop","services":{
              "web":{"build":{"context":"/srv/shop","dockerfile":"docker/Dockerfile.web","args":{"PY":"3.12"}},"image":"shop-web"},
              "db":{"image":"postgres:16"},
              "tool":{"build":"/srv/shop/tool"},
              "remote":{"build":{"context":"https://github.com/org/repo.git"}},
              "inline":{"build":{"context":"/srv/shop","dockerfile_inline":"FROM alpine:3.20\nRUN true"}}
            }}
            """;
        var files = new Dictionary<string, string>
        {
            ["/srv/shop/docker/Dockerfile.web"] = "ARG PY=3.11\nFROM python:${PY}-slim\n",
            ["/srv/shop/tool/Dockerfile"] = "FROM debian:12\n",
        };
        var builds = DockerBuild.ParseConfig(json, files.ContainsKey, files.GetValueOrDefault);

        Assert.Equal(["inline", "remote", "tool", "web"], builds.Keys.Order());
        Assert.Equal(("/srv/shop/docker/Dockerfile.web", "python:3.12-slim"), (builds["web"].Dockerfile, builds["web"].BaseImage));
        Assert.Equal("debian:12", builds["tool"].BaseImage);
        Assert.Null(builds["remote"].BaseImage); // Git-Kontext: Dockerfile unbekannt
        Assert.Equal(("(inline)", "alpine:3.20"), (builds["inline"].Dockerfile, builds["inline"].BaseImage));
    }

    [Fact]
    public void Packages_OsReleaseDpkgAndApk()
    {
        Assert.Equal(("debian", "12"), DockerImagePackages.ParseOsRelease("PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nID=debian\nVERSION_ID=\"12\"\n"));

        var dpkg = DockerImagePackages.ParseDpkgStatus("""
            Package: libssl3
            Status: install ok installed
            Source: openssl (3.0.15-1~deb12u1)
            Version: 3.0.15-1~deb12u1
            Description: Secure Sockets Layer toolkit
             continued description line: with colon

            Package: removed-thing
            Status: deinstall ok config-files
            Version: 1.0

            Package: bash
            Status: install ok installed
            Version: 5.2.15-2+b7
            Source: bash
            """);
        Assert.Equal([new ImagePackage("libssl3", "3.0.15-1~deb12u1", "openssl", "3.0.15-1~deb12u1"), new ImagePackage("bash", "5.2.15-2+b7")], dpkg);

        var apk = DockerImagePackages.ParseApkInstalled("C:Q1abc=\nP:libcrypto3\nV:3.3.2-r1\no:openssl\n\nP:busybox\nV:1.36.1-r29\no:busybox\n");
        Assert.Equal([new ImagePackage("libcrypto3", "3.3.2-r1", "openssl"), new ImagePackage("busybox", "1.36.1-r29")], apk);
    }

    [Theory]
    [InlineData("debian", "12", "Debian:12")]
    [InlineData("ubuntu", "22.04", "Ubuntu:22.04:LTS")]
    [InlineData("ubuntu", "23.10", "Ubuntu:23.10")]
    [InlineData("alpine", "3.20.3", "Alpine:v3.20")]
    [InlineData("debian", null, null)]
    [InlineData("rocky", "9.4", null)]
    public void Ecosystem_ForImages(string? id, string? version, string? expected) => Assert.Equal(expected, CveMapping.EcosystemForImage(id, version));

    // --- Server -----------------------------------------------------------------------------------------

    private ManagedHost Host()
    {
        var host = new ManagedHost
        {
            Site = new Site { Name = "Home" }, Name = "docker1", Hostname = "docker1", CertificateThumbprint = "x",
            CertificateExpiresAt = Now.AddYears(1), OsFamily = OsFamily.Linux,
            ReportedModules = JsonSerializer.Serialize(new List<ModuleInfo>
            {
                new(AgentModules.Docker, true, false, [ModuleActions.Inventory, ModuleActions.AppUpdate, ModuleActions.Packages]),
            }, ModuleJson.Default.ListModuleInfo),
        };
        _db.Hosts.Add(host);
        _db.SaveChanges();
        return host;
    }

    private ModuleContext Context(Notifier notifier) => new(_db, notifier, _features, _downloads, null, Now,
        new Dictionary<(string, string), DateTime> { [(AgentModules.Docker, ModuleActions.Inventory)] = Now.AddMinutes(-5) });

    private static ContainerReport Running(string name, string image, string imageId, string id) =>
        new(name, image, imageId, [$"{image.Split(':')[0]}@sha256:aaa"], "running", null, id, 0, Now.AddDays(-1), null, null, "always");

    private static TaskResultRequest Inventory(params ContainerReport[] containers) =>
        new(JobOutcome.Succeeded, "ok", PullJsonContext.ToElement(new ContainerList(containers), PullJsonContext.Default.ContainerList));

    [Fact]
    public async Task Inventory_StoppedContainer_IsReportedOnce_AndOneShotJobsAreQuiet()
    {
        var host = Host();
        var notifier = new Notifier();
        var module = new DockerServerModule();
        var task = new AgentTaskRecord { HostId = host.Id, Module = AgentModules.Docker, Action = ModuleActions.Inventory };

        var job = Running("backup", "restic/restic:0.17", "sha256:job", "cid3") with { RestartPolicy = "no" };
        await module.HandleAsync(Context(notifier), host, task, Inventory(Running("web", "nginx:1.27", "sha256:img1", "cid1"), job), CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.False(notifier.Reader.TryRead(out _));

        // web stürzt ab (137), der Einmal-Job endet sauber (Exit 0, Restart "no")
        await module.HandleAsync(Context(notifier), host, task, Inventory(
            Running("web", "nginx:1.27", "sha256:img1", "cid1") with { State = "exited", ExitCode = 137, FinishedAt = Now },
            job with { State = "exited", ExitCode = 0, FinishedAt = Now }), CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.True(notifier.Reader.TryRead(out var stopped));
        Assert.Equal(NotificationEvents.ContainerStopped, stopped!.Event);
        Assert.Contains("web", stopped.Title);
        Assert.Contains("137", stopped.Message);
        Assert.False(notifier.Reader.TryRead(out _));

        var web = await _db.Containers.SingleAsync(c => c.Name == "web");
        Assert.Equal(("exited", 137, "always"), (web.State, web.ExitCode, web.RestartPolicy));

        // weiterhin gestoppt: keine zweite Meldung
        await module.HandleAsync(Context(notifier), host, task, Inventory(
            Running("web", "nginx:1.27", "sha256:img1", "cid1") with { State = "exited", ExitCode = 137, FinishedAt = Now }), CancellationToken.None);
        await _db.SaveChangesAsync();
        Assert.False(notifier.Reader.TryRead(out _));
    }

    [Fact]
    public async Task SelfBuiltImages_CheckTheBaseImage_LocalImagesWithoutBuildAreNotChecked()
    {
        var host = Host();
        var built = new ContainerReport("shop-web-1", "shop-web", "sha256:built", [], "running", new ComposeInfo("shop", "web", "/srv/shop", ["/srv/shop/compose.yml"], true),
            "cid1", Build: new BuildInfo("/srv/shop", "/srv/shop/Dockerfile", ["python:3.12-slim"], "python:3.12-slim", ["python@sha256:base1"]));
        var local = new ContainerReport("handmade", "handmade:dev", "sha256:local", [], "running", null, "cid2");

        await ContainerUpdateService.ReplaceContainersAsync(_db, host, [built, local], Now, CancellationToken.None);
        await _db.SaveChangesAsync();

        var web = await _db.Containers.SingleAsync(c => c.Name == "shop-web-1");
        Assert.Equal(("docker.io/library/python:3.12-slim", "sha256:base1", "python:3.12-slim", true), (web.Reference, web.LocalDigest, web.BaseImage, web.LocalImage));

        var handmade = await _db.Containers.SingleAsync(c => c.Name == "handmade");
        Assert.Null(handmade.Reference); // nichts in einer Registry, womit man vergleichen könnte
        Assert.True(handmade.LocalImage);
    }

    [Fact]
    public async Task Packages_AreRequestedOncePerImage_AndStoredForAllHosts()
    {
        var host = Host();
        var module = new DockerServerModule();
        var notifier = new Notifier();
        var inventory = new AgentTaskRecord { HostId = host.Id, Module = AgentModules.Docker, Action = ModuleActions.Inventory };
        await module.HandleAsync(Context(notifier), host, inventory, Inventory(
            Running("web", "nginx:1.27", "sha256:img1", "cid1"),
            Running("web2", "nginx:1.27", "sha256:img1", "cid2") with { State = "exited" },
            Running("db", "postgres:16", "sha256:img2", "cid3")), CancellationToken.None);

        var planned = module.Due(host, Context(notifier), null);
        Assert.Equal(ModuleActions.Packages, planned?.Action);
        var request = PullJsonContext.FromElement(planned!.Payload, PullJsonContext.Default.ImagePackagesRequest)!;
        Assert.Equal([new ImagePackagesTarget("cid1", "sha256:img1"), new ImagePackagesTarget("cid3", "sha256:img2")], request.Images.OrderBy(i => i.ImageId));

        var answer = new ImagePackagesResult([
            new ImagePackages("sha256:img1", "debian", "12", "dpkg", [new ImagePackage("libssl3", "3.0.15-1~deb12u1", "openssl", "3.0.15-1~deb12u1"), new ImagePackage("openssl", "3.0.15-1~deb12u1")]),
            new ImagePackages("sha256:img2", null, null, null, [], "No dpkg or apk package database in the image."),
        ]);
        await module.HandleAsync(Context(notifier), host, new AgentTaskRecord { HostId = host.Id, Module = AgentModules.Docker, Action = ModuleActions.Packages },
            new TaskResultRequest(JobOutcome.Succeeded, "ok", PullJsonContext.ToElement(answer, PullJsonContext.Default.ImagePackagesResult)), CancellationToken.None);

        var nginx = await _db.ImagePackageSets.SingleAsync(s => s.ImageId == "sha256:img1");
        Assert.Equal(("Debian:12", 2, (DateTime?)null), (nginx.Ecosystem, nginx.PackageCount, nginx.ScannedAt));
        Assert.Equal([("openssl", "3.0.15-1~deb12u1")], CveService.ImageSourcePackages(nginx)); // Quellpaket wie bei Hosts
        Assert.NotNull((await _db.ImagePackageSets.SingleAsync(s => s.ImageId == "sha256:img2")).Error);

        // nächste Inventur: nichts mehr anzufordern (auch das Image ohne Paketdatenbank erst nach einem Tag wieder)
        await module.HandleAsync(Context(notifier), host, inventory, Inventory(Running("web", "nginx:1.27", "sha256:img1", "cid1"), Running("db", "postgres:16", "sha256:img2", "cid3")),
            CancellationToken.None);
        Assert.Null(module.Due(host, Context(notifier), null));
    }
}
