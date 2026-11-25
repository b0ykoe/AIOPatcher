using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Compose-Editor ohne Wartezeit: Die Container-Inventur meldet je bearbeitbarer Compose-Datei SHA-256 und Größe (nur mit
/// allowContainerManagement und allowComposeEdit, nur Dateien unter composeRoot). Den Inhalt schickt der Agent nur, wenn der
/// Server ihn für einen neuen Hash anfordert (docker.composecache); der Server hält ihn verschlüsselt vor.
/// </summary>
public static partial class DockerManager
{
    /// <summary>Höchstens so viele Stacks meldet die Inventur mit Hash.</summary>
    public const int MaxHashedProjects = 50;

    /// <summary>Compose-Datei, die der Editor bearbeiten darf (erste Datei des Projekts, echter Pfad unter dem Basisordner); sonst null.</summary>
    public static string? EditablePath(ComposeInfo compose, string root, Func<string, string>? realPath = null)
    {
        if (!compose.FilesFound || compose.ConfigFiles.Count == 0)
        {
            return null;
        }

        var file = compose.ConfigFiles[0];
        var real = (realPath ?? RealPath)(file);
        return IsInside(root, file) && IsInside(root, real) ? real : null;
    }

    /// <summary>Darf dieser Host Compose-Dateien bearbeiten (beide Policy-Werte)?</summary>
    public static bool ComposeEditAllowed(AgentPolicy policy) => policy.AllowContainerManagement && policy.AllowComposeEdit;

    /// <summary>Hashes für die Inventur; null, wenn die Policy es nicht erlaubt (dann löscht der Server seinen Vorrat).</summary>
    public static async Task<IReadOnlyList<ComposeFileHash>?> ComposeHashesAsync(IReadOnlyList<ContainerReport> containers, AgentPolicy policy, CancellationToken ct)
    {
        if (!ComposeEditAllowed(policy))
        {
            return null;
        }

        var result = new List<ComposeFileHash>();
        foreach (var compose in containers.Select(c => c.Compose).OfType<ComposeInfo>().DistinctBy(c => c.Project).Take(MaxHashedProjects))
        {
            try
            {
                if (EditablePath(compose, policy.EffectiveComposeRoot) is not { } path || new FileInfo(path) is not { Exists: true } info
                    || info.Length > ContainerOps.MaxComposeBytes)
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(path, ct);
                result.Add(new ComposeFileHash(compose.Project, path, Sha256(content), info.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Datei nicht lesbar: ohne Hash, der Editor liest dann wie bisher per Job
            }
        }

        return result;
    }

    /// <summary>docker.composecache: Inhalte für den Vorrat des Servers (gleiche Prüfungen wie beim Lesen im Editor).</summary>
    public static async Task<TaskOutcome> CacheAsync(ComposeCacheRequest request, AgentPolicy policy, CancellationToken ct)
    {
        if (!ComposeEditAllowed(policy))
        {
            return TaskOutcome.Rejected($"Local policy forbids editing compose files. To allow it on this host: {PolicyHint} and {ComposePolicyHint}");
        }

        var files = new List<ComposeFileResult>();
        foreach (var project in request.Projects.Where(ContainerOps.IsValidProjectName).Distinct().Take(ComposeCacheRequest.MaxFiles))
        {
            var (_, path, error) = await EditableFileAsync(project, policy.EffectiveComposeRoot, ct);
            if (error is not null || path is null || new FileInfo(path) is not { Exists: true } info || info.Length > ContainerOps.MaxComposeBytes)
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(path, ct);
            files.Add(new ComposeFileResult(project, path, content, Sha256(content)));
        }

        return new TaskOutcome(JobOutcome.Succeeded, $"{files.Count} compose files",
            PullJsonContext.ToElement(new ComposeFileBatch(files), PullJsonContext.Default.ComposeFileBatch));
    }
}
