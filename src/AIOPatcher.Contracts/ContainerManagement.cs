using System.Text.RegularExpressions;

namespace AIOPatcher.Contracts;

/// <summary>
/// Mini-Docker-Manager (docker.manage): Container starten, stoppen, löschen, Platz freigeben, Compose-Dateien bearbeiten
/// und neue Stacks anlegen. Hochriskant: wer Compose-Dateien schreiben darf, ist root auf dem Host. Darum dreifach gesperrt:
/// Funktionsschalter im Dashboard, policy.json "allowContainerManagement" und für Compose zusätzlich "allowComposeEdit";
/// beide Policy-Werte lassen sich nur auf dem Host selbst setzen (aiopatcher-agent policy set), nie vom Server.
/// </summary>
public static partial class ContainerOps
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    /// <summary>Container stoppen und entfernen (benannte Volumes bleiben; mit Volumes: auch anonyme Volumes).</summary>
    public const string Remove = "remove";

    /// <summary>Stack: "compose down" (mit Volumes: "down --volumes").</summary>
    public const string Down = "down";

    /// <summary>Ungenutzte Images und Build-Cache löschen (mit Volumes: auch ungenutzte Volumes).</summary>
    public const string Prune = "prune";

    /// <summary>Compose-Datei eines Stacks lesen (für den Editor).</summary>
    public const string ReadCompose = "read";

    /// <summary>Compose-Datei eines Stacks ersetzen (geprüft mit "compose config", Sicherung der alten Datei).</summary>
    public const string WriteCompose = "write";

    /// <summary>Neuen Stack im Basisordner anlegen.</summary>
    public const string Create = "create";

    public static readonly IReadOnlyList<string> All = [Start, Stop, Restart, Remove, Down, Prune, ReadCompose, WriteCompose, Create];

    /// <summary>Braucht zusätzlich allowComposeEdit (Compose-Datei lesen oder schreiben, Stack anlegen).</summary>
    public static bool NeedsComposeEdit(string op) => op is ReadCompose or WriteCompose or Create;

    /// <summary>Wirkt auf einzelne Container (Namen in <see cref="ContainerManageRequest.Containers"/>).</summary>
    public static bool OnContainers(string op) => op is Start or Stop or Restart or Remove;

    /// <summary>Wirkt auf ein Compose-Projekt (<see cref="ContainerManageRequest.Project"/>).</summary>
    public static bool OnProject(string op) => op is Down or ReadCompose or WriteCompose or Create;

    /// <summary>Größe einer Compose-Datei, die der Editor liest und schreibt.</summary>
    public const int MaxComposeBytes = 60 * 1024;

    /// <summary>Höchstens so viele Container je Auftrag.</summary>
    public const int MaxContainers = 100;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ContainerNamePattern();

    /// <summary>Compose-Projektnamen wie Docker sie erlaubt (klein, Ziffern, "-" und "_"), zugleich Ordnername neuer Stacks.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}$")]
    private static partial Regex ProjectNamePattern();

    public static bool IsValidContainerName(string? name) => name is not null && ContainerNamePattern().IsMatch(name);

    public static bool IsValidProjectName(string? name) => name is not null && ProjectNamePattern().IsMatch(name);

    /// <summary>Warum der Auftrag so nicht geht; null = in Ordnung. Prüfen Server (vor dem Job) und Agent (vor dem Ausführen).</summary>
    public static string? Invalid(ContainerManageRequest request)
    {
        if (!All.Contains(request.Op))
        {
            return $"Unknown operation \"{request.Op}\".";
        }

        if (OnContainers(request.Op))
        {
            var names = request.Containers ?? [];
            if (names.Count == 0 || names.Count > MaxContainers)
            {
                return $"Give 1 to {MaxContainers} containers.";
            }

            if (names.FirstOrDefault(n => !IsValidContainerName(n)) is { } bad)
            {
                return $"Invalid container name \"{bad}\".";
            }
        }

        if (OnProject(request.Op) && !IsValidProjectName(request.Project))
        {
            return "Invalid stack name (lowercase letters, digits, \"-\" and \"_\", up to 63 characters).";
        }

        if (request.Op is WriteCompose or Create)
        {
            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return "The compose file is empty.";
            }

            if (System.Text.Encoding.UTF8.GetByteCount(request.Content) > MaxComposeBytes)
            {
                return $"The compose file is larger than {MaxComposeBytes / 1024} KB.";
            }
        }

        return null;
    }
}

/// <param name="Op">Eine der <see cref="ContainerOps"/>.</param>
/// <param name="Containers">Containernamen für start/stop/restart/remove.</param>
/// <param name="Project">Compose-Projekt für down/read/write/create.</param>
/// <param name="Volumes">remove/down/prune: auch Volumes löschen (Daten weg). Ab Werk aus.</param>
/// <param name="Content">write/create: neue Compose-Datei. Der Server entfernt sie aus dem Job, sobald der Agent sie hat.</param>
/// <param name="ExpectedSha256">write: SHA-256 der Datei beim Lesen; hat sie sich inzwischen geändert, schreibt der Agent nicht.</param>
/// <param name="Up">write/create: danach "compose up -d".</param>
public sealed record ContainerManageRequest(string Op, IReadOnlyList<string>? Containers = null, string? Project = null, bool Volumes = false,
    string? Content = null, string? ExpectedSha256 = null, bool Up = false);

/// <summary>Antwort auf "read": Inhalt der Compose-Datei eines Stacks.</summary>
public sealed record ComposeFileResult(string Project, string Path, string Content, string Sha256);

/// <summary>Zeile aus "docker system df": Typ (Images, Containers, Local Volumes, Build Cache), Größe und was frei würde (Bytes).</summary>
public sealed record DockerDiskUsage(string Type, long Size, long Reclaimable, int Count);
