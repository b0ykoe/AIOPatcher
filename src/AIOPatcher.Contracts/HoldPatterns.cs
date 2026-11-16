namespace AIOPatcher.Contracts;

/// <summary>
/// Muster für zurückgehaltene Updates: Paketname (Linux) bzw. Titel oder KB-Nummer (Windows).
/// <c>*</c> steht für beliebig viele Zeichen, <c>?</c> für genau eines; Groß-/Kleinschreibung egal.
/// Beispiele: <c>linux-image-*</c>, <c>KB5034441</c>, <c>*Preview*</c>.
/// </summary>
public static class HoldPatterns
{
    public const int MaxPatternLength = 200;

    public static bool IsHeld(IReadOnlyList<string>? patterns, params string?[] candidates)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            foreach (var pattern in patterns)
            {
                if (Matches(pattern, candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Glob-Vergleich über den ganzen Text, ohne Regex (linear, kein Backtracking-Risiko).</summary>
    public static bool Matches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
