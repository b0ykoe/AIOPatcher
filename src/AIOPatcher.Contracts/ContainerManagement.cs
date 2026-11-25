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

    /// <summary>Nur lesen: was "Platz freigeben" löschen könnte (ungenutzte Images, Volumes, Build-Cache), für die Auswahl im Dialog.</summary>
    public const string Scan = "scan";

    /// <summary>
    /// Ausgewählte Einträge aus dem Scan löschen (Images, Volumes, Build-Cache). Der Agent scannt dabei neu und löscht nur, was
    /// dann noch ungenutzt ist; nie mehr, als im Dialog stand.
    /// </summary>
    public const string Clean = "clean";

    public static readonly IReadOnlyList<string> All = [Start, Stop, Restart, Remove, Down, Prune, ReadCompose, WriteCompose, Create, Scan, Clean];

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

    /// <summary>Höchstens so viele Images bzw. Volumes je Scan und je Löschauftrag.</summary>
    public const int MaxPruneItems = 300;

    /// <summary>Image-ID ("sha256:…" mit 64 Hex-Zeichen oder gekürzt ab 12).</summary>
    [GeneratedRegex(@"^(sha256:[a-f0-9]{64}|[a-f0-9]{12,64})$")]
    private static partial Regex ImageIdPattern();

    /// <summary>Volume-Namen wie Docker sie erlaubt.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$")]
    private static partial Regex VolumeNamePattern();

    public static bool IsValidImageId(string? id) => id is not null && ImageIdPattern().IsMatch(id);

    public static bool IsValidVolumeName(string? name) => name is not null && VolumeNamePattern().IsMatch(name);

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

        if (request.Op == Clean)
        {
            var images = request.Images ?? [];
            var volumes = request.VolumeNames ?? [];
            if (images.Count == 0 && volumes.Count == 0 && !request.BuildCache)
            {
                return "Nothing selected.";
            }

            if (images.Count > MaxPruneItems || volumes.Count > MaxPruneItems)
            {
                return $"Select at most {MaxPruneItems} images and {MaxPruneItems} volumes at a time.";
            }

            if (images.FirstOrDefault(i => !IsValidImageId(i)) is { } badImage)
            {
                return $"Invalid image ID \"{badImage}\".";
            }

            if (volumes.FirstOrDefault(v => !IsValidVolumeName(v)) is { } badVolume)
            {
                return $"Invalid volume name \"{badVolume}\".";
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
/// <param name="Images">clean: Image-IDs aus dem Scan.</param>
/// <param name="VolumeNames">clean: Volume-Namen aus dem Scan (Daten weg).</param>
/// <param name="BuildCache">clean: Build-Cache leeren.</param>
public sealed record ContainerManageRequest(string Op, IReadOnlyList<string>? Containers = null, string? Project = null, bool Volumes = false,
    string? Content = null, string? ExpectedSha256 = null, bool Up = false,
    IReadOnlyList<string>? Images = null, IReadOnlyList<string>? VolumeNames = null, bool BuildCache = false);

/// <summary>Antwort auf "read": Inhalt der Compose-Datei eines Stacks.</summary>
public sealed record ComposeFileResult(string Project, string Path, string Content, string Sha256);

/// <summary>Zeile aus "docker system df": Typ (Images, Containers, Local Volumes, Build Cache), Größe und was frei würde (Bytes).</summary>
public sealed record DockerDiskUsage(string Type, long Size, long Reclaimable, int Count);

/// <summary>Antwort auf "scan": was "Platz freigeben" löschen könnte, je Eintrag einzeln auswählbar.</summary>
/// <param name="VolumeSizesKnown">Docker hat Volume-Größen geliefert (docker system df -v); sonst ist Size null.</param>
public sealed record PruneScan(IReadOnlyList<UnusedImage> Images, IReadOnlyList<UnusedVolume> Volumes, long BuildCacheSize, int BuildCacheCount,
    bool VolumeSizesKnown = false);

/// <summary>Image, das kein Container nutzt (auch kein gestoppter).</summary>
/// <param name="References">"repo:tag" (mehrere, wenn das Image mehrere Tags hat); leer bei Images ohne Namen.</param>
/// <param name="Size">Größe laut docker image ls (inklusive geteilter Schichten, frei wird ggf. weniger).</param>
/// <param name="Created">Wann gebaut, wie Docker es meldet (z. B. "2024-01-02 10:00:00 +0100 CET").</param>
public sealed record UnusedImage(string Id, IReadOnlyList<string> References, long Size, string? Created);

/// <summary>Volume, das kein Container nutzt (auch kein gestoppter).</summary>
/// <param name="Project">Compose-Projekt laut Label com.docker.compose.project, damit man Daten gestoppter Stacks erkennt.</param>
public sealed record UnusedVolume(string Name, long? Size, string? Project, string? Driver);

/// <summary>Inventur: bearbeitbare Compose-Datei eines Stacks mit SHA-256 (Inhalt kommt nur auf Anfrage).</summary>
public sealed record ComposeFileHash(string Project, string Path, string Sha256, long Size);

/// <summary>docker.composecache: Inhalt dieser Stacks schicken (höchstens <see cref="MaxFiles"/>).</summary>
public sealed record ComposeCacheRequest(IReadOnlyList<string> Projects)
{
    public const int MaxFiles = 20;
}

public sealed record ComposeFileBatch(IReadOnlyList<ComposeFileResult> Files);
