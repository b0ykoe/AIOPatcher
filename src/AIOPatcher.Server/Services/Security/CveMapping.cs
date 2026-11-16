using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Security;

/// <summary>Reine Hilfsfunktionen der CVE-Zuordnung (ohne Netz, gut testbar).</summary>
public static partial class CveMapping
{
    [GeneratedRegex(@"CVE-\d{4}-\d{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex CvePattern();

    [GeneratedRegex(@"\b(?<distro>Debian(?:-Security)?|Ubuntu):(?<version>\d+(?:\.\d+)?)")]
    private static partial Regex OriginPattern();

    [GeneratedRegex(@"^KB(?<number>\d{6,8})$", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();

    /// <summary>
    /// OSV-Ökosystem aus der apt-Herkunft, z. B. "Debian-Security:12/stable-security" → "Debian:12",
    /// "Ubuntu:24.04/noble-security" → "Ubuntu:24.04:LTS". Proxmox-eigene Pakete haben keins (null).
    /// </summary>
    public static string? OsvEcosystem(string? classification)
    {
        var match = OriginPattern().Match(classification ?? "");
        if (!match.Success)
        {
            return null;
        }

        var version = match.Groups["version"].Value;
        if (match.Groups["distro"].Value.StartsWith("Debian", StringComparison.Ordinal))
        {
            return $"Debian:{version.Split('.')[0]}";
        }

        // Ubuntu-LTS: gerade Jahreszahl im April (22.04, 24.04, …)
        var parts = version.Split('.');
        var lts = parts.Length == 2 && parts[1] == "04" && int.TryParse(parts[0], out var year) && year % 2 == 0;
        return lts ? $"Ubuntu:{version}:LTS" : $"Ubuntu:{version}";
    }

    public static string? KbNumber(string? value)
    {
        var match = KbPattern().Match(value?.Trim() ?? "");
        return match.Success ? match.Groups["number"].Value : null;
    }

    /// <summary>Cache-Schlüssel für die Zuordnung eines Updates. null = nicht zuordenbar.</summary>
    public static string? LookupKey(PendingUpdate update, OsFamily os)
    {
        if (os == OsFamily.Windows)
        {
            return KbNumber(update.AvailableVersion) is { } kb ? $"msrc|KB{kb}" : null;
        }

        if (update.CurrentVersion is null || update.AvailableVersion is null || OsvEcosystem(update.Classification) is not { } ecosystem)
        {
            return null;
        }

        return $"osv|{ecosystem}|{update.SourcePackage ?? update.Name}|{update.CurrentVersion}|{update.AvailableVersion}";
    }

    public static (string Ecosystem, string Package, string Current, string Available) ParseOsvKey(string key)
    {
        var parts = key.Split('|');
        return (parts[1], parts[2], parts[3], parts[4]);
    }

    public static IEnumerable<string> ExtractCves(string? text) =>
        CvePattern().Matches(text ?? "").Select(m => m.Value.ToUpperInvariant()).Distinct();

    public static string Join(IEnumerable<string> cves) =>
        string.Join(',', cves.Distinct().Order(StringComparer.Ordinal).Take(200));

    public static string[] Split(string? cves) => string.IsNullOrEmpty(cves) ? [] : cves.Split(',');

    /// <summary>
    /// MSRC-CVRF-Dokument (JSON) → KB-Nummer → CVEs. Remediation-Typ 2 = Vendor Fix, Description = KB-Nummer.
    /// </summary>
    public static Dictionary<string, HashSet<string>> ParseMsrcDocument(JsonNode? document)
    {
        var map = new Dictionary<string, HashSet<string>>();
        foreach (var vulnerability in (document?["Vulnerability"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var cve = (string?)vulnerability["CVE"];
            if (cve is null || !CvePattern().IsMatch(cve))
            {
                continue;
            }

            foreach (var remediation in (vulnerability["Remediations"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var value = (string?)remediation["Description"]?["Value"];
                if (value is null || value.Length is < 6 or > 8 || !value.All(char.IsAsciiDigit))
                {
                    continue;
                }

                if (!map.TryGetValue(value, out var set))
                {
                    map[value] = set = [];
                }

                set.Add(cve.ToUpperInvariant());
            }
        }

        return map;
    }
}
