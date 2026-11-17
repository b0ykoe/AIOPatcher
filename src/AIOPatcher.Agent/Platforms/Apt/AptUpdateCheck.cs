using System.Text.RegularExpressions;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Wertet die Ausgabe von "apt-get update" aus. Eine einzelne kaputte Paketquelle (z. B. eingestelltes Fremd-Repo wie
/// NodeSource node_18.x) lässt apt-get update mit Fehler enden, obwohl alle anderen Quellen aktuell sind. Dann wird
/// trotzdem gepatcht, mit Warnung. Abgebrochen wird nur bei echten Problemen (Sperre, dpkg unterbrochen, unbekannter
/// Fehler) oder wenn gar keine Quelle geladen werden konnte.
/// </summary>
public static partial class AptUpdateCheck
{
    public sealed record Result(bool CanContinue, IReadOnlyList<BrokenAptSource> Broken)
    {
        public IReadOnlyList<string> BrokenSources => Broken.Select(b => b.Source).ToList();
    }

    // E: The repository 'https://deb.nodesource.com/node_18.x jammy Release' no longer has a Release file.
    [GeneratedRegex(@"^[EW]: The repository '([^']+)'\s*(.*)$")]
    private static partial Regex RepositoryError();

    // Err:8 https://deb.nodesource.com/node_18.x jammy Release
    [GeneratedRegex(@"^Err:\d+\s+(\S+)\s+(\S+)")]
    private static partial Regex ErrLine();

    // W: GPG error: https://example.org/repo stable InRelease: The following signatures …
    [GeneratedRegex(@"^[EW]: GPG error: (\S+) (\S+) \S*Release: (.*)$")]
    private static partial Regex GpgError();

    // Hit:1 … / Get:2 … = Quelle erfolgreich geladen
    [GeneratedRegex(@"^(Hit|Get):\d+\s")]
    private static partial Regex OkLine();

    [GeneratedRegex(@"\s*\[IP: [^\]]*\]")]
    private static partial Regex IpSuffix();

    [GeneratedRegex(@"\s+(In)?Release$")]
    private static partial Regex ReleaseSuffix();

    private static readonly string[] Fatal =
    [
        "Could not get lock",
        "Unable to lock",
        "dpkg was interrupted",
        "Unable to acquire the dpkg frontend lock",
    ];

    // Fehler, die nicht von selbst verschwinden (anders als DNS, Timeout, 5xx)
    private static readonly string[] PermanentMarkers = ["404", "no longer has a Release file", "does not have a Release file"];

    public static Result Analyze(int exitCode, IReadOnlyList<string> lines)
    {
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string source, string reason)
        {
            source = ReleaseSuffix().Replace(source.Trim(), "");
            if (!reasons.TryGetValue(source, out var list))
            {
                reasons[source] = list = [];
            }

            reason = IpSuffix().Replace(reason, "").Trim().TrimEnd('.');
            if (reason.Length > 0 && !list.Contains(reason))
            {
                list.Add(reason);
            }
        }

        string? currentErr = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (currentErr is not null && raw.Length > 0 && char.IsWhiteSpace(raw[0]) && line.Length > 0)
            {
                Add(currentErr, line); // Begründung steht eingerückt unter der Err-Zeile
                continue;
            }

            currentErr = null;
            if (ErrLine().Match(line) is { Success: true } err)
            {
                currentErr = $"{err.Groups[1].Value} {err.Groups[2].Value}";
                Add(currentErr, "");
            }
            else if (RepositoryError().Match(line) is { Success: true } repo)
            {
                Add(repo.Groups[1].Value, repo.Groups[2].Value);
            }
            else if (GpgError().Match(line) is { Success: true } gpg)
            {
                Add($"{gpg.Groups[1].Value} {gpg.Groups[2].Value}", gpg.Groups[3].Value);
            }
        }

        var broken = reasons
            .Select(r => new BrokenAptSource(
                r.Key,
                r.Value.Count == 0 ? "unreachable" : string.Join("; ", r.Value),
                r.Value.Any(v => PermanentMarkers.Any(m => v.Contains(m, StringComparison.OrdinalIgnoreCase)))))
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
