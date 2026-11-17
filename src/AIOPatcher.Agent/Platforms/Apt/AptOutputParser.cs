using System.Text.RegularExpressions;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Wertet die Simulation von <c>apt-get -s dist-upgrade</c> aus. Beispielzeilen:
/// <code>
/// Inst libssl3 [3.0.13-1] (3.0.15-1~deb12u1 Debian-Security:12/stable-security [amd64])
/// Inst curl [8.5.0-2ubuntu10.4] (8.5.0-2ubuntu10.6 Ubuntu:24.04/noble-updates, Ubuntu:24.04/noble-security [amd64]) []
/// Inst proxmox-kernel-6.8 (6.8.12-5 Proxmox:bookworm [amd64])
/// </code>
/// </summary>
public static partial class AptOutputParser
{
    [GeneratedRegex(@"^Inst\s+(?<name>\S+)\s+(?:\[(?<current>[^\]]+)\]\s+)?\((?<new>\S+)\s+(?<origin>[^\[\)]*?)\s*(?:\[[^\]]*\])?\)")]
    private static partial Regex InstLine();

    /// <summary>
    /// Ausgabe von <c>dpkg-query -W -f='${Package}\t${source:Package}\n' …</c> → Paket → Quellpaket.
    /// </summary>
    public static Dictionary<string, string> ParseSourceMap(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                map[parts[0]] = parts[1];
            }
        }

        return map;
    }

    /// <summary>dpkg-query mit Status, Paket, Version, Quellpaket, Maintainer, Quellversion (Tab-getrennt). Nur installierte ("ii").</summary>
    public static List<InstalledSoftware> ParseInstalled(string output)
    {
        var result = new List<InstalledSoftware>();
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length < 4 || !parts[0].StartsWith("ii", StringComparison.Ordinal))
            {
                continue;
            }

            var maintainer = parts.Length > 4 ? parts[4].Split('<')[0].Trim() : null;
            var sourceVersion = parts.Length > 5 ? parts[5].Trim() : "";
            result.Add(new InstalledSoftware(parts[1], parts[2], string.IsNullOrEmpty(maintainer) ? null : maintainer, "dpkg",
                PackageId: string.IsNullOrEmpty(parts[3]) ? parts[1] : parts[3],
                SourceVersion: sourceVersion.Length == 0 ? null : sourceVersion));
        }

        return result;
    }

    /// <summary>"snap list": Name Version Rev Tracking Publisher Notes</summary>
    public static IEnumerable<InstalledSoftware> ParseSnapList(string output) =>
        output.Split('\n')
            .Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 5)
            .Select(p => new InstalledSoftware(p[0], p[1], p[4].TrimEnd('✓', '*', '✪'), "snap"));

    public static IReadOnlyList<AvailableUpdate> Parse(string simulationOutput)
    {
        var updates = new List<AvailableUpdate>();
        foreach (var rawLine in simulationOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = InstLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var origin = match.Groups["origin"].Value.Trim();
            updates.Add(new AvailableUpdate(
                Name: match.Groups["name"].Value,
                CurrentVersion: match.Groups["current"].Success ? match.Groups["current"].Value : null,
                AvailableVersion: match.Groups["new"].Value,
                IsSecurity: IsSecurityOrigin(origin),
                Classification: origin.Length > 0 ? origin : null));
        }

        return updates;
    }

    /// <summary>Debian: "Debian-Security:12/stable-security", Ubuntu: "Ubuntu:24.04/noble-security".</summary>
    public static bool IsSecurityOrigin(string origin) =>
        origin.Contains("-security", StringComparison.OrdinalIgnoreCase)
        || origin.Contains("Debian-Security", StringComparison.OrdinalIgnoreCase);
}
