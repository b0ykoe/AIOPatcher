using System.Text.Json;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// "Platz freigeben" mit Auswahl: Der Scan listet ungenutzte Images (kein Container, auch kein gestoppter, nutzt sie),
/// ungenutzte Volumes (docker volume ls --filter dangling=true) und den Build-Cache. Gelöscht wird nur, was ausgewählt ist
/// und beim Löschen noch im neuen Scan steht, per "docker image rm" und "docker volume rm" ohne -f: Docker selbst verweigert
/// zusätzlich alles, was inzwischen wieder genutzt wird.
/// </summary>
public static partial class DockerManager
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(2);

    private static async Task<TaskOutcome> ScanOutcomeAsync(Action<string> log, CancellationToken ct)
    {
        var scan = await ScanAsync(log, ct);
        var summary = $"{scan.Images.Count} unused images ({FormatSize(scan.Images.Sum(i => i.Size))}), {scan.Volumes.Count} unused volumes, " +
                      $"build cache {FormatSize(scan.BuildCacheSize)}.";
        log($"[agent] {summary}");
        return new TaskOutcome(JobOutcome.Succeeded, summary, PullJsonContext.ToElement(scan, PullJsonContext.Default.PruneScan));
    }

    /// <summary>Was ungenutzt ist, wie "docker image prune --all", "docker volume ls -f dangling=true" und der Build-Cache es sehen.</summary>
    public static async Task<PruneScan> ScanAsync(Action<string> log, CancellationToken ct)
    {
        // Images aller Container, auch gestoppter: die bleiben
        var ps = await ProcessRunner.RunAsync("docker", ["ps", "-a", "-q", "--no-trunc"], ScanTimeout, ct);
        var ids = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var used = (await DockerInventory.InspectAsync(ids, ScanTimeout, ct)).Select(c => c.ImageId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (ids.Length > 0 && used.Count == 0)
        {
            // Container da, aber nicht lesbar: lieber nichts als ungenutzt anbieten
            throw new InvalidOperationException("Could not read the images of the existing containers.");
        }

        log("$ docker image ls --no-trunc --format {{json .}}");
        var images = await ProcessRunner.RunAsync("docker", ["image", "ls", "--no-trunc", "--format", "{{json .}}"], ScanTimeout, ct);
        log("$ docker volume ls --filter dangling=true --format {{json .}}");
        var volumes = await ProcessRunner.RunAsync("docker", ["volume", "ls", "--filter", "dangling=true", "--format", "{{json .}}"], ScanTimeout, ct);

        // Volume-Größen gibt es nur über "system df -v" (dauert bei vielen Volumes); ohne geht es auch
        log("$ docker system df -v --format {{json .}}");
        var verbose = await ProcessRunner.RunAsync("docker", ["system", "df", "-v", "--format", "{{json .}}"], ScanTimeout, ct);
        var sizes = verbose.Success ? ParseVolumeSizes(verbose.StandardOutput) : null;
        var disk = await DiskUsageAsync(ct);
        var cache = disk.FirstOrDefault(d => d.Type.StartsWith("Build", StringComparison.OrdinalIgnoreCase));

        return new PruneScan(
            ParseUnusedImages(images.StandardOutput, used).Take(ContainerOps.MaxPruneItems).ToList(),
            ParseUnusedVolumes(volumes.StandardOutput, sizes).Take(ContainerOps.MaxPruneItems).ToList(),
            cache?.Reclaimable ?? 0, cache?.Count ?? 0, sizes is not null);
    }

    /// <summary>
    /// Zeilen von "docker image ls --no-trunc --format {{json .}}" (eine je Tag); gleiche ID = ein Image mit mehreren Namen.
    /// Ungenutzt = keine Container-ID verweist darauf. Größte zuerst.
    /// </summary>
    public static List<UnusedImage> ParseUnusedImages(string output, IReadOnlySet<string> usedImageIds)
    {
        var byId = new Dictionary<string, (List<string> Refs, long Size, string? Created)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var id = Str(e, "ID");
                if (id is null || !ContainerOps.IsValidImageId(id) || usedImageIds.Contains(id))
                {
                    continue;
                }

                if (!byId.TryGetValue(id, out var entry))
                {
                    entry = ([], ParseSize(Str(e, "Size") ?? "0"), Str(e, "CreatedAt"));
                    byId[id] = entry;
                }

                var repo = Str(e, "Repository");
                var tag = Str(e, "Tag");
                if (repo is { Length: > 0 } and not "<none>" && tag is { Length: > 0 } and not "<none>" && !entry.Refs.Contains($"{repo}:{tag}"))
                {
                    entry.Refs.Add($"{repo}:{tag}");
                }
            }
            catch (JsonException)
            {
            }
        }

        return byId.Select(kv => new UnusedImage(kv.Key, kv.Value.Refs, kv.Value.Size, kv.Value.Created))
            .OrderByDescending(i => i.Size).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"(?:^|,)com\.docker\.compose\.project=(?<p>[^,]*)")]
    private static partial Regex ComposeProjectLabel();

    /// <summary>Zeilen von "docker volume ls --filter dangling=true --format {{json .}}"; Labels kommen als "k=v,k=v".</summary>
    public static List<UnusedVolume> ParseUnusedVolumes(string output, IReadOnlyDictionary<string, long>? sizes)
    {
        var result = new List<UnusedVolume>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (Str(e, "Name") is not { } name || !ContainerOps.IsValidVolumeName(name))
                {
                    continue;
                }

                var project = Str(e, "Labels") is { } labels && ComposeProjectLabel().Match(labels) is { Success: true } m && m.Groups["p"].Value.Length > 0
                    ? m.Groups["p"].Value
                    : null;
                result.Add(new UnusedVolume(name, sizes is not null && sizes.TryGetValue(name, out var size) ? size : null, project, Str(e, "Driver")));
            }
            catch (JsonException)
            {
            }
        }

        return result.OrderByDescending(v => v.Size ?? -1).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>"docker system df -v --format {{json .}}": ein Objekt mit "Volumes":[{"Name":…,"Size":"1.2GB"}]; null, wenn nicht lesbar.</summary>
    public static Dictionary<string, long>? ParseVolumeSizes(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output.Trim());
            if (!doc.RootElement.TryGetProperty("Volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var v in volumes.EnumerateArray())
            {
                if (Str(v, "Name") is { } name && Str(v, "Size") is { } size)
                {
                    sizes[name] = ParseSize(size);
                }
            }

            return sizes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>
    /// Was von der Auswahl wirklich gelöscht wird: nur Einträge, die im aktuellen Scan stehen. Images werden über ihre Namen
    /// entfernt (beim letzten Namen löscht Docker das Image), ohne Namen über die ID.
    /// </summary>
    public static (List<string> ImageRefs, int Images, List<string> Volumes, List<string> Skipped) PlanClean(PruneScan scan, ContainerManageRequest request)
    {
        var skipped = new List<string>();
        var refs = new List<string>();
        var images = 0;
        foreach (var id in (request.Images ?? []).Distinct())
        {
            if (scan.Images.FirstOrDefault(i => i.Id == id || i.Id.EndsWith(id, StringComparison.Ordinal) && id.Length >= 12) is { } image)
            {
                refs.AddRange(image.References.Count > 0 ? image.References : [image.Id]);
                images++;
            }
            else
            {
                skipped.Add(id);
            }
        }

        var volumes = new List<string>();
        foreach (var name in (request.VolumeNames ?? []).Distinct())
        {
            if (scan.Volumes.Any(v => v.Name == name))
            {
                volumes.Add(name);
            }
            else
            {
                skipped.Add(name);
            }
        }

        return (refs.Distinct().ToList(), images, volumes, skipped);
    }

    private static async Task<TaskOutcome> CleanAsync(ContainerManageRequest request, Action<string> log, CancellationToken ct)
    {
        await LogDiskUsageAsync(log, ct);
        var scan = await ScanAsync(log, ct);
        var (refs, images, volumes, skipped) = PlanClean(scan, request);
        foreach (var item in skipped)
        {
            log($"[agent] {item}: no longer unused (or gone), skipped.");
        }

        var failed = new List<string>();
        if (refs.Count > 0)
        {
            log($"$ docker image rm {string.Join(' ', refs)}");
            if (await ProcessRunner.RunStreamingAsync("docker", ["image", "rm", .. refs], PruneTimeout, log, ct) != 0)
            {
                failed.Add("images");
            }
        }

        if (volumes.Count > 0)
        {
            log($"$ docker volume rm {string.Join(' ', volumes)}");
            if (await ProcessRunner.RunStreamingAsync("docker", ["volume", "rm", .. volumes], PruneTimeout, log, ct) != 0)
            {
                failed.Add("volumes");
            }
        }

        if (request.BuildCache)
        {
            log("$ docker builder prune --all --force");
            await ProcessRunner.RunStreamingAsync("docker", ["builder", "prune", "--all", "--force"], PruneTimeout, log, ct); // ältere Docker: kein Buildx
        }

        await LogDiskUsageAsync(log, ct);
        var summary = $"Deleted {images} images, {volumes.Count} volumes{(request.BuildCache ? ", build cache" : "")}" +
                      (skipped.Count > 0 ? $"; skipped {skipped.Count} that are in use again" : "") + ".";
        return failed.Count == 0
            ? new TaskOutcome(JobOutcome.Succeeded, summary)
            : new TaskOutcome(JobOutcome.Failed, $"{summary} Docker refused some {string.Join(" and ", failed)} (see log).");
    }
}
