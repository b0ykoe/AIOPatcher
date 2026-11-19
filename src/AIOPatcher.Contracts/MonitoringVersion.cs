using System.Text.RegularExpressions;

namespace AIOPatcher.Contracts;

/// <summary>Versionen von Monitoring-Clients vergleichen: "v4.7.2", "4.7.2-1", "2.2.0p12", "1:2.1.0-1".</summary>
public static partial class MonitoringVersion
{
    /// <summary>Zahlenteile (Epoche und "v" ignoriert, Checkmk-Patchlevel "p12" als vierte Stelle). null = nicht lesbar.</summary>
    public static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = VersionPattern().Match(version.Trim());
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Groups["main"].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3)
        {
            parts.Add(0);
        }

        parts.Add(match.Groups["p"].Success ? int.Parse(match.Groups["p"].Value) : 0);
        return [.. parts];
    }

    /// <summary>&lt; 0: a älter als b. Unlesbare Versionen gelten als gleich (dann wird nichts abgelehnt).</summary>
    public static int Compare(string? a, string? b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return 0;
        }

        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>Nur Haupt- und Nebenversion vergleichen (z. B. Checkmk 2.2 gegen 2.3).</summary>
    public static int CompareMinor(string? a, string? b) =>
        Parse(a) is { } x && Parse(b) is { } y ? (x[0], x[1]).CompareTo((y[0], y[1])) : 0;

    /// <summary>Lesbare Kurzform ohne Paket-Revision: "4.7.2", "2.2.0p12".</summary>
    public static string? Display(string? version) =>
        version is null ? null : VersionPattern().Match(version.Trim()) is { Success: true } m ? m.Value.TrimStart('v', 'V').Split(':').Last() : version.Trim();

    [GeneratedRegex(@"^(?:\d+:)?[vV]?(?<main>\d+(?:\.\d+){0,3})(?:p(?<p>\d+))?")]
    private static partial Regex VersionPattern();
}
