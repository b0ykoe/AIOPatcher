using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Update-Rezepte (<see cref="UpdateRecipeCatalog"/>): erkennen, zu welchem Compose-Projekt ein App-eigenes Update-Skript
/// gehört (z. B. mailcow update.sh), und es ausführen. Der Server nennt nur Rezept-Id und Containername; Ordner, Befehl und
/// Erkennung bestimmt der Agent selbst aus den Container-Labels und dem eingebetteten Rezept. Hat ein Projekt ein Rezept,
/// lehnt <see cref="DockerUpdater"/> das generische pull/up für seine Container ab (das Rezept hat Vorrang).
/// </summary>
public static class DockerRecipes
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Rezept für ein Compose-Projekt: alle Erkennungsdateien im Projektordner und ein passendes Image.</summary>
    public static UpdateRecipe? Match(string? workingDir, IEnumerable<string> images, Func<string, bool> fileExists, IReadOnlyList<UpdateRecipe>? catalog = null)
    {
        if (string.IsNullOrEmpty(workingDir) || !workingDir.StartsWith('/'))
        {
            return null;
        }

        var imageList = images.ToList();
        return (catalog ?? UpdateRecipeCatalog.All).FirstOrDefault(r =>
            r.Match.Files.All(f => fileExists(Combine(workingDir, f))) && imageList.Any(r.Match.MatchesImage));
    }

    /// <summary>Inventar: jedem Container eines Projekts mit Rezept die Rezept-Id (und ob die Policy es erlaubt) mitgeben.</summary>
    public static List<ContainerReport> WithRecipes(IReadOnlyList<ContainerReport> containers, bool allowed, Func<string, bool> fileExists,
        IReadOnlyList<UpdateRecipe>? catalog = null)
    {
        var recipes = containers
            .Where(c => c.Compose is { FilesFound: true, WorkingDir: not null })
            .GroupBy(c => (c.Compose!.Project, c.Compose.WorkingDir))
            .Select(g => (g.Key, Recipe: Match(g.Key.WorkingDir, g.Select(c => c.Image), fileExists, catalog)))
            .Where(x => x.Recipe is not null)
            .ToDictionary(x => x.Key, x => x.Recipe!);

        return containers
            .Select(c => c.Compose is { } compose && recipes.TryGetValue((compose.Project, compose.WorkingDir), out var recipe)
                ? c with { Recipe = new RecipeRef(recipe.Id, allowed) }
                : c)
            .ToList();
    }

    /// <summary>Policy für Rezepte: eigener Schalter plus die Regeln für Container-Updates.</summary>
    public static string? PolicyRejects(AgentPolicy policy) =>
        DockerModule.PolicyRejects(policy)
        ?? (policy.AllowUpdateRecipes ? null
            : "Local policy forbids app update scripts (update recipes). To allow it on this host: aiopatcher-agent policy set allowUpdateRecipes true");

    /// <summary>Rezept, das für das Projekt dieses Containers gilt (für den Vorrang vor pull/up); null = keins.</summary>
    public static async Task<UpdateRecipe?> ForProjectAsync(ComposeInfo compose, CancellationToken ct)
    {
        if (!compose.FilesFound || compose.WorkingDir is null)
        {
            return null;
        }

        var project = await ProjectContainersAsync(compose.Project, ct);
        return Match(compose.WorkingDir, project.Select(c => c.Image), File.Exists);
    }

    public static async Task<JobExecutionResult> RunAsync(RecipeUpdateRequest request, Action<string> log, CancellationToken ct)
    {
        if (!DockerUpdater.IsValidName(request.Container))
        {
            return new JobExecutionResult(JobOutcome.Rejected, "Invalid container name.");
        }

        if (UpdateRecipeCatalog.Find(request.Recipe) is not { } recipe)
        {
            return new JobExecutionResult(JobOutcome.Rejected, $"This agent does not know the update recipe \"{request.Recipe}\" (agent too old?).");
        }

        var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", DockerInventory.DetailedFormat, request.Container], ShortTimeout, ct);
        if (!inspect.Success || DockerInventory.ParseDetailed(inspect.StandardOutput, File.Exists) is not [{ Compose: { } compose }])
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Container {request.Container} not found or not started with Docker Compose.");
        }

        // Rezept selbst neu erkennen: der Server darf kein Rezept auf einen fremden Ordner ansetzen
        var before = await ProjectContainersAsync(compose.Project, ct);
        var workingDir = compose.WorkingDir;
        if (!compose.FilesFound || workingDir is null || Match(workingDir, before.Select(c => c.Image), File.Exists, [recipe]) is null)
        {
            return new JobExecutionResult(JobOutcome.Rejected, $"The compose project {compose.Project} does not match the recipe \"{recipe.Id}\" ({recipe.App}).");
        }

        var script = Combine(workingDir, recipe.Command[0]);
        if (ScriptProblem(script) is { } problem)
        {
            log($"[agent] {problem}");
            return new JobExecutionResult(JobOutcome.Rejected, problem);
        }

        var wasRunning = before.Where(c => c.State == "running").Select(c => c.Name).ToList();
        log($"[agent] {recipe.App}: project {compose.Project} in {workingDir}, {before.Count} containers ({wasRunning.Count} running).");
        log($"$ cd {workingDir} && {string.Join(' ', recipe.Command)}");

        string? failure = null;
        int code;
        try
        {
            code = await ProcessRunner.RunStreamingAsync(script, recipe.Command.Skip(1), TimeSpan.FromMinutes(recipe.TimeoutMinutes), line =>
            {
                var clean = ServerUpdateModule.StripAnsi(line);
                failure ??= recipe.FailureIn(clean) is not null ? clean.Trim() : null;
                log(clean);
            }, ct, workingDir);
        }
        catch (TimeoutException e)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"{recipe.Name}: {e.Message}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"{recipe.Name} could not be started: {e.Message}");
        }

        if (!recipe.IsSuccessExitCode(code))
        {
            return new JobExecutionResult(JobOutcome.Failed, $"{recipe.Name} failed (exit code {code}); see the log.");
        }

        if (failure is not null)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"{recipe.Name} reported an error: {failure}");
        }

        if (recipe.Verify is { ProjectRunning: false })
        {
            log($"[agent] {recipe.Name} finished.");
            return new JobExecutionResult(JobOutcome.Succeeded, $"{recipe.App} updated with {recipe.Name}.");
        }

        // Exit 0 beweist bei vielen Skripten nichts: laufen alle Container wieder, die vorher liefen?
        log($"[agent] Waiting for {wasRunning.Count} containers of {compose.Project} to run again …");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds((recipe.Verify ?? new RecipeVerify()).WaitSeconds);
        ProjectCheck check;
        while (true)
        {
            check = CheckProject(wasRunning, await ProjectContainersAsync(compose.Project, ct));
            if (check.Problems.Count == 0 && check.Starting.Count == 0 || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        if (check.Problems.Count > 0)
        {
            foreach (var name in check.Problems.Select(p => p.Name).Where(DockerUpdater.IsValidName).Take(3))
            {
                var logs = await ProcessRunner.RunAsync("docker", ["logs", "--tail", "15", name], ShortTimeout, ct);
                foreach (var line in (logs.StandardOutput + logs.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(15))
                {
                    log($"  {name} | {line}");
                }
            }

            return new JobExecutionResult(JobOutcome.Failed,
                $"{recipe.Name} finished, but not all containers run again: {string.Join(", ", check.Problems.Take(5).Select(p => $"{p.Name} ({p.State})"))}.");
        }

        var summary = $"{recipe.App} updated with {recipe.Name}, {wasRunning.Count} containers running"
                      + (check.Starting.Count > 0 ? $" ({check.Starting.Count} health checks still starting)." : ".");
        log($"[agent] {summary}");
        return new JobExecutionResult(JobOutcome.Succeeded, summary);
    }

    public sealed record ProjectCheck(IReadOnlyList<(string Name, string State)> Problems, IReadOnlyList<string> Starting);

    /// <summary>Vorher laufende Container: fehlen, gestoppt oder unhealthy = Problem; Healthcheck "starting" = noch abwarten.</summary>
    public static ProjectCheck CheckProject(IReadOnlyList<string> wasRunning, IReadOnlyList<ContainerReport> now)
    {
        var byName = now.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        var problems = new List<(string, string)>();
        var starting = new List<string>();
        foreach (var name in wasRunning)
        {
            if (!byName.TryGetValue(name, out var c))
            {
                problems.Add((name, "missing"));
            }
            else if (c.State != "running")
            {
                problems.Add((name, c.State ?? "unknown"));
            }
            else if (c.Health == "unhealthy")
            {
                problems.Add((name, "unhealthy"));
            }
            else if (c.Health == "starting")
            {
                starting.Add(name);
            }
        }

        return new ProjectCheck(problems, starting);
    }

    /// <summary>Skript vorhanden und nicht von jedem beschreibbar (sonst könnte ein beliebiger Benutzer root-Befehle einschleusen).</summary>
    public static string? ScriptProblem(string path)
    {
        if (!File.Exists(path))
        {
            return $"{path} not found.";
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) != 0)
            {
                return $"{path} is writable by group or others; refusing to run it as root. Fix with: chmod go-w {path}";
            }

            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                return $"{path} is not executable.";
            }
        }

        return null;
    }

    /// <summary>Alle Container (auch gestoppte) eines Compose-Projekts.</summary>
    public static async Task<List<ContainerReport>> ProjectContainersAsync(string project, CancellationToken ct)
    {
        var ps = await ProcessRunner.RunAsync("docker", ["ps", "-a", "-q", "--no-trunc", "--filter", $"label=com.docker.compose.project={project}"], ShortTimeout, ct);
        var ids = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!ps.Success || ids.Length == 0)
        {
            return [];
        }

        var inspect = await ProcessRunner.RunAsync("docker", ["inspect", "--format", DockerInventory.DetailedFormat, .. ids], ShortTimeout, ct);
        return DockerInventory.ParseDetailed(inspect.StandardOutput, File.Exists);
    }

    private static string Combine(string dir, string relative) =>
        $"{dir.TrimEnd('/')}/{(relative.StartsWith("./", StringComparison.Ordinal) ? relative[2..] : relative)}";
}
