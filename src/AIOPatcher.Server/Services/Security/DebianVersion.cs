namespace AIOPatcher.Server.Services.Security;

/// <summary>Versionsvergleich wie dpkg (Epoche, Upstream, Debian-Revision; "~" sortiert vor allem, auch vor dem Ende).</summary>
public static class DebianVersion
{
    public static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var (ea, ua, ra) = Split(a.Trim());
        var (eb, ub, rb) = Split(b.Trim());
        if (ea != eb)
        {
            return ea.CompareTo(eb);
        }

        var upstream = CompareParts(ua, ub);
        return upstream != 0 ? upstream : CompareParts(ra, rb);
    }

    /// <summary>Die höchste Version einer Liste (null bei leerer Liste).</summary>
    public static string? Max(IEnumerable<string?> versions) =>
        versions.Where(v => !string.IsNullOrWhiteSpace(v)).Aggregate((string?)null, (max, v) => max is null || Compare(v, max) > 0 ? v : max);

    private static (long Epoch, string Upstream, string Revision) Split(string version)
    {
        long epoch = 0;
        var colon = version.IndexOf(':');
        if (colon > 0 && long.TryParse(version[..colon], out var e))
        {
            epoch = e;
            version = version[(colon + 1)..];
        }

        var dash = version.LastIndexOf('-');
        return dash > 0 ? (epoch, version[..dash], version[(dash + 1)..]) : (epoch, version, "");
    }

    /// <summary>dpkg verrevcmp: abwechselnd Nicht-Ziffern (zeichenweise, Buchstaben vor Sonstigem) und Zahlen (numerisch).</summary>
    private static int CompareParts(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                var ca = Order(a, i);
                var cb = Order(b, j);
                if (ca != cb)
                {
                    return ca.CompareTo(cb);
                }

                i++;
                j++;
            }

            while (i < a.Length && a[i] == '0')
            {
                i++;
            }

            while (j < b.Length && b[j] == '0')
            {
                j++;
            }

            var first = 0;
            while (i < a.Length && char.IsAsciiDigit(a[i]) && j < b.Length && char.IsAsciiDigit(b[j]))
            {
                if (first == 0)
                {
                    first = a[i].CompareTo(b[j]);
                }

                i++;
                j++;
            }

            if (i < a.Length && char.IsAsciiDigit(a[i]))
            {
                return 1;
            }

            if (j < b.Length && char.IsAsciiDigit(b[j]))
            {
                return -1;
            }

            if (first != 0)
            {
                return first;
            }
        }

        return 0;
    }

    private static int Order(string s, int index)
    {
        if (index >= s.Length || char.IsAsciiDigit(s[index]))
        {
            return 0;
        }

        var c = s[index];
        return c == '~' ? -1 : char.IsAsciiLetter(c) ? c : c + 256;
    }
}
