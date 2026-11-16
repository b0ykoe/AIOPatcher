using System.Text.Json;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Laufende Docker-Container über die docker-CLI. Nur lesen: ob ein neueres Image existiert, prüft der Server
/// zentral gegen die Registry (gecacht pro Image), der Agent aktualisiert keine Container.
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

            var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", "{{.Name}}|{{.Config.Image}}|{{.Image}}|{{.State.Status}}", .. ids], Timeout, ct);
            var containers = ParseContainers(inspect.StandardOutput);

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

    /// <summary>Zeilen "/name|nginx:1.27|sha256:abc|running".</summary>
    public static List<ContainerReport> ParseContainers(string output)
    {
        var result = new List<ContainerReport>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4 || parts[1].Length == 0)
            {
                continue;
            }

            result.Add(new ContainerReport(parts[0].TrimStart('/'), parts[1], parts[2].Length > 0 ? parts[2] : null, null, parts[3]));
        }

        return result;
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
