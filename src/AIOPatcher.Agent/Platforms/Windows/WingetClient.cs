using AIOPatcher.Agent.Inventory;

namespace AIOPatcher.Agent.Platforms.Windows;

public sealed record WingetUpgrade(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// winget (App Installer) als SYSTEM benutzen. Dort ist winget nicht im PATH: die neueste Version unter
/// C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_* suchen. Windows Server 2019/2022 haben meist kein winget.
/// </summary>
public static class WingetClient
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromMinutes(30);

    public static string? FindExecutable()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var candidate = Directory.Exists(apps)
                ? Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                    .Select(d => Path.Combine(d, "winget.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            if (candidate is not null)
            {
                return candidate;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // als normaler Benutzer nicht lesbar: unten über das Benutzerprofil versuchen
        }

        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(user) ? user : null;
    }

    public static async Task<IReadOnlyList<WingetUpgrade>?> ListUpgradesAsync(CancellationToken ct)
    {
        if (FindExecutable() is not { } winget)
        {
            return null;
        }

        var result = await ProcessRunner.RunAsync(winget,
            ["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"], ListTimeout, ct);
        return ParseTable(result.StandardOutput);
    }

    /// <summary>
    /// Liest die Tabelle von "winget upgrade". Die Überschriften sind übersetzt, deshalb zählen nur die
    /// Spaltenpositionen aus der Kopfzeile über der Strichlinie (Name, Id, Version, Verfügbar, Quelle).
    /// </summary>
    public static List<WingetUpgrade> ParseTable(string output)
    {
        // Fortschrittsanzeigen schreiben mit \r über dieselbe Zeile: nur den letzten Teil behalten.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();
        var separator = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        if (separator <= 0)
        {
            return [];
        }

        var header = lines[separator - 1];
        var starts = new List<int> { header.TakeWhile(char.IsWhiteSpace).Count() };
        for (var i = 1; i < header.Length; i++)
        {
            if (header[i] != ' ' && header[i - 1] == ' ')
            {
                starts.Add(i);
            }
        }

        if (starts.Count < 4)
        {
            return [];
        }

        var result = new List<WingetUpgrade>();
        foreach (var line in lines.Skip(separator + 1))
        {
            if (line.Length <= starts[^1] - 1 || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string Column(int index) =>
                index >= starts.Count ? "" :
                line.Length <= starts[index] ? "" :
                line[starts[index]..(index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length)].Trim();

            var id = Column(1);
            var available = Column(3);
            if (id.Length == 0 || id.Contains(' ') || available.Length == 0)
            {
                continue; // Fußzeilen wie "5 Aktualisierungen verfügbar."
            }

            result.Add(new WingetUpgrade(Column(0).TrimEnd('…'), id, Column(2), available, starts.Count > 4 ? Column(4) : "winget"));
        }

        return result;
    }

    /// <summary>Aktualisiert ein Paket still. Gibt den Exit-Code zurück (0 = ok).</summary>
    public static async Task<int> UpgradeAsync(string winget, string id, Action<string> log, CancellationToken ct)
    {
        log($"$ winget upgrade --id {id} --exact --silent");
        return await ProcessRunner.RunStreamingAsync(winget,
            ["upgrade", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            UpgradeTimeout, line =>
            {
                // Fortschrittsbalken nicht ins Log schreiben
                var text = line[(line.LastIndexOf('\r') + 1)..].Trim();
                if (text.Length > 0 && !text.All(c => c is '█' or '▒' or ' ' or '-' or '\\' or '|' or '/'))
                {
                    log(text);
                }
            }, ct);
    }
}
