using System.Text.Json;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Laufende Docker-Container über die docker-CLI. Nur lesen: ob ein neueres Image existiert, prüft der Server
/// zentral gegen die Registry (gecacht pro Image). Aktualisieren: <see cref="DockerUpdater"/>.
/// </summary>
public static class DockerInventory
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>null = kein Docker installiert oder nicht erreichbar.</summary>
    public static async Task<IReadOnlyList<ContainerReport>?> CollectAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/docker") && !File.Exists("/usr/local/bin/docker"))
        {
            return null;
        }

        try
        {
            var ps = await ProcessRunner.RunAsync("docker", ["ps", "-q", "--no-trunc"], Timeout, ct);
            if (!ps.Success)
            {
                return null; // Daemon läuft nicht
            }

            var ids = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length == 0)
            {
                return [];
            }

            var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", "{{.Name}}|{{.Config.Image}}|{{.Image}}|{{.State.Status}}|{{json .Config.Labels}}", .. ids], Timeout, ct);
            var containers = ParseContainers(inspect.StandardOutput, File.Exists);

            var imageIds = containers.Select(c => c.ImageId).OfType<string>().Distinct().ToArray();
            var images = imageIds.Length == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : ParseImageDigests((await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}|{{json .RepoDigests}}", .. imageIds], Timeout, ct)).StandardOutput);

            return containers
                .Select(c => c with { RepoDigests = c.ImageId is not null && images.TryGetValue(c.ImageId, out var d) ? d : [] })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
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
