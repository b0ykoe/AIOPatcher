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
