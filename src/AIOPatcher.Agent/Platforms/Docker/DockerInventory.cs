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

    /// <summary>
    /// "docker inspect" als JSON lesen, ohne --format: Ein Go-Template bricht je Container ab, wenn ein Feld fehlt (z. B.
    /// "map has no entry for key Health" bei Containern ohne Healthcheck), und der Container fehlte dann ganz in der Liste.
    /// Einzelne nicht (mehr) vorhandene IDs: docker meldet einen Fehler, gibt die übrigen aber trotzdem als JSON aus.
    /// </summary>
    public static async Task<List<ContainerReport>> InspectAsync(IReadOnlyCollection<string> ids, TimeSpan timeout, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var inspect = await ProcessRunner.RunAsync("docker", ["inspect", .. ids], timeout, ct);
        return ParseInspect(inspect.StandardOutput, File.Exists);
    }

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

            var containers = await InspectAsync(ids, Timeout, ct);

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
    /// Ausgabe von "docker inspect" (JSON-Array, ein Objekt je Container). Fehlende Felder (State.Health ohne Healthcheck,
    /// Labels null) sind einfach leer; Einträge ohne Name oder Image werden übersprungen, kaputte Ausgabe ergibt eine leere Liste.
    /// </summary>
    public static List<ContainerReport> ParseInspect(string json, Func<string, bool>? fileExists = null)
    {
        var result = new List<ContainerReport>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var name = Str(c, "Name")?.TrimStart('/');
                var config = Obj(c, "Config");
                var image = config is { } cfg ? Str(cfg, "Image") : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(image))
                {
                    continue;
                }

                var state = Obj(c, "State");
                var health = state is { } s && Obj(s, "Health") is { } h ? Str(h, "Status") : null;
                var restart = Obj(c, "HostConfig") is { } host && Obj(host, "RestartPolicy") is { } policy ? Str(policy, "Name") : null;
                var labels = new Dictionary<string, string>();
                if (config is { } withLabels && Obj(withLabels, "Labels") is { } l)
                {
                    foreach (var p in l.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                    {
                        labels[p.Name] = p.Value.GetString()!;
                    }
                }

                result.Add(new ContainerReport(name, image, NullIfEmpty(Str(c, "Image")), null, state is { } st ? NullIfEmpty(Str(st, "Status")) : null,
                    ComposeFromLabels(labels, fileExists ?? (_ => false)),
                    Id: NullIfEmpty(Str(c, "Id")),
                    ExitCode: state is { } se && se.TryGetProperty("ExitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var code) ? code : null,
                    StartedAt: state is { } s1 && Str(s1, "StartedAt") is { } started ? DockerTime(started) : null,
                    FinishedAt: state is { } s2 && Str(s2, "FinishedAt") is { } finished ? DockerTime(finished) : null,
                    Health: NullIfEmpty(health),
                    RestartPolicy: NullIfEmpty(restart)));
            }
        }

        return result;

        static JsonElement? Obj(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        static string? Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
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
