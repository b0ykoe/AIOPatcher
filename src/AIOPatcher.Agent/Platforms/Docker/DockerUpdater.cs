using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Compose-Container aktualisieren: neues Image ziehen und nur diese Dienste neu erstellen
/// ("docker compose pull &lt;dienste&gt;", "up -d --no-deps &lt;dienste&gt;"), je Compose-Projekt gebündelt, danach prüfen, ob
/// sie wieder laufen. Gestoppte Container bekommen das neue Image, bleiben aber gestoppt ("up --no-start"). Selbst gebaute
/// Dienste (build: ohne Image aus einer Registry) werden stattdessen neu gebaut ("build --pull": aktuelles Basis-Image). Volumes,
/// Netzwerke und Einstellungen bleiben, die Compose-Datei wird nicht verändert. Der Server nennt nur Containernamen;
/// Projekt, Dateien und Ordner liest der Agent selbst aus den Labels der Container.
/// Nicht unterstützt (mit klarer Meldung): Container ohne Compose (docker run) und Portainer-Stacks, deren Compose-Datei
/// nur in Portainers Datenbereich liegt.
/// </summary>
public static partial class DockerUpdater
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);

    /// <summary>Höchstens so viele Container je Auftrag ("Alle aktualisieren").</summary>
    public const int MaxContainers = 100;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ContainerName();

    public static bool IsValidName(string name) => ContainerName().IsMatch(name);

    /// <summary>Compose v2 ("docker compose") oder v1 ("docker-compose"); null = keins installiert.</summary>
    public static async Task<string[]?> FindComposeAsync(CancellationToken ct)
    {
        try
        {
            if ((await ProcessRunner.RunAsync("docker", ["compose", "version"], ShortTimeout, ct)).Success)
            {
                return ["docker", "compose"];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return new[] { "/usr/local/bin/docker-compose", "/usr/bin/docker-compose" }.FirstOrDefault(File.Exists) is { } v1 ? [v1] : null;
    }

    /// <summary>Gemeinsame Argumente: Projekt, Projektordner, Compose-Dateien (wie beim ursprünglichen Start).</summary>
    public static List<string> ProjectArguments(ComposeInfo compose)
    {
        var args = new List<string> { "-p", compose.Project };
        if (compose.WorkingDir is { } dir)
        {
            args.AddRange(["--project-directory", dir]);
        }

        foreach (var file in compose.ConfigFiles)
        {
            args.AddRange(["-f", file]);
        }

        return args;
    }

    /// <summary>Warum sich dieser Container nicht per Compose aktualisieren lässt; null = es geht.</summary>
    public static string? NotUpdatable(ComposeInfo? compose) => compose switch
    {
        null => "Container was not started with Docker Compose (e.g. docker run). Update it with the tool that created it.",
        { FilesFound: false } when compose.ConfigFiles.Any(f => f.StartsWith("/data/compose/", StringComparison.Ordinal)) =>
            "Container belongs to a Portainer stack (compose file lives inside Portainer). Use \"Pull and redeploy\" in Portainer.",
        { FilesFound: false } => $"Compose file not found on the host: {string.Join(", ", compose.ConfigFiles.DefaultIfEmpty("(none recorded)"))}.",
        _ => null,
    };

    /// <summary>Ein Container eines Projekts: Dienst und ob er vorher lief (gestoppte werden neu angelegt, aber nicht gestartet).</summary>
    public sealed record UpdateTarget(string Name, string Service, bool Running, string? ImageId = null, string Image = "");

    public sealed record ProjectPlan(ComposeInfo Compose, IReadOnlyList<UpdateTarget> Targets);

    /// <summary>Gilt als laufend (nach dem Update wieder gestartet): running, restarting, paused.</summary>
    public static bool WasRunning(string? state) => state is "running" or "restarting" or "paused";

    /// <summary>
    /// Container nach Compose-Projekt bündeln (gleiches Projekt, gleicher Ordner, gleiche Dateien = ein Aufruf). Was sich nicht
    /// per Compose aktualisieren lässt, kommt mit Grund in Skipped.
    /// </summary>
    public static (List<ProjectPlan> Projects, List<(string Name, string Reason)> Skipped) Plan(
        IEnumerable<(string Name, ComposeInfo? Compose, string? State, string? ImageId, string Image)> containers)
    {
        var skipped = new List<(string Name, string Reason)>();
        var keys = new List<string>();
        var projects = new List<(ComposeInfo Compose, List<UpdateTarget> Targets)>();
        foreach (var (name, compose, state, imageId, image) in containers)
        {
            if (NotUpdatable(compose) is { } reason)
            {
                skipped.Add((name, reason));
                continue;
            }

            var key = $"{compose!.Project}|{compose.WorkingDir}|{string.Join(',', compose.ConfigFiles)}";
            var index = keys.IndexOf(key);
            if (index < 0)
            {
                keys.Add(key);
                projects.Add((compose, []));
                index = projects.Count - 1;
            }

            if (projects[index].Targets.All(t => t.Name != name))
            {
                projects[index].Targets.Add(new UpdateTarget(name, compose.Service, WasRunning(state), imageId, image));
            }
        }

        return (projects.Select(p => new ProjectPlan(p.Compose, p.Targets)).ToList(), skipped);
    }

    /// <summary>Dienste in Reihenfolge, ohne Doppelte (skalierte Dienste haben mehrere Container).</summary>
    public static List<string> Services(IEnumerable<UpdateTarget> targets) => targets.Select(t => t.Service).Distinct().ToList();

    public static Task<JobExecutionResult> UpdateAsync(string name, Action<string> log, CancellationToken ct) => UpdateManyAsync([name], log, ct);

    /// <summary>
    /// Container aktualisieren, je Compose-Projekt gebündelt: ein "pull" (bzw. "build --pull") für alle Dienste des Projekts,
    /// dann "up -d --no-deps" für die vorher laufenden und "up --no-start --no-deps" für gestoppte. Ein fehlgeschlagenes
    /// Projekt hält die übrigen nicht auf.
    /// </summary>
    public static async Task<JobExecutionResult> UpdateManyAsync(IReadOnlyList<string> names, Action<string> log, CancellationToken ct)
    {
        var single = names.Count == 1;
        var valid = names.Where(IsValidName).Distinct().Take(MaxContainers).ToList();
        if (valid.Count == 0)
        {
            return new JobExecutionResult(JobOutcome.Rejected, names.Count == 0 ? "No container given." : "Invalid container name.");
        }

        var failed = new List<(string Name, string Reason)>();
        var found = new List<(string Name, ContainerState State)>();
        foreach (var name in valid)
        {
            if (await InspectAsync(name, ct) is { } state)
            {
                found.Add((name, state));
            }
            else
            {
                failed.Add((name, $"Container {name} not found."));
            }
        }

        var (projects, skipped) = Plan(found.Select(f => (f.Name, f.State.Compose, (string?)f.State.Status, f.State.ImageId, f.State.Image)));
        foreach (var (name, reason) in skipped)
        {
            log($"[agent] {name}: skipped. {reason}");
        }

        var results = new List<TargetResult>();
        if (projects.Count > 0)
        {
            if (await FindComposeAsync(ct) is not { } command)
            {
                return new JobExecutionResult(JobOutcome.Rejected, "Neither \"docker compose\" nor \"docker-compose\" is installed.");
            }

            foreach (var project in projects)
            {
                // Projekte mit App-eigenem Update-Skript (z. B. mailcow) nicht einzeln ziehen: Versionen gehören dort zusammen
                if (await DockerRecipes.ForProjectAsync(project.Compose, ct) is { } recipe)
                {
                    foreach (var target in project.Targets)
                    {
                        var viaRecipe = $"{target.Name} belongs to {recipe.App}; update it with {recipe.Name} (update recipe \"{recipe.Id}\"), not by pulling single containers.";
                        log($"[agent] {viaRecipe}");
                        skipped.Add((target.Name, viaRecipe));
                    }

                    continue;
                }

                results.AddRange(await UpdateProjectAsync(command, project, log, ct));
            }
        }

        failed.AddRange(results.Where(r => r.Error is not null).Select(r => (r.Name, r.Error!)));
        var ok = results.Where(r => r.Error is null).ToList();

        if (single)
        {
            return ok is [var one] ? new JobExecutionResult(JobOutcome.Succeeded, one.Summary)
                : failed is [var f, ..] ? new JobExecutionResult(JobOutcome.Failed, f.Reason)
                : new JobExecutionResult(JobOutcome.Rejected, skipped is [var s, ..] ? s.Reason : "Nothing to update.");
        }

        var summary = Summarize(ok.Count(r => r.Changed), ok.Count(r => !r.Changed), skipped.Select(s => s.Name).ToList(), failed.Select(f => f.Name).ToList());
        log($"[agent] {summary}");
        var outcome = failed.Count > 0 ? JobOutcome.Failed : ok.Count == 0 ? JobOutcome.Rejected : JobOutcome.Succeeded;
        return new JobExecutionResult(outcome, summary);
    }

    /// <summary>"Containers: 3 updated, 1 already current, 1 skipped (a), 1 failed (b)."</summary>
    public static string Summarize(int updated, int current, IReadOnlyList<string> skipped, IReadOnlyList<string> failed)
    {
        var parts = new List<string>();
        if (updated > 0)
        {
            parts.Add($"{updated} updated");
        }

        if (current > 0)
        {
            parts.Add($"{current} already current");
        }

        if (skipped.Count > 0)
        {
            parts.Add($"{skipped.Count} skipped ({Names(skipped)})");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} failed ({Names(failed)})");
        }

        return $"Containers: {(parts.Count == 0 ? "nothing to do" : string.Join(", ", parts))}.";

        static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" +{names.Count - 5}" : "");
    }

    /// <param name="Error">null = erfolgreich (läuft wieder bzw. neu angelegt und gestoppt).</param>
    private sealed record TargetResult(string Name, bool Changed, string Summary, string? Error = null);

    private static async Task<List<TargetResult>> UpdateProjectAsync(string[] command, ProjectPlan project, Action<string> log, CancellationToken ct)
    {
        var compose = project.Compose;
        var targets = project.Targets;
        var baseArgs = command.Skip(1).Concat(ProjectArguments(compose)).ToList();
        var shown = $"{string.Join(' ', command)} {string.Join(' ', ProjectArguments(compose))}";
        var workDir = compose.WorkingDir is { } d && Directory.Exists(d) ? d : null;
        var label = targets.Count == 1 ? targets[0].Name : $"project {compose.Project}";
        foreach (var target in targets)
        {
            log($"[agent] {target.Name}: project {compose.Project}, service {target.Service}, image {target.Image}{(target.Running ? "" : " (stopped, stays stopped)")}");
        }

        List<TargetResult> AllFailed(string reason) => targets.Select(t => new TargetResult(t.Name, false, "", reason)).ToList();

        // Selbst gebaut (build:, Image in keiner Registry): neu bauen mit aktuellem Basis-Image statt ziehen
        var builds = await DockerBuild.ProjectAsync(compose, ct);
        var built = new HashSet<string>();
        foreach (var target in targets.Where(t => builds.ContainsKey(t.Service)))
        {
            if (await IsLocalImageAsync(target.ImageId, ct))
            {
                built.Add(target.Name);
            }
        }

        var buildServices = Services(targets.Where(t => built.Contains(t.Name)));
        var pullServices = Services(targets.Where(t => !built.Contains(t.Name))).Except(buildServices).ToList();
        if (buildServices.Count > 0)
        {
            log($"$ {shown} build --pull {string.Join(' ', buildServices)}");
            if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "build", "--pull", .. buildServices], PullTimeout, log, ct, workDir) != 0)
            {
                return AllFailed($"Building the image for {label} failed (see log). The container was not changed.");
            }
        }

        if (pullServices.Count > 0)
        {
            log($"$ {shown} pull {string.Join(' ', pullServices)}");
            if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "pull", .. pullServices], PullTimeout, log, ct, workDir) != 0)
            {
                return AllFailed($"Pulling the image for {label} failed (see log). The container was not changed.");
            }
        }

        var start = Services(targets.Where(t => t.Running));
        var create = Services(targets.Where(t => !t.Running)).Except(start).ToList();
        if (start.Count > 0)
        {
            log($"$ {shown} up -d --no-deps {string.Join(' ', start)}");
            if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "up", "-d", "--no-deps", .. start], UpTimeout, log, ct, workDir) != 0)
            {
                return AllFailed($"Recreating {label} failed (see log).");
            }
        }

        if (create.Count > 0)
        {
            log($"$ {shown} up --no-start --no-deps {string.Join(' ', create)}");
            if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "up", "--no-start", "--no-deps", .. create], UpTimeout, log, ct, workDir) != 0)
            {
                return AllFailed($"Recreating {label} failed (see log).");
            }
        }

        // Laufen die Dienste wieder? (Name bleibt bei Compose gleich; Healthcheck abwarten, falls vorhanden)
        var after = new Dictionary<string, ContainerState?>();
        var deadline = DateTime.UtcNow + StartWait;
        while (true)
        {
            foreach (var target in targets)
            {
                after[target.Name] = await InspectAsync(target.Name, ct);
            }

            var waiting = targets.Any(t => t.Running && after[t.Name] is { Status: "running", Health: "starting" } or { Status: "restarting" or "created" });
            if (!waiting || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        var results = new List<TargetResult>();
        foreach (var target in targets)
        {
            var state = after[target.Name];
            if (state is null || target.Running && (state.Status != "running" || state.Health == "unhealthy"))
            {
                if (state is not null)
                {
                    var logs = await ProcessRunner.RunAsync("docker", ["logs", "--tail", "20", target.Name], ShortTimeout, ct);
                    foreach (var line in (logs.StandardOutput + logs.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
                    {
                        log($"  | {line}");
                    }
                }

                var error = state is null
                    ? $"{target.Name} is missing after the update."
                    : $"{target.Name} is not running after the update (state {state.Status}{(state.Health is { } h ? $", health {h}" : "")}).";
                log($"[agent] {error}");
                results.Add(new TargetResult(target.Name, false, "", error));
                continue;
            }

            var summary = Describe(target, state, built.Contains(target.Name));
            log($"[agent] {summary}");
            results.Add(new TargetResult(target.Name, state.ImageId != target.ImageId, summary));
        }

        return results;
    }

    private static string Describe(UpdateTarget target, ContainerState after, bool rebuilt)
    {
        var tail = !target.Running ? " (left stopped)" : after.Health == "starting" ? " (health check still starting)" : "";
        var running = target.Running ? " and running" : "";
        if (after.ImageId != target.ImageId)
        {
            return rebuilt
                ? $"{target.Name} rebuilt on the current base image{running}{tail}."
                : $"{target.Name} updated to the current image of {after.Image}{running}{tail}.";
        }

        var isRunning = target.Running ? ", container is running" : "";
        return rebuilt
            ? $"{target.Name}: rebuild produced the same image{isRunning}{tail}."
            : $"{target.Name}: image was already current{isRunning}{tail}.";
    }

    /// <summary>Image stammt aus keiner Registry (keine RepoDigests), also selbst gebaut.</summary>
    private static async Task<bool> IsLocalImageAsync(string? imageId, CancellationToken ct)
    {
        if (imageId is null)
        {
            return false;
        }

        var digests = await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{json .RepoDigests}}", imageId], ShortTimeout, ct);
        return digests.Success && DockerInventory.ParseImageDigests($"x|{digests.StandardOutput.Trim()}").GetValueOrDefault("x") is { Count: 0 };
    }

    private sealed record ContainerState(string Image, string? ImageId, string Status, string? Health, ComposeInfo? Compose);

    private static async Task<ContainerState?> InspectAsync(string name, CancellationToken ct)
    {
        return await DockerInventory.InspectAsync([name], ShortTimeout, ct) is [var c]
            ? new ContainerState(c.Image, c.ImageId, c.State ?? "", c.Health, c.Compose)
            : null;
    }
}
