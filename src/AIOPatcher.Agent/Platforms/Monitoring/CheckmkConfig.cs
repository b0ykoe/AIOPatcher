using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>Registrierte Verbindung des Agent Controllers.</summary>
/// <param name="SiteId">"server[:port]/site".</param>
public sealed record CheckmkConnection(string SiteId, string Uuid)
{
    public string Server => SiteId.Split('/')[0];
    public string Site => SiteId.Contains('/') ? SiteId[(SiteId.LastIndexOf('/') + 1)..] : "";

    /// <summary>Gleiche Site (Servername ohne Port, Groß-/Kleinschreibung egal, Site-Name exakt)?</summary>
    public bool Matches(string server, string site) =>
        string.Equals(CheckmkConfig.HostOf(Server), CheckmkConfig.HostOf(server), StringComparison.OrdinalIgnoreCase) && Site == site;
}

/// <summary>
/// Konfiguration des Checkmk-Agents lesen und umschreiben: only_from in xinetd (Linux-Legacy) und in
/// check_mk.user.yml (Windows), Verbindungen aus "cmk-agent-ctl status --json".
/// </summary>
public static partial class CheckmkConfig
{
    // --- xinetd (Linux, Legacy-Pull) -----------------------------------------------------------------

    public static IReadOnlyList<string> ParseXinetdOnlyFrom(string text) =>
        XinetdOnlyFromPattern().Match(text) is { Success: true } m ? Split(m.Groups["value"].Value) : [];

    /// <summary>only_from setzen (bestehende Zeile ersetzen, sonst vor der schließenden Klammer des Dienstes einfügen).</summary>
    public static string RewriteXinetdOnlyFrom(string text, IReadOnlyList<string> onlyFrom)
    {
        var value = string.Join(' ', onlyFrom);
        if (XinetdOnlyFromPattern().Match(text) is { Success: true } m)
        {
            return text[..m.Index] + $"{m.Groups["indent"].Value}only_from{m.Groups["sep"].Value}{value}" + text[(m.Index + m.Length)..];
        }

        var close = text.LastIndexOf('}');
        if (close < 0)
        {
            throw new InvalidOperationException("The xinetd file has no service block.");
        }

        var indent = IndentPattern().Match(text) is { Success: true } i ? i.Groups[1].Value : "\t";
        var lineStart = text.LastIndexOf('\n', close) + 1;
        return text[..lineStart] + $"{indent}only_from      = {value}\n" + text[lineStart..];
    }

    // --- check_mk.user.yml (Windows) -----------------------------------------------------------------

    public static IReadOnlyList<string> ParseYamlOnlyFrom(string text)
    {
        var lines = Lines(text);
        if (FindGlobal(lines) is not { } global || FindOnlyFrom(lines, global) is not { } entry)
        {
            return [];
        }

        return ReadYamlValue(lines, entry.Line, entry.Indent);
    }

    /// <summary>global: only_from setzen; fehlt global:, wird es angehängt. Übrige Datei bleibt unverändert.</summary>
    public static string RewriteYamlOnlyFrom(string text, IReadOnlyList<string> onlyFrom)
    {
        var value = string.Join(' ', onlyFrom);
        var lines = Lines(text);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";

        if (FindGlobal(lines) is not { } global)
        {
            var prefix = text.Length == 0 || text.EndsWith('\n') ? text : text + newline;
            return prefix + $"global:{newline}    only_from: {value}{newline}";
        }

        if (FindOnlyFrom(lines, global) is { } entry)
        {
            // Listenzeilen ("- 10.0.0.1") unter only_from entfernen
            var end = entry.Line + 1;
            while (end < lines.Count && (lines[end].Trim().StartsWith('-') && IndentOf(lines[end]) >= entry.Indent))
            {
                end++;
            }

            lines.RemoveRange(entry.Line, end - entry.Line);
            lines.Insert(entry.Line, $"{new string(' ', entry.Indent)}only_from: {value}");
        }
        else
        {
            var indent = ChildIndent(lines, global) ?? 4;
            lines.Insert(global + 1, $"{new string(' ', indent)}only_from: {value}");
        }

        return string.Join(newline, lines);
    }

    // --- Agent Controller ----------------------------------------------------------------------------

    /// <summary>
    /// Verbindungen aus der JSON-Ausgabe von "cmk-agent-ctl status --json" bzw. aus registered_connections.json.
    /// Tolerant gelesen (Aufbau kann sich je Version unterscheiden): Objekte mit "site_id" und "uuid" sowie
    /// Einträge, deren Schlüssel "server/site" ist und darunter eine "uuid" haben.
    /// </summary>
    public static IReadOnlyList<CheckmkConnection> ParseConnections(string json)
    {
        var found = new List<CheckmkConnection>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, found, 0);
        }
        catch (JsonException)
        {
            return [];
        }

        return found.DistinctBy(c => c.Uuid).ToList();
    }

    private static void Walk(JsonElement element, List<CheckmkConnection> found, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Walk(item, found, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (String(element, "site_id") is { } siteId && FindUuid(element, 0) is { } ownUuid)
        {
            found.Add(new CheckmkConnection(siteId, ownUuid));
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (SiteIdPattern().IsMatch(property.Name) && property.Value.ValueKind == JsonValueKind.Object && FindUuid(property.Value, 0) is { } uuid)
            {
                found.Add(new CheckmkConnection(property.Name, uuid));
            }
            else
            {
                Walk(property.Value, found, depth + 1);
            }
        }
    }

    private static string? FindUuid(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 3)
        {
            return null;
        }

        if (String(element, "uuid") is { } uuid && UuidPattern().IsMatch(uuid))
        {
            return uuid;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (FindUuid(property.Value, depth + 1) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // --- Hilfen ---------------------------------------------------------------------------------------

    /// <summary>Server ohne Port ("cmk.example.com:8000" → "cmk.example.com", "[::1]:8000" → "::1").</summary>
    public static string HostOf(string server)
    {
        if (server.StartsWith('['))
        {
            var end = server.IndexOf(']');
            return end > 0 ? server[1..end] : server;
        }

        return server.Count(c => c == ':') == 1 ? server[..server.IndexOf(':')] : server;
    }

    /// <summary>IP-Adresse oder Netz (CIDR), wie es only_from erwartet.</summary>
    public static bool ValidSource(string value) =>
        IPAddress.TryParse(value, out _) || IPNetwork.TryParse(value, out _);

    private static IReadOnlyList<string> Split(string value)
    {
        var trimmed = value.Trim().Trim('"', '\'');
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim('"', '\''))
            .Where(v => v.Length > 0 && v != "~" && v != "null")
            .ToList();
    }

    private static List<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();

    private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

    private static bool IsContent(string line) => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#');

    private static int? FindGlobal(List<string> lines) =>
        lines.FindIndex(l => GlobalPattern().IsMatch(l)) is var i and >= 0 ? i : null;

    /// <summary>Zeilen des global:-Blocks (bis zur nächsten Zeile ohne Einrückung).</summary>
    private static IEnumerable<int> Block(List<string> lines, int global)
    {
        for (var i = global + 1; i < lines.Count; i++)
        {
            if (IsContent(lines[i]) && IndentOf(lines[i]) == 0)
            {
                yield break;
            }

            yield return i;
        }
    }

    private static int? ChildIndent(List<string> lines, int global) =>
        Block(lines, global).Where(i => IsContent(lines[i])).Select(i => (int?)IndentOf(lines[i])).FirstOrDefault();

    private static (int Line, int Indent)? FindOnlyFrom(List<string> lines, int global)
    {
        var indent = ChildIndent(lines, global);
        foreach (var i in Block(lines, global))
        {
            if (indent is not null && IndentOf(lines[i]) == indent && YamlOnlyFromPattern().IsMatch(lines[i]))
            {
                return (i, indent.Value);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadYamlValue(List<string> lines, int line, int indent)
    {
        var inline = YamlOnlyFromPattern().Match(lines[line]).Groups["value"].Value;
        var values = Split(inline.Split('#')[0]).ToList();
        for (var i = line + 1; i < lines.Count && lines[i].Trim().StartsWith('-') && IndentOf(lines[i]) >= indent; i++)
        {
            values.AddRange(Split(lines[i].Trim()[1..]));
        }

        return values;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)only_from(?<sep>\s*=\s*)(?<value>[^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex XinetdOnlyFromPattern();

    [GeneratedRegex(@"^([ \t]+)\S", RegexOptions.Multiline)]
    private static partial Regex IndentPattern();

    [GeneratedRegex(@"^global:\s*(#.*)?$")]
    private static partial Regex GlobalPattern();

    [GeneratedRegex(@"^\s+only_from:\s*(?<value>.*)$")]
    private static partial Regex YamlOnlyFromPattern();

    [GeneratedRegex(@"^[^/\s]+/[A-Za-z0-9_\-]+$")]
    private static partial Regex SiteIdPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidPattern();
}
