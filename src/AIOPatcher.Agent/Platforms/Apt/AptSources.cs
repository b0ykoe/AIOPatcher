using System.Text.RegularExpressions;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Ordnet kaputte Paketquellen ihrer Datei unter /etc/apt/sources.list.d zu und deaktiviert sie auf Wunsch.
/// Deaktiviert wird nur, was sicher ist: dauerhaft kaputt (404 / kein Release mehr), keine Quelle der Distribution
/// (Debian, Ubuntu, Proxmox), eigene Datei, in der alle Einträge kaputt sind. Die Datei wird nicht gelöscht, sondern
/// nach /etc/apt/aiopatcher-disabled verschoben; zurück geht es mit einem mv.
/// </summary>
public static partial class AptSources
{
    public const string SourcesDirectory = "/etc/apt/sources.list.d";
    public const string BackupDirectory = "/etc/apt/aiopatcher-disabled";

    // Quellen der Distribution fasst AIOPatcher nie an, auch wenn sie kaputt sind (z. B. Proxmox-Enterprise ohne Abo)
    private static readonly string[] ProtectedHosts = ["debian.org", "ubuntu.com", "canonical.com", "proxmox.com"];

    public sealed record Entry(string Uri, string Suite)
    {
        public string Source => $"{Uri} {Suite}";
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Options();

    /// <summary>Einträge einer .list-Datei ("deb [opts] URI suite comp…").</summary>
    public static List<Entry> ParseList(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("deb ", StringComparison.Ordinal) || l.StartsWith("deb-src ", StringComparison.Ordinal))
            .Select(l => Options().Replace(l, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3)
            .Select(p => new Entry(Normalize(p[1]), p[2]))
            .Distinct()
            .ToList();

    /// <summary>Einträge einer .sources-Datei (deb822: Absätze mit URIs/Suites, "Enabled: no" zählt nicht).</summary>
    public static List<Entry> ParseSources(string text)
    {
        var entries = new List<Entry>();
        foreach (var stanza in text.Replace("\r", "").Split("\n\n"))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in stanza.Split('\n'))
            {
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#'))
                {
                    continue; // Fortsetzungszeilen (z. B. eingebetteter Signed-By-Schlüssel) und Kommentare
                }

                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (fields.GetValueOrDefault("Enabled")?.Equals("no", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            var uris = fields.GetValueOrDefault("URIs")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var suites = fields.GetValueOrDefault("Suites")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            entries.AddRange(uris.SelectMany(u => suites.Select(s => new Entry(Normalize(u), s))));
        }

        return entries.Distinct().ToList();
    }

    private static string Normalize(string uri) => uri.TrimEnd('/');

    private static string NormalizeSource(string source)
    {
        var parts = source.Split(' ', 2);
        return parts.Length == 2 ? $"{Normalize(parts[0])} {parts[1]}" : source;
    }

    public static bool IsProtected(string uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && ProtectedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Datei je Quelle suchen und festlegen, ob sie automatisch deaktiviert werden darf.</summary>
    public static IReadOnlyList<BrokenAptSource> Classify(IReadOnlyList<BrokenAptSource> broken, string directory = SourcesDirectory)
    {
        if (broken.Count == 0 || !Directory.Exists(directory))
        {
            return broken;
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".list", StringComparison.Ordinal) || f.EndsWith(".sources", StringComparison.Ordinal))
            .Select(f => (Path: f, Entries: Read(f)))
            .ToList();
        var permanent = broken.Where(b => b.Permanent).Select(b => NormalizeSource(b.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return broken.Select(b =>
        {
            var source = NormalizeSource(b.Source);
            var file = files.FirstOrDefault(f => f.Entries.Any(e => e.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));
            if (file.Path is null)
            {
                return b;
            }

            var fixable = b.Permanent
                && file.Entries.All(e => permanent.Contains(e.Source) && !IsProtected(e.Uri));
            return b with { File = file.Path, Fixable = fixable };
        }).ToList();
    }

    private static List<Entry> Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return path.EndsWith(".sources", StringComparison.Ordinal) ? ParseSources(text) : ParseList(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Datei aus sources.list.d in den Sicherungsordner verschieben. Rückgabe: neuer Pfad, null = nicht erlaubt.</summary>
    public static string? Disable(string file, string directory = SourcesDirectory, string backupDirectory = BackupDirectory)
    {
        var full = Path.GetFullPath(file);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(directory), StringComparison.Ordinal)
            || !File.Exists(full)
            || new FileInfo(full).LinkTarget is not null)
        {
            return null; // nur echte Dateien direkt in sources.list.d
        }

        Directory.CreateDirectory(backupDirectory);
        var target = Path.Combine(backupDirectory, $"{Path.GetFileName(full)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        File.Move(full, target);
        return target;
    }
}
