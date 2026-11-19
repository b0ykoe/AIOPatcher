using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIOPatcher.Contracts;

// Update-Rezepte: App-eigene Update-Abläufe (z. B. mailcow update.sh), die Vorrang vor dem generischen
// "docker compose pull && up -d" haben. Jedes Rezept ist eine JSON-Datei in Recipes/ und wird in diese Assembly
// eingebettet: Agent und Server kennen denselben Katalog. Der Agent führt nur eingebettete Rezepte aus; der Server
// nennt nur Rezept-Id und Containername, nie Befehle oder Pfade. Auf dem Host braucht es policy.json "allowUpdateRecipes".

/// <param name="Id">Eindeutig, klein, z. B. "mailcow" (Dateiname ohne .json).</param>
/// <param name="Name">Anzeige des Ablaufs im Dashboard, z. B. "mailcow update.sh" ("Update über …").</param>
/// <param name="App">Name der Anwendung, z. B. "mailcow: dockerized".</param>
/// <param name="Docs">Offizielle Update-Anleitung der App.</param>
/// <param name="Command">Programm relativ zum Compose-Ordner plus Argumente; ohne Shell ausgeführt, im Compose-Ordner.</param>
/// <param name="SuccessExitCodes">Exit-Codes, die als Erfolg gelten (Standard: 0).</param>
/// <param name="FailurePatterns">Text in der Ausgabe, der trotz Exit-Code 0 einen Fehler bedeutet.</param>
/// <param name="Notes">Hinweis für den Bestätigungsdialog je Sprache ("en" Pflicht, "de" usw. optional).</param>
public sealed record UpdateRecipe(
    string Id,
    string Name,
    string App,
    string? Docs,
    RecipeMatch Match,
    IReadOnlyList<string> Command,
    int TimeoutMinutes = 30,
    IReadOnlyList<int>? SuccessExitCodes = null,
    IReadOnlyList<string>? FailurePatterns = null,
    RecipeVerify? Verify = null,
    IReadOnlyDictionary<string, string>? Notes = null)
{
    public bool IsSuccessExitCode(int code) => (SuccessExitCodes is { Count: > 0 } codes ? codes : [0]).Contains(code);

    public string? FailureIn(string line) =>
        FailurePatterns?.FirstOrDefault(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));

    public string? NoteFor(string language) =>
        Notes is null ? null : Notes.GetValueOrDefault(language) ?? Notes.GetValueOrDefault("en");
}

/// <summary>
/// Wann ein Compose-Projekt zu einem Rezept gehört: alle <see cref="Files"/> liegen im Projektordner (Label
/// com.docker.compose.project.working_dir) und mindestens ein Container des Projekts nutzt ein Image aus <see cref="Images"/>
/// (Muster mit *, z. B. "ghcr.io/mailcow/*"; leer = Image egal).
/// </summary>
public sealed record RecipeMatch(IReadOnlyList<string> Files, IReadOnlyList<string>? Images = null)
{
    public bool MatchesImage(string image) =>
        Images is not { Count: > 0 } patterns || patterns.Any(p => UpdateRecipeCatalog.Glob(p, image));
}

/// <param name="ProjectRunning">Nach dem Lauf müssen alle vorher laufenden Container des Projekts wieder laufen (nicht unhealthy).</param>
/// <param name="WaitSeconds">So lange darauf warten.</param>
public sealed record RecipeVerify(bool ProjectRunning = true, int WaitSeconds = 120);

/// <summary>Am Container gemeldet: passendes Rezept und ob die lokale Policy es erlaubt.</summary>
public sealed record RecipeRef(string Id, bool Allowed);

/// <summary>docker.recipe: Rezept für das Compose-Projekt dieses Containers ausführen.</summary>
public sealed record RecipeUpdateRequest(string Recipe, string Container);

public static partial class UpdateRecipeCatalog
{
    private const string ResourcePrefix = "AIOPatcher.Recipes.";

    private static readonly Lazy<IReadOnlyList<UpdateRecipe>> Recipes = new(Load);

    /// <summary>Alle mitgelieferten Rezepte (ungültige Dateien fallen weg; der Test prüft, dass es keine gibt).</summary>
    public static IReadOnlyList<UpdateRecipe> All => Recipes.Value;

    public static UpdateRecipe? Find(string? id) => id is null ? null : All.FirstOrDefault(r => r.Id == id);

    /// <summary>Eingebettete Rezept-Dateien als (Name, Inhalt).</summary>
    public static IEnumerable<(string Name, string Json)> Files()
    {
        var assembly = typeof(UpdateRecipeCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }

    public static UpdateRecipe? Parse(string json) => JsonSerializer.Deserialize(json, RecipeJsonContext.Default.UpdateRecipe);

    private static IReadOnlyList<UpdateRecipe> Load()
    {
        var list = new List<UpdateRecipe>();
        foreach (var (name, json) in Files())
        {
            try
            {
                if (Parse(json) is { } recipe && Validate(recipe, name).Count == 0 && list.All(r => r.Id != recipe.Id))
                {
                    list.Add(recipe);
                }
            }
            catch (JsonException)
            {
                // kaputte Datei: weglassen (Test schlägt an)
            }
        }

        return list;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    /// <summary>Regeln für Rezepte; leer = gültig. fileName: Datei muss &lt;id&gt;.json heißen.</summary>
    public static List<string> Validate(UpdateRecipe recipe, string? fileName = null)
    {
        var errors = new List<string>();
        if (recipe.Id is null || !IdPattern().IsMatch(recipe.Id))
        {
            errors.Add("id: lowercase letters, digits and '-' only (max. 40).");
        }
        else if (fileName is not null && fileName != $"{recipe.Id}.json")
        {
            errors.Add($"file must be named {recipe.Id}.json.");
        }

        if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Length > 60)
        {
            errors.Add("name: required, max. 60 characters.");
        }

        if (string.IsNullOrWhiteSpace(recipe.App) || recipe.App.Length > 80)
        {
            errors.Add("app: required, max. 80 characters.");
        }

        if (recipe.Docs is { } docs && !docs.StartsWith("https://", StringComparison.Ordinal))
        {
            errors.Add("docs: must be an https:// link.");
        }

        if (recipe.Match?.Files is not { Count: > 0 } files)
        {
            errors.Add("match.files: at least one file that identifies the app.");
        }
        else if (files.Any(f => !IsRelativePath(f)))
        {
            errors.Add("match.files: relative paths inside the compose folder only.");
        }

        if (recipe.Command is not { Count: > 0 } command || string.IsNullOrWhiteSpace(command[0]))
        {
            errors.Add("command: program and arguments as a list.");
        }
        else
        {
            if (!IsRelativePath(command[0]) || !command[0].StartsWith("./", StringComparison.Ordinal))
            {
                errors.Add("command[0]: a script inside the compose folder, written as ./name (no absolute paths, no '..').");
            }

            if (command.Any(a => a.Contains('\n') || a.Contains('\0')))
            {
                errors.Add("command: no line breaks.");
            }

            if (Path.GetFileName(command[0]) is "sh" or "bash" or "dash" or "zsh" or "env" or "sudo")
            {
                errors.Add("command[0]: run the app's script directly, not through a shell.");
            }
        }

        if (recipe.TimeoutMinutes is < 1 or > 180)
        {
            errors.Add("timeoutMinutes: 1 to 180.");
        }

        if (recipe.Verify is { WaitSeconds: < 0 or > 1800 })
        {
            errors.Add("verify.waitSeconds: 0 to 1800.");
        }

        if (recipe.Notes is not { } notes || !notes.ContainsKey("en"))
        {
            errors.Add("notes.en: an English note for the confirmation dialog (what the script does, downtime, backups).");
        }

        return errors;
    }

    private static bool IsRelativePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(part => part != "..");

    /// <summary>Einfaches Muster: * steht für beliebig viele Zeichen, sonst exakt (ohne Groß/klein).</summary>
    public static bool Glob(string pattern, string value) =>
        Regex.IsMatch(value, "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(UpdateRecipe))]
internal sealed partial class RecipeJsonContext : JsonSerializerContext;
