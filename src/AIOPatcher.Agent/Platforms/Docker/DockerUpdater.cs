using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Einen Compose-Container aktualisieren: neues Image ziehen und nur diesen Dienst neu erstellen
/// ("docker compose pull &lt;dienst&gt;", "up -d --no-deps &lt;dienst&gt;"), danach prüfen, ob er wieder läuft. Volumes,
/// Netzwerke und Einstellungen bleiben, die Compose-Datei wird nicht verändert. Der Server nennt nur den Containernamen;
/// Projekt, Dateien und Ordner liest der Agent selbst aus den Labels des Containers.
/// Nicht unterstützt (mit klarer Meldung): Container ohne Compose (docker run) und Portainer-Stacks, deren Compose-Datei
/// nur in Portainers Datenbereich liegt.
/// </summary>
public static partial class DockerUpdater
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);

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

    public static async Task<JobExecutionResult> UpdateAsync(string name, Action<string> log, CancellationToken ct)
    {
        if (!IsValidName(name))
        {
            return new JobExecutionResult(JobOutcome.Rejected, "Invalid container name.");
        }

        var before = await InspectAsync(name, ct);
        if (before is null)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Container {name} not found.");
        }

        if (NotUpdatable(before.Compose) is { } reason)
        {
            log($"[agent] {reason}");
            return new JobExecutionResult(JobOutcome.Rejected, reason);
        }

        var compose = before.Compose!;
        if (await FindComposeAsync(ct) is not { } command)
        {
            return new JobExecutionResult(JobOutcome.Rejected, "Neither \"docker compose\" nor \"docker-compose\" is installed.");
        }

        var baseArgs = command.Skip(1).Concat(ProjectArguments(compose)).ToList();
        var workDir = compose.WorkingDir is { } d && Directory.Exists(d) ? d : null;
        log($"[agent] {name}: project {compose.Project}, service {compose.Service}, image {before.Image}");

        log($"$ {string.Join(' ', command)} {string.Join(' ', ProjectArguments(compose))} pull {compose.Service}");
        if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "pull", compose.Service], PullTimeout, log, ct, workDir) != 0)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Pulling the image for {name} failed (see log). The container was not changed.");
        }

        log($"$ {string.Join(' ', command)} {string.Join(' ', ProjectArguments(compose))} up -d --no-deps {compose.Service}");
        if (await ProcessRunner.RunStreamingAsync(command[0], [.. baseArgs, "up", "-d", "--no-deps", compose.Service], UpTimeout, log, ct, workDir) != 0)
        {
            return new JobExecutionResult(JobOutcome.Failed, $"Recreating {name} failed (see log).");
        }

        // Läuft der Dienst wieder? (Name bleibt bei Compose gleich; Healthcheck abwarten, falls vorhanden)
        var deadline = DateTime.UtcNow + StartWait;
        ContainerState? after = null;
        while (DateTime.UtcNow < deadline)
        {
            after = await InspectAsync(name, ct);
            if (after is { Status: "running" } && after.Health is null or "healthy")
            {
                break;
            }

            if (after is { Status: "exited" or "dead" } || after?.Health == "unhealthy")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        if (after is null || after.Status != "running" || after.Health == "unhealthy")
        {
            var logs = await ProcessRunner.RunAsync("docker", ["logs", "--tail", "20", name], ShortTimeout, ct);
            foreach (var line in (logs.StandardOutput + logs.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
            {
                log($"  | {line}");
            }

            return new JobExecutionResult(JobOutcome.Failed,
                $"{name} is not running after the update (state {after?.Status ?? "missing"}{(after?.Health is { } h ? $", health {h}" : "")}).");
        }

        var changed = after.ImageId != before.ImageId;
        var summary = changed
            ? $"{name} updated to the current image of {after.Image} and running{(after.Health == "starting" ? " (health check still starting)" : "")}."
            : $"{name}: image was already current, container is running.";
        log($"[agent] {summary}");
        return new JobExecutionResult(JobOutcome.Succeeded, summary);
    }

    private sealed record ContainerState(string Image, string? ImageId, string Status, string? Health, ComposeInfo? Compose);

    private static async Task<ContainerState?> InspectAsync(string name, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("docker",
            ["inspect", "--format", "{{.Name}}|{{.Config.Image}}|{{.Image}}|{{.State.Status}}|{{json .Config.Labels}}", name], ShortTimeout, ct);
        var health = await ProcessRunner.RunAsync("docker",
            ["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{end}}", name], ShortTimeout, ct);
        if (!result.Success || DockerInventory.ParseContainers(result.StandardOutput, File.Exists) is not [var c])
        {
            return null;
        }

        var h = health.StandardOutput.Trim();
        return new ContainerState(c.Image, c.ImageId, c.State ?? "", h.Length == 0 ? null : h, c.Compose);
    }
}
