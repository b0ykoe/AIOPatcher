using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Apt;

/// <summary>
/// Changelogs anstehender Updates über <c>apt-get changelog paket=version</c>. apt nimmt die Adresse aus den
/// Paketquellen (Feld "Changelogs" der Release-Datei): Debian, Ubuntu und Proxmox bieten das an, auch über das
/// Enterprise-Repo (apt kennt die Zugangsdaten). Nur Lesen, nichts wird installiert.
/// </summary>
public static partial class AptChangelog
{
    /// <summary>Höchstens so viele Pakete je Auftrag (der Server teilt größere Listen auf).</summary>
    public const int MaxPackages = 40;

    /// <summary>Neueste Einträge zuerst: der Anfang reicht für alles seit der installierten Version.</summary>
    public const int MaxChars = 32_000;

    private static readonly TimeSpan PerPackage = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Total = TimeSpan.FromMinutes(5);

    public static bool Supported => OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");

    // Paketnamen und Versionen wie in Debian Policy 5.6.1/5.6.12; schließt Optionen ("-o ...") und Leerzeichen aus.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.+-]{0,99}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.+~:-]{1,100}$")]
    private static partial Regex VersionPattern();

    public static bool IsValid(ChangelogPackage package) =>
        NamePattern().IsMatch(package.Name) && VersionPattern().IsMatch(package.Version) && char.IsDigit(package.Version[0]);

    public static async Task<List<PackageChangelog>> FetchAsync(IReadOnlyList<ChangelogPackage> packages, CancellationToken ct)
    {
        var result = new List<PackageChangelog>();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(Total);

        foreach (var package in packages.Take(MaxPackages))
        {
            if (!IsValid(package))
            {
                result.Add(new PackageChangelog(package.Name, package.Version, null, "Invalid package name or version."));
                continue;
            }

            if (total.IsCancellationRequested)
            {
                result.Add(new PackageChangelog(package.Name, package.Version, null, "Time limit reached."));
                continue;
            }

            try
            {
                var run = await ProcessRunner.RunAsync("apt-get", ["-qq", "changelog", $"{package.Name}={package.Version}"], PerPackage, total.Token);
                result.Add(run.Success && !string.IsNullOrWhiteSpace(run.StandardOutput)
                    ? new PackageChangelog(package.Name, package.Version, Trim(run.StandardOutput), null)
                    : new PackageChangelog(package.Name, package.Version, null, Error(run)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                result.Add(new PackageChangelog(package.Name, package.Version, null, ex is OperationCanceledException ? "Timed out." : ex.Message));
            }
        }

        return result;
    }

    /// <summary>Anfang behalten, an einer Zeilengrenze abschneiden.</summary>
    public static string Trim(string text)
    {
        if (text.Length <= MaxChars)
        {
            return text;
        }

        var cut = text.LastIndexOf('\n', MaxChars);
        return text[..(cut > 0 ? cut : MaxChars)];
    }

    /// <summary>Letzte aussagekräftige Zeile von apt (z. B. "E: Failed to fetch changelog ..."), gekürzt.</summary>
    private static string Error(ProcessResult run)
    {
        var line = run.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("E:", StringComparison.Ordinal)) ?? run.StandardError.Trim();
        if (string.IsNullOrEmpty(line))
        {
            line = $"apt-get changelog exit code {run.ExitCode}.";
        }

        return line.Length > 300 ? line[..300] : line;
    }
}
