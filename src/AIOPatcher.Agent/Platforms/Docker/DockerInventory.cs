using System.Globalization;
using System.Text.Json;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Docker-Container über die docker-CLI, auch gestoppte (Status, Exit-Code, Healthcheck). Nur lesen: ob ein neueres
/// Image existiert, prüft der Server zentral gegen die Registry (gecacht pro Image). Selbst gebaute Compose-Dienste
/// bringen ihr Basis-Image mit (<see cref="DockerBuild"/>). Aktualisieren: <see cref="DockerUpdater"/>.
/// </summary>
public static class DockerInventory
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxContainers = 200;

    /// <summary>docker inspect: Labels zuletzt, sie dürfen selbst "|" enthalten.</summary>
    public const string DetailedFormat =
        "{{.Name}}|{{.Config.Image}}|{{.Image}}|{{.State.Status}}|{{.Id}}|{{.State.ExitCode}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|" +
        "{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{.HostConfig.RestartPolicy.Name}}|{{json .Config.Labels}}";

    /// <summary>null = kein Docker installiert oder nicht erreichbar.</summary>
    public static async Task<IReadOnlyList<ContainerReport>?> CollectAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/docker") && !File.Exists("/usr/local/bin/docker"))
        {
            return null;
        }

        try
        {
            var ps = await ProcessRunner.RunAsync("docker", ["ps", "-a", "-q", "--no-trunc"], Timeout, ct);
            if (!ps.Success)
            {
                return null; // Daemon läuft nicht
            }

            var ids = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxContainers).ToArray();
            if (ids.Length == 0)
            {
                return [];
            }

            var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", DetailedFormat, .. ids], Timeout, ct);
            var containers = ParseDetailed(inspect.StandardOutput, File.Exists);

            var imageIds = containers.Select(c => c.ImageId).OfType<string>().Distinct().ToArray();
            var images = imageIds.Length == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : ParseImageDigests((await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}|{{json .RepoDigests}}", .. imageIds], Timeout, ct)).StandardOutput);

            containers = containers
                .Select(c => c with { RepoDigests = c.ImageId is not null && images.TryGetValue(c.ImageId, out var d) ? d : [] })
                .ToList();
            return await WithBuildInfoAsync(containers, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Selbst gebaute Compose-Dienste (Image ohne Registry-Digest): Dockerfile und Basis-Image aus "compose config",
    /// dazu die Digests der lokalen Kopie des Basis-Images. Fehler hier lassen die Inventur nicht scheitern.
    /// </summary>
    private static async Task<List<ContainerReport>> WithBuildInfoAsync(List<ContainerReport> containers, CancellationToken ct)
    {
        var local = containers.Where(c => c is { RepoDigests.Count: 0, Compose.FilesFound: true }).ToList();
        if (local.Count == 0)
        {
            return containers;
        }

        var builds = new Dictionary<(string Project, string Service), BuildInfo>();
        foreach (var project in local.GroupBy(c => c.Compose!.Project).Take(20))
        {
            try
            {
                foreach (var (service, build) in await DockerBuild.ProjectAsync(project.First().Compose!, ct))
                {
                    builds[(project.Key, service)] = build;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ohne Build-Angaben weiter
            }
        }

        var baseImages = builds.Values.Select(b => b.BaseImage).OfType<string>().Distinct().ToArray();
        var baseDigests = new Dictionary<string, IReadOnlyList<string>>();
        if (baseImages.Length > 0)
        {
            // Basis-Image fehlt lokal (z. B. aufgeräumt): docker meldet einen Fehler, die übrigen kommen trotzdem
            var inspect = await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{json .RepoDigests}}", .. baseImages], Timeout, ct);
            var lines = inspect.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == baseImages.Length)
            {
                for (var i = 0; i < baseImages.Length; i++)
                {
                    baseDigests[baseImages[i]] = ParseImageDigests($"x|{lines[i]}").GetValueOrDefault("x") ?? [];
                }
            }
            else
            {
                foreach (var image in baseImages)
                {
                    var one = await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{json .RepoDigests}}", image], Timeout, ct);
                    baseDigests[image] = one.Success ? ParseImageDigests($"x|{one.StandardOutput.Trim()}").GetValueOrDefault("x") ?? [] : [];
                }
            }
        }

        return containers.Select(c => c.Compose is { } compose && builds.TryGetValue((compose.Project, compose.Service), out var build) && c.RepoDigests is { Count: 0 }
                ? c with { Build = build with { BaseRepoDigests = build.BaseImage is { } b ? baseDigests.GetValueOrDefault(b) ?? [] : [] } }
                : c)
            .ToList();
    }

    /// <summary>
    /// Zeilen im <see cref="DetailedFormat"/>: "/name|image|sha256:img|exited|id|137|2026-…Z|2026-…Z|unhealthy|always|{Labels}".
    /// </summary>
    public static List<ContainerReport> ParseDetailed(string output, Func<string, bool>? fileExists = null)
    {
        var result = new List<ContainerReport>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 11);
            if (parts.Length < 10 || parts[1].Length == 0)
            {
                continue;
            }

            var compose = parts.Length == 11 ? ComposeFromLabels(ParseLabels(parts[10]), fileExists ?? (_ => false)) : null;
            result.Add(new ContainerReport(parts[0].TrimStart('/'), parts[1], parts[2].Length > 0 ? parts[2] : null, null, parts[3], compose,
                Id: parts[4].Length > 0 ? parts[4] : null,
                ExitCode: int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exit) ? exit : null,
                StartedAt: DockerTime(parts[6]),
                FinishedAt: DockerTime(parts[7]),
                Health: parts[8].Length > 0 ? parts[8] : null,
                RestartPolicy: parts[9].Length > 0 ? parts[9] : null));
        }

        return result;
    }

    /// <summary>"2026-11-19T07:00:00.123456789Z" → UTC; "0001-01-01T00:00:00Z" (nie) → null.</summary>
    public static DateTime? DockerTime(string value)
    {
        if (value.Length < 19 || value.StartsWith("0001-", StringComparison.Ordinal))
        {
            return null;
        }

        // Nanosekunden auf 7 Stellen kürzen, die .NET versteht
        var dot = value.IndexOf('.');
        var trimmed = dot > 0 && value.IndexOfAny(['Z', '+', '-'], dot) is var end and > 0 && end - dot > 8
            ? value[..(dot + 8)] + value[end..]
            : value;
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;
    }

    /// <summary>Zeilen "/name|nginx:1.27|sha256:abc|running|{Labels als JSON}" (Labels dürfen selbst "|" enthalten).</summary>
    public static List<ContainerReport> ParseContainers(string output, Func<string, bool>? fileExists = null)
    {
        var result = new List<ContainerReport>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 5);
            if (parts.Length < 4 || parts[1].Length == 0)
            {
                continue;
            }

            var compose = parts.Length == 5 ? ComposeFromLabels(ParseLabels(parts[4]), fileExists ?? (_ => false)) : null;
            result.Add(new ContainerReport(parts[0].TrimStart('/'), parts[1], parts[2].Length > 0 ? parts[2] : null, null, parts[3], compose));
        }

        return result;
    }

    /// <summary>{"com.docker.compose.project":"web", …} → Wörterbuch; "null" oder Unsinn → leer.</summary>
    public static Dictionary<string, string> ParseLabels(string json)
    {
        var labels = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        labels[p.Name] = p.Value.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // kaputte Ausgabe: wie ohne Labels
        }

        return labels;
    }

    /// <summary>Compose-Herkunft aus den Labels; null, wenn der Container nicht per Compose gestartet wurde.</summary>
    public static ComposeInfo? ComposeFromLabels(IReadOnlyDictionary<string, string> labels, Func<string, bool> fileExists)
    {
        if (!labels.TryGetValue("com.docker.compose.project", out var project) || !labels.TryGetValue("com.docker.compose.service", out var service)
            || project.Length == 0 || service.Length == 0)
        {
            return null;
        }

        var workingDir = labels.GetValueOrDefault("com.docker.compose.project.working_dir");
        var files = (labels.GetValueOrDefault("com.docker.compose.project.config_files") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.StartsWith('/') || string.IsNullOrEmpty(workingDir) ? f : $"{workingDir.TrimEnd('/')}/{f}")
            .ToList();
        return new ComposeInfo(project, service, string.IsNullOrEmpty(workingDir) ? null : workingDir, files, files.Count > 0 && files.All(fileExists));
    }

    /// <summary>Zeilen "sha256:abc|["nginx@sha256:…"]".</summary>
    public static Dictionary<string, IReadOnlyList<string>> ParseImageDigests(string output)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var digests = new List<string>();
            try
            {
                using var json = JsonDocument.Parse(line[(separator + 1)..]);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    digests.AddRange(json.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>());
                }
            }
            catch (JsonException)
            {
                // "null" oder kaputte Ausgabe: Image ohne Registry-Digest
            }

            result[line[..separator]] = digests;
        }

        return result;
    }
}
