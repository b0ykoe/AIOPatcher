using System.Text.RegularExpressions;

namespace AIOPatcher.Tests;

/// <summary>
/// PowerShell unterscheidet bei Variablen nicht nach Groß-/Kleinschreibung: Eine Hilfsvariable $sha256 überschreibt den
/// Parameter [string]$Sha256 und wird dabei zum Text (so geschehen in install.ps1 bis 0.13). Diese Tests laufen ohne
/// PowerShell (CI auf Linux) und prüfen die Skripte als Text.
/// </summary>
public sealed partial class InstallScriptTests
{
    // Absichtliche Normalisierungen von Parametern (TrimEnd, Großschreibung, -Force bei Server-Wechsel)
    private static readonly Dictionary<string, string[]> AllowedReassignments = new()
    {
        ["install.ps1"] = ["Server", "PublicUrl", "Fingerprint", "Force"],
        ["aiopatcher-agent-lite.ps1"] = ["Server", "Fingerprint", "StateDir", "Force", "Token", "Command"],
    };

    public static TheoryData<string> Scripts => ["install.ps1", "aiopatcher-agent-lite.ps1"];

    [Theory]
    [MemberData(nameof(Scripts))]
    public void NoVariableOverwritesAScriptParameter(string file)
    {
        var text = File.ReadAllText(ScriptPath(file));
        var parameters = Parameters(text);
        Assert.NotEmpty(parameters);

        var offenders = AssignmentRegex().Matches(text)
            .Select(m => m.Groups["name"].Value)
            .Where(name => parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Where(name => !AllowedReassignments[file].Contains(name, StringComparer.Ordinal))
            .Distinct()
            .ToList();
        Assert.True(offenders.Count == 0, $"{file}: variables collide with script parameters (PowerShell ignores case): {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ScriptsArePureAscii(string file) =>
        Assert.DoesNotContain(File.ReadAllBytes(ScriptPath(file)), b => b > 127);

    /// <summary>Namen aus dem param(...)-Block am Skriptanfang.</summary>
    private static List<string> Parameters(string text)
    {
        var start = text.IndexOf("param(", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, "param block not found");
        var depth = 0;
        var end = start;
        for (var i = start + "param".Length; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) { end = i; break; }
        }

        return ParameterRegex().Matches(text[start..end]).Select(m => m.Groups["name"].Value).ToList();
    }

    private static string ScriptPath(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AIOPatcher.Server", "AgentAssets", file);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(file);
    }

    [GeneratedRegex(@"\[(?:string|switch|int|bool)\]\s*\$(?<name>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterRegex();

    // Zuweisung am Zeilenanfang oder nach ; bzw. {: "$name =" (nicht "-eq", "==")
    [GeneratedRegex(@"(?:^|[;{]\s*)\s*\$(?<name>\w+)\s*(?:\+|-)?=(?!=)", RegexOptions.Multiline)]
    private static partial Regex AssignmentRegex();
}
