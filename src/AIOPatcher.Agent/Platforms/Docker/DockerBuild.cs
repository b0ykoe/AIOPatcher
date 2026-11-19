using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Selbst gebaute Compose-Dienste (build:) erkennen: "docker compose config" nennt Kontext und Dockerfile, die
/// FROM-Zeilen das Basis-Image. Nur lesen. So kann der Server das Basis-Image gegen die Registry prüfen, und
/// "Aktualisieren" baut neu ("compose build --pull") statt ein Image zu ziehen, das es in keiner Registry gibt.
/// </summary>
public static partial class DockerBuild
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxDockerfileBytes = 256 * 1024;

    /// <summary>Build-Angaben je Dienst eines Compose-Projekts; leer, wenn "compose config" nicht geht (z. B. docker-compose v1).</summary>
    public static async Task<Dictionary<string, BuildInfo>> ProjectAsync(ComposeInfo compose, CancellationToken ct)
    {
        if (!compose.FilesFound || await DockerUpdater.FindComposeAsync(ct) is not { Length: 2 } command)
        {
            return [];
        }

        var workDir = compose.WorkingDir is { } d && Directory.Exists(d) ? d : null;
        var config = await ProcessRunner.RunAsync(command[0], [.. command.Skip(1), .. DockerUpdater.ProjectArguments(compose), "config", "--format", "json"],
            Timeout, ct, workingDirectory: workDir);
        return config.Success ? ParseConfig(config.StandardOutput, File.Exists, ReadDockerfile) : [];
    }

    /// <summary>Dienste mit build: aus "docker compose config --format json" (Pfade dort schon absolut).</summary>
    public static Dictionary<string, BuildInfo> ParseConfig(string json, Func<string, bool> fileExists, Func<string, string?> readFile)
    {
        var result = new Dictionary<string, BuildInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var service in services.EnumerateObject())
            {
                if (!service.Value.TryGetProperty("build", out var build))
                {
                    continue;
                }

                string? context = null, dockerfile = null, inline = null;
                var args = new Dictionary<string, string>();
                if (build.ValueKind == JsonValueKind.String)
                {
                    context = build.GetString();
                }
                else if (build.ValueKind == JsonValueKind.Object)
                {
                    context = build.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    dockerfile = build.TryGetProperty("dockerfile", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    inline = build.TryGetProperty("dockerfile_inline", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    if (build.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var arg in a.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String))
                        {
                            args[arg.Name] = arg.Value.GetString()!;
                        }
                    }
                }

                if (string.IsNullOrEmpty(context))
                {
                    continue;
                }

                // Kontext kann auch eine Git-URL sein: dann kennen wir das Dockerfile nicht
                string? path = null;
                if (inline is null && !context.Contains("://", StringComparison.Ordinal) && !context.StartsWith("git@", StringComparison.Ordinal))
                {
                    var name = dockerfile ?? "Dockerfile";
                    path = name.StartsWith('/') ? name : $"{context.TrimEnd('/')}/{name}";
                }

                var text = inline ?? (path is not null && fileExists(path) ? readFile(path) : null);
                var (bases, final) = text is null ? ([], null) : ParseFrom(text, args);
                result[service.Name] = new BuildInfo(context, inline is not null ? "(inline)" : path, bases, final, null);
            }
        }
        catch (JsonException)
        {
            // kaputte Ausgabe: wie ohne Build-Angaben
        }

        return result;
    }

    private static string? ReadDockerfile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length > MaxDockerfileBytes ? null : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ${NAME}, ${NAME:-default}, ${NAME-default}, $NAME
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?-)(?<default>[^}]*))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Variable();

    /// <summary>
    /// FROM-Zeilen eines Dockerfiles: alle Basis-Images aus Registries (ohne Stufen-Namen und "scratch") und die Basis der
    /// letzten Stufe. ARGs vor dem ersten FROM (bzw. Build-Args aus Compose) werden eingesetzt.
    /// </summary>
    public static (List<string> Bases, string? Final) ParseFrom(string dockerfile, IReadOnlyDictionary<string, string>? buildArgs = null)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var bases = new List<string>();
        string? final = null;
        var seenFrom = false;

        string Substitute(string value) => Variable().Replace(value, m =>
        {
            var name = m.Groups["bare"].Success ? m.Groups["bare"].Value : m.Groups["name"].Value;
            var known = buildArgs?.GetValueOrDefault(name) ?? args.GetValueOrDefault(name);
            return m.Groups["op"].Success && string.IsNullOrEmpty(known) ? m.Groups["default"].Value : known ?? "";
        });

        foreach (var line in Instructions(dockerfile))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var instruction = parts[0].ToUpperInvariant();
            if (instruction == "ARG" && !seenFrom)
            {
                foreach (var definition in parts.Skip(1))
                {
                    var eq = definition.IndexOf('=');
                    var name = eq < 0 ? definition : definition[..eq];
                    var value = eq < 0 ? null : definition[(eq + 1)..].Trim('"', '\'');
                    if (value is not null)
                    {
                        args[name] = value;
                    }
                }
            }
            else if (instruction == "FROM")
            {
                seenFrom = true;
                var tokens = parts.Skip(1).Where(t => !t.StartsWith("--", StringComparison.Ordinal)).ToList();
                if (tokens.Count == 0)
                {
                    continue;
                }

                var image = Substitute(tokens[0]);
                string? resolved;
                if (stages.TryGetValue(image, out var stageBase))
                {
                    resolved = stageBase;
                }
                else if (image.Length == 0 || image.Equals("scratch", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = null;
                }
                else
                {
                    resolved = image;
                    if (!bases.Contains(image))
                    {
                        bases.Add(image);
                    }
                }

                if (tokens.Count >= 3 && tokens[1].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    stages[tokens[2]] = resolved;
                }

                final = resolved;
            }
        }

        return (bases, final);
    }

    /// <summary>Anweisungen ohne Kommentare, Fortsetzungszeilen ("\" am Ende) zusammengefügt.</summary>
    private static IEnumerable<string> Instructions(string dockerfile)
    {
        var current = new StringBuilder();
        foreach (var raw in dockerfile.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (current.Length == 0 && (line.TrimStart().StartsWith('#') || line.Trim().Length == 0))
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                current.Append(line[..^1]).Append(' ');
                continue;
            }

            current.Append(line);
            var instruction = current.ToString().Trim();
            current.Clear();
            if (instruction.Length > 0)
            {
                yield return instruction;
            }
        }

        if (current.ToString().Trim() is { Length: > 0 } rest)
        {
            yield return rest;
        }
    }
}
