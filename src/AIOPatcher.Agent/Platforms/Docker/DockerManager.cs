using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Mini-Docker-Manager (docker.manage): start/stop/restart, Container löschen, Stack "down", Platz freigeben, Compose-Datei
/// lesen/schreiben und neue Stacks anlegen. Jede Aktion braucht policy.json allowContainerManagement, Compose zusätzlich
/// allowComposeEdit; beides lässt sich nur auf dem Host setzen. Alle Befehle laufen ohne Shell mit Argumentliste, Namen
/// werden geprüft (<see cref="ContainerOps.Invalid"/>), Compose-Dateien nur unter dem Basisordner (composeRoot), echte Pfade
/// (Symlinks aufgelöst).
/// </summary>
public static partial class DockerManager
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(20);

    public const string PolicyHint = "sudo aiopatcher-agent policy set allowContainerManagement true";
    public const string ComposePolicyHint = "sudo aiopatcher-agent policy set allowComposeEdit true";

    /// <summary>Warum die lokale Policy diese Aktion verbietet; null = erlaubt.</summary>
    public static string? PolicyRejects(AgentPolicy policy, string op) =>
        !policy.AllowContainerManagement
            ? $"Local policy forbids managing containers (allowContainerManagement: false). To allow it on this host: {PolicyHint}"
            : ContainerOps.NeedsComposeEdit(op) && !policy.AllowComposeEdit
                ? $"Local policy forbids editing compose files (allowComposeEdit: false). To allow it on this host: {ComposePolicyHint}"
                : null;

    public static async Task<TaskOutcome> RunAsync(ContainerManageRequest request, AgentPolicy policy, Action<string> log, CancellationToken ct)
    {
        if (PolicyRejects(policy, request.Op) is { } rejected)
        {
            log($"[agent] Rejected: {rejected}");
            return TaskOutcome.Rejected(rejected);
        }

        if (ContainerOps.Invalid(request) is { } invalid)
        {
            log($"[agent] Rejected: {invalid}");
            return TaskOutcome.Rejected(invalid);
        }

        return request.Op switch
        {
            ContainerOps.Start or ContainerOps.Stop or ContainerOps.Restart => await LifecycleAsync(request.Op, request.Containers!, log, ct),
            ContainerOps.Remove => await RemoveAsync(request.Containers!, request.Volumes, log, ct),
            ContainerOps.Down => await DownAsync(request.Project!, request.Volumes, log, ct),
            ContainerOps.Prune => await PruneAsync(request.Volumes, log, ct),
            ContainerOps.ReadCompose => await ReadComposeAsync(request.Project!, policy.EffectiveComposeRoot, log, ct),
            ContainerOps.WriteCompose => await WriteComposeAsync(request, policy.EffectiveComposeRoot, log, ct),
            _ => await CreateAsync(request, policy.EffectiveComposeRoot, log, ct),
        };
    }

    // --- Niedrig: start, stop, restart ------------------------------------------------------------------

    /// <summary>"docker start|stop|restart &lt;namen&gt;".</summary>
    public static List<string> LifecycleArguments(string op, IEnumerable<string> names) => [op, .. names];

    private static async Task<TaskOutcome> LifecycleAsync(string op, IReadOnlyList<string> names, Action<string> log, CancellationToken ct)
    {
        var args = LifecycleArguments(op, names);
        log($"$ docker {string.Join(' ', args)}");
        if (await ProcessRunner.RunStreamingAsync("docker", args, LifecycleTimeout, log, ct) != 0)
        {
            return new TaskOutcome(JobOutcome.Failed, $"docker {op} failed (see log).");
        }

        var verb = op switch { ContainerOps.Start => "Started", ContainerOps.Stop => "Stopped", _ => "Restarted" };
        return new TaskOutcome(JobOutcome.Succeeded, $"{verb}: {Names(names)}.");
    }

    // --- Mittel: löschen, down, Platz freigeben ---------------------------------------------------------

    /// <summary>Erst sauber stoppen, dann entfernen; "-v" nimmt anonyme Volumes mit (benannte Volumes bleiben immer).</summary>
    public static List<string> RemoveArguments(IEnumerable<string> names, bool volumes) => ["rm", .. volumes ? new[] { "-v" } : [], .. names];

    private static async Task<TaskOutcome> RemoveAsync(IReadOnlyList<string> names, bool volumes, Action<string> log, CancellationToken ct)
    {
        log($"$ docker stop {string.Join(' ', names)}");
        await ProcessRunner.RunStreamingAsync("docker", ["stop", .. names], LifecycleTimeout, log, ct); // schon gestoppt: egal
        var args = RemoveArguments(names, volumes);
        log($"$ docker {string.Join(' ', args)}");
        return await ProcessRunner.RunStreamingAsync("docker", args, LifecycleTimeout, log, ct) == 0
            ? new TaskOutcome(JobOutcome.Succeeded, $"Removed: {Names(names)}{(volumes ? " (with anonymous volumes)" : "")}.")
            : new TaskOutcome(JobOutcome.Failed, "docker rm failed (see log).");
    }

    /// <summary>"compose … down" (mit Volumes: "--volumes": benannte und anonyme Volumes des Projekts).</summary>
    public static List<string> DownArguments(ComposeInfo compose, bool volumes) =>
        [.. DockerUpdater.ProjectArguments(compose), "down", .. volumes ? new[] { "--volumes" } : []];

    private static async Task<TaskOutcome> DownAsync(string project, bool volumes, Action<string> log, CancellationToken ct)
    {
        if (await FindProjectAsync(project, ct) is not { } compose)
        {
            return TaskOutcome.Rejected($"No compose file found on the host for stack {project}. Remove its containers individually instead.");
        }

        if (await DockerUpdater.FindComposeAsync(ct) is not { } command)
        {
            return TaskOutcome.Rejected("Neither \"docker compose\" nor \"docker-compose\" is installed.");
        }

        var args = DownArguments(compose, volumes);
        log($"$ {string.Join(' ', command)} {string.Join(' ', args)}");
        return await ProcessRunner.RunStreamingAsync(command[0], [.. command.Skip(1), .. args], LifecycleTimeout, log, ct, WorkDir(compose)) == 0
            ? new TaskOutcome(JobOutcome.Succeeded, $"Stack {project} taken down{(volumes ? " including its volumes" : "")}. The compose file stays.")
            : new TaskOutcome(JobOutcome.Failed, $"compose down for {project} failed (see log).");
    }

    /// <summary>Befehle für "Platz freigeben": ungenutzte Images (auch ohne Tag), Build-Cache, auf Wunsch ungenutzte Volumes.</summary>
    public static List<string[]> PruneCommands(bool volumes) =>
    [
        ["image", "prune", "--all", "--force"],
        ["builder", "prune", "--all", "--force"],
        .. volumes ? new[] { new[] { "volume", "prune", "--force" } } : [],
    ];

    [GeneratedRegex(@"Total reclaimed space:\s*(?<size>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReclaimedPattern();

    private static async Task<TaskOutcome> PruneAsync(bool volumes, Action<string> log, CancellationToken ct)
    {
        await LogDiskUsageAsync(log, ct);
        long freed = 0;
        var failed = new List<string>();
        foreach (var args in PruneCommands(volumes))
        {
            log($"$ docker {string.Join(' ', args)}");
            var exit = await ProcessRunner.RunStreamingAsync("docker", args, PruneTimeout, line =>
            {
                log(line);
                if (ReclaimedPattern().Match(line) is { Success: true } m)
                {
                    freed += ParseSize(m.Groups["size"].Value);
                }
            }, ct);
            if (exit != 0 && args[0] != "builder") // ältere Docker ohne Buildx kennen "builder prune" nicht
            {
                failed.Add(args[0]);
            }
        }

        await LogDiskUsageAsync(log, ct);
        var summary = $"Freed {FormatSize(freed)} (unused images and build cache{(volumes ? ", unused volumes" : "")}).";
        return failed.Count == 0
            ? new TaskOutcome(JobOutcome.Succeeded, summary)
            : new TaskOutcome(JobOutcome.Failed, $"{summary} Failed: {string.Join(", ", failed)} prune (see log).");
    }

    private static async Task LogDiskUsageAsync(Action<string> log, CancellationToken ct)
    {
        log("$ docker system df");
        await ProcessRunner.RunStreamingAsync("docker", ["system", "df"], ShortTimeout, log, ct);
    }

    /// <summary>"docker system df" (je Typ eine JSON-Zeile); Fehler ergeben eine leere Liste.</summary>
    public static async Task<IReadOnlyList<DockerDiskUsage>> DiskUsageAsync(CancellationToken ct)
    {
        try
        {
            var df = await ProcessRunner.RunAsync("docker", ["system", "df", "--format", "{{json .}}"], ShortTimeout, ct);
            return df.Success ? ParseDiskUsage(df.StandardOutput) : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Zeilen wie {"Type":"Images","TotalCount":"12","Active":"5","Size":"3.2GB","Reclaimable":"1.1GB (34%)"}.</summary>
    public static List<DockerDiskUsage> ParseDiskUsage(string output)
    {
        var result = new List<DockerDiskUsage>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                string? Str(string key) => e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
                if (Str("Type") is not { Length: > 0 } type)
                {
                    continue;
                }

                var reclaimable = (Str("Reclaimable") ?? "0").Split(' ')[0];
                result.Add(new DockerDiskUsage(type, ParseSize(Str("Size") ?? "0"), ParseSize(reclaimable),
                    int.TryParse(Str("TotalCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0));
            }
            catch (JsonException)
            {
                // Zeile überspringen
            }
        }

        return result;
    }

    [GeneratedRegex(@"^(?<n>[0-9]+(?:\.[0-9]+)?)\s*(?<u>[kKMGTP]?i?B)?$")]
    private static partial Regex SizePattern();

    /// <summary>Docker-Größen ("1.2GB", "512kB", "0B", "3.4MiB") in Bytes; Docker rechnet mit 1000er-Einheiten.</summary>
    public static long ParseSize(string value)
    {
        if (SizePattern().Match(value.Trim()) is not { Success: true } m)
        {
            return 0;
        }

        var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var unit = m.Groups["u"].Value.ToUpperInvariant();
        var binary = unit.Contains('I');
        var factor = (unit.Length > 0 ? unit[0] : 'B') switch
        {
            'K' => 1d,
            'M' => 2d,
            'G' => 3d,
            'T' => 4d,
            'P' => 5d,
            _ => 0d,
        };
        return (long)(n * Math.Pow(binary ? 1024 : 1000, factor));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1000 && i < units.Length - 1)
        {
            value /= 1000;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[i]}";
    }

    // --- Hoch: Compose lesen, schreiben, neuer Stack ----------------------------------------------------

    /// <summary>
    /// Liegt <paramref name="path"/> im Ordner <paramref name="root"/> (nicht der Ordner selbst)? Nur Text: ".." und doppelte
    /// "/" werden aufgelöst, Symlinks prüft <see cref="RealPath"/>.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        if (!root.StartsWith('/') || !path.StartsWith('/'))
        {
            return false;
        }

        var r = Normalize(root).TrimEnd('/');
        var p = Normalize(path);
        return r.Length > 0 && p.StartsWith(r + "/", StringComparison.Ordinal) && p.Length > r.Length + 1;
    }

    /// <summary>Unix-Pfad ohne ".", ".." und doppelte "/" (unabhängig vom Betriebssystem, auf dem die Tests laufen).</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>Pfad mit aufgelösten Symlinks (jede Ebene), damit ein Link im Basisordner nicht nach /etc zeigen kann.</summary>
    public static string RealPath(string path)
    {
        var current = "/";
        var queue = new Queue<string>(Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
        var hops = 0;
        while (queue.Count > 0)
        {
            var next = current == "/" ? "/" + queue.Dequeue() : $"{current}/{queue.Dequeue()}";
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is { } target)
            {
                if (++hops > 40)
                {
                    throw new IOException("Too many symbolic links.");
                }

                var resolved = Normalize(target.StartsWith('/') ? target : $"{current}/{target}");
                var rest = queue.ToList();
                queue = new Queue<string>(resolved.Split('/', StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                current = "/";
                continue;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Compose-Herkunft eines Projekts aus den Labels seiner Container (erste mit vorhandenen Dateien).</summary>
    private static async Task<ComposeInfo?> FindProjectAsync(string project, CancellationToken ct) =>
        (await DockerRecipes.ProjectContainersAsync(project, ct)).Select(c => c.Compose).FirstOrDefault(c => c is { FilesFound: true });

    private static string? WorkDir(ComposeInfo compose) => compose.WorkingDir is { } d && Directory.Exists(d) ? d : null;

    /// <summary>Bearbeitbare Datei eines Stacks (die erste Compose-Datei); Fehlertext, wenn sie nicht unter dem Basisordner liegt.</summary>
    private static async Task<(ComposeInfo? Compose, string? File, string? Error)> EditableFileAsync(string project, string root, CancellationToken ct)
    {
        if (await FindProjectAsync(project, ct) is not { } compose)
        {
            return (null, null, $"No compose file found on the host for stack {project}.");
        }

        var file = compose.ConfigFiles[0];
        var real = RealPath(file);
        if (!IsInside(root, file) || !IsInside(root, real))
        {
            return (compose, null, $"{file} is outside the folder this host allows for editing ({root}). To change it on the host: sudo aiopatcher-agent policy set composeRoot <folder>");
        }

        return (compose, real, null);
    }

    public static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static async Task<TaskOutcome> ReadComposeAsync(string project, string root, Action<string> log, CancellationToken ct)
    {
        var (_, file, error) = await EditableFileAsync(project, root, ct);
        if (error is not null || file is null)
        {
            log($"[agent] {error}");
            return TaskOutcome.Rejected(error ?? "No compose file.");
        }

        var info = new FileInfo(file);
        if (!info.Exists || info.Length > ContainerOps.MaxComposeBytes)
        {
            return TaskOutcome.Rejected($"{file} is missing or larger than {ContainerOps.MaxComposeBytes / 1024} KB.");
        }

        var content = await File.ReadAllTextAsync(file, ct);
        log($"[agent] Read {file} ({info.Length} bytes).");
        return new TaskOutcome(JobOutcome.Succeeded, $"Read {file}.",
            PullJsonContext.ToElement(new ComposeFileResult(project, file, content, Sha256(content)), PullJsonContext.Default.ComposeFileResult));
    }

    /// <summary>"compose … config --quiet" mit der neuen Datei an Stelle der alten (weitere Dateien bleiben dabei).</summary>
    public static List<string> ValidateArguments(string project, string? workDir, IReadOnlyList<string> files) =>
    [
        "-p", project,
        .. workDir is null ? Array.Empty<string>() : ["--project-directory", workDir],
        .. files.SelectMany(f => new[] { "-f", f }),
        "config", "--quiet",
    ];

    private static async Task<TaskOutcome> WriteComposeAsync(ContainerManageRequest request, string root, Action<string> log, CancellationToken ct)
    {
        var project = request.Project!;
        var (compose, file, error) = await EditableFileAsync(project, root, ct);
        if (error is not null || file is null || compose is null)
        {
            log($"[agent] {error}");
            return TaskOutcome.Rejected(error ?? "No compose file.");
        }

        var current = await File.ReadAllTextAsync(file, ct);
        if (request.ExpectedSha256 is { } expected && !string.Equals(Sha256(current), expected, StringComparison.OrdinalIgnoreCase))
        {
            return new TaskOutcome(JobOutcome.Failed, $"{file} was changed on the host after it was opened in the editor. Nothing was written; open it again.");
        }

        if (await DockerUpdater.FindComposeAsync(ct) is not { } command)
        {
            return TaskOutcome.Rejected("Neither \"docker compose\" nor \"docker-compose\" is installed.");
        }

        var dir = Path.GetDirectoryName(file)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(file)}.aiopatcher-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, request.Content!, ct);
            CopyOwnerAndMode(file, temp);
            var files = compose.ConfigFiles.Select((f, i) => i == 0 ? temp : f).ToList();
            if (!await ValidAsync(command, project, compose.WorkingDir ?? dir, files, log, ct))
            {
                return new TaskOutcome(JobOutcome.Failed, $"compose config rejected the new file (see log). {file} was not changed.");
            }

            var backup = $"{file}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(file, backup);
            CopyOwnerAndMode(file, backup);
            File.Move(temp, file, overwrite: true);
            log($"[agent] Saved {file}; previous version: {backup}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        if (request.Up && !await UpAsync(command, DockerUpdater.ProjectArguments(compose), WorkDir(compose), log, ct))
        {
            return new TaskOutcome(JobOutcome.Failed, $"{file} saved, but compose up failed (see log).");
        }

        return new TaskOutcome(JobOutcome.Succeeded, $"{file} saved{(request.Up ? " and stack started (up -d)" : "")}.");
    }

    private static async Task<TaskOutcome> CreateAsync(ContainerManageRequest request, string root, Action<string> log, CancellationToken ct)
    {
        var project = request.Project!;
        var dir = $"{Normalize(root).TrimEnd('/')}/{project}";
        if (!IsInside(root, dir) || !IsInside(root, RealPath(dir)))
        {
            return TaskOutcome.Rejected($"{dir} is outside {root}.");
        }

        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            return TaskOutcome.Rejected($"{dir} already exists and is not empty. Pick another name.");
        }

        if (await DockerUpdater.FindComposeAsync(ct) is not { } command)
        {
            return TaskOutcome.Rejected("Neither \"docker compose\" nor \"docker-compose\" is installed.");
        }

        var created = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        var file = $"{dir}/compose.yaml";
        var temp = $"{dir}/.compose.yaml.aiopatcher-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, request.Content!, ct);
            if (!await ValidAsync(command, project, dir, [temp], log, ct))
            {
                File.Delete(temp);
                if (created && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }

                return new TaskOutcome(JobOutcome.Failed, "compose config rejected the file (see log). Nothing was created.");
            }

            File.Move(temp, file);
            log($"[agent] Created {file}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        if (request.Up && !await UpAsync(command, ["-p", project, "--project-directory", dir, "-f", file], dir, log, ct))
        {
            return new TaskOutcome(JobOutcome.Failed, $"{file} created, but compose up failed (see log).");
        }

        return new TaskOutcome(JobOutcome.Succeeded, $"Stack {project} created in {dir}{(request.Up ? " and started" : "")}.");
    }

    private static async Task<bool> ValidAsync(string[] command, string project, string workDir, IReadOnlyList<string> files, Action<string> log, CancellationToken ct)
    {
        var args = ValidateArguments(project, workDir, files);
        log($"$ {string.Join(' ', command)} {string.Join(' ', args)}");
        return await ProcessRunner.RunStreamingAsync(command[0], [.. command.Skip(1), .. args], ShortTimeout, log, ct, Directory.Exists(workDir) ? workDir : null) == 0;
    }

    private static async Task<bool> UpAsync(string[] command, IReadOnlyList<string> projectArgs, string? workDir, Action<string> log, CancellationToken ct)
    {
        log($"$ {string.Join(' ', command)} {string.Join(' ', projectArgs)} up -d");
        return await ProcessRunner.RunStreamingAsync(command[0], [.. command.Skip(1), .. projectArgs, "up", "-d"], UpTimeout, log, ct, workDir) == 0;
    }

    /// <summary>Neue Datei bekommt Besitzer und Rechte der alten (der Agent schreibt als root mit Umask 077).</summary>
    private static void CopyOwnerAndMode(string from, string to)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
            var chown = ProcessRunner.RunAsync("chown", ["--reference", from, to], ShortTimeout, CancellationToken.None).GetAwaiter().GetResult();
            _ = chown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Rechte bleiben dann die des Agents
        }
    }

    private static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" +{names.Count - 5}" : "");
}
