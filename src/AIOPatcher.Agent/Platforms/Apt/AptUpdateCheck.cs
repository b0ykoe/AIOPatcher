using System.Text.RegularExpressions;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Wertet die Ausgabe von "apt-get update" aus. Eine einzelne kaputte Paketquelle (z. B. eingestelltes Fremd-Repo wie
/// NodeSource node_18.x) lässt apt-get update mit Fehler enden, obwohl alle anderen Quellen aktuell sind. Dann wird
/// trotzdem gepatcht, mit Warnung. Abgebrochen wird nur bei echten Problemen (Sperre, dpkg unterbrochen) oder wenn
/// gar keine Quelle geladen werden konnte.
/// </summary>
public static partial class AptUpdateCheck
{
    public sealed record Result(bool CanContinue, IReadOnlyList<string> BrokenSources);

    // E: The repository 'https://deb.nodesource.com/node_18.x jammy Release' no longer has a Release file.
    [GeneratedRegex(@"^[EW]: The repository '([^']+)'")]
    private static partial Regex RepositoryError();

    // Err:8 https://deb.nodesource.com/node_18.x jammy Release
    [GeneratedRegex(@"^Err:\d+\s+(\S+\s+\S+)")]
    private static partial Regex ErrLine();

    // Hit:1 … / Get:2 … = Quelle erfolgreich geladen
    [GeneratedRegex(@"^(Hit|Get):\d+\s")]
    private static partial Regex OkLine();

    private static readonly string[] Fatal =
    [
        "Could not get lock",
        "Unable to lock",
        "dpkg was interrupted",
        "Unable to acquire the dpkg frontend lock",
    ];

    public static Result Analyze(int exitCode, IReadOnlyList<string> lines)
    {
        var broken = lines
            .Select(l => l.Trim())
            .Select(l => RepositoryError().Match(l) is { Success: true } r ? r.Groups[1].Value
                : ErrLine().Match(l) is { Success: true } e ? e.Groups[1].Value
                : null)
            .OfType<string>()
            .Select(s => Regex.Replace(s, @"\s+(In)?Release$", ""))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (exitCode == 0)
        {
            return new Result(true, broken);
        }

        var fatal = lines.Any(l => Fatal.Any(f => l.Contains(f, StringComparison.OrdinalIgnoreCase)));
        var anyLoaded = lines.Any(l => OkLine().IsMatch(l.Trim()));
        return new Result(!fatal && anyLoaded && broken.Count > 0, broken);
    }
}
