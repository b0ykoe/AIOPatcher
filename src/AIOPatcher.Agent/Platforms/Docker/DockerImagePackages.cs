using System.Text.RegularExpressions;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Docker;

/// <summary>
/// Paketliste eines Images, gelesen aus einem seiner Container (laufend oder gestoppt) per "docker cp" in einen
/// temporären Ordner: /etc/os-release und die Paketdatenbank (dpkg bzw. apk). Der Container wird dabei nicht verändert
/// und nichts darin ausgeführt. Der Server gleicht die Pakete dann wie bei Hosts mit OSV ab.
/// </summary>
public static partial class DockerImagePackages
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const long MaxFileBytes = 32 * 1024 * 1024;
    private const int MaxPackages = 5000;
    public const int MaxImagesPerTask = 20;

    [GeneratedRegex("^[0-9a-f]{12,64}$")]
    private static partial Regex ContainerId();

    public static async Task<List<ImagePackages>> CollectAsync(IReadOnlyList<ImagePackagesTarget> targets, CancellationToken ct)
    {
        var result = new List<ImagePackages>();
        foreach (var target in targets.Take(MaxImagesPerTask))
        {
            if (!ContainerId().IsMatch(target.ContainerId))
            {
                result.Add(new ImagePackages(target.ImageId, null, null, null, [], "Invalid container id."));
                continue;
            }

            var dir = Directory.CreateTempSubdirectory("aiopatcher-pkg-");
            try
            {
                result.Add(await ReadAsync(target, dir.FullName, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new ImagePackages(target.ImageId, null, null, null, [], ex.Message));
            }
            finally
            {
                try
                {
                    dir.Delete(recursive: true);
                }
                catch (IOException)
                {
                    // Aufräumen klappt beim nächsten Mal (Temp-Ordner)
                }
            }
        }

        return result;
    }

    private static async Task<ImagePackages> ReadAsync(ImagePackagesTarget target, string dir, CancellationToken ct)
    {
        var os = await CopyAsync(target.ContainerId, "/etc/os-release", dir, "os-release", ct)
                 ?? await CopyAsync(target.ContainerId, "/usr/lib/os-release", dir, "os-release2", ct);
        var (osId, osVersion) = os is null ? (null, null) : ParseOsRelease(os);

        if (await CopyAsync(target.ContainerId, "/var/lib/dpkg/status", dir, "status", ct) is { } status)
        {
            return new ImagePackages(target.ImageId, osId, osVersion, "dpkg", ParseDpkgStatus(status));
        }

        // distroless: ein Status-Schnipsel je Paket in /var/lib/dpkg/status.d/
        if (await CopyDirectoryAsync(target.ContainerId, "/var/lib/dpkg/status.d", dir, "status.d", ct) is { } parts)
        {
            return new ImagePackages(target.ImageId, osId, osVersion, "dpkg", ParseDpkgStatus(string.Join("\n\n", parts), requireInstalled: false));
        }

        if (await CopyAsync(target.ContainerId, "/lib/apk/db/installed", dir, "installed", ct) is { } apk)
        {
            return new ImagePackages(target.ImageId, osId, osVersion, "apk", ParseApkInstalled(apk));
        }

        return new ImagePackages(target.ImageId, osId, osVersion, null, [], "No dpkg or apk package database in the image (e.g. distroless static, scratch or another distribution).");
    }

    private static async Task<string?> CopyAsync(string container, string path, string dir, string name, CancellationToken ct)
    {
        var target = Path.Combine(dir, name);
        var copy = await ProcessRunner.RunAsync("docker", ["cp", "-L", $"{container}:{path}", target], Timeout, ct);
        if (!copy.Success || !File.Exists(target) || new FileInfo(target).Length > MaxFileBytes)
        {
            return null;
        }

        return await File.ReadAllTextAsync(target, ct);
    }

    private static async Task<List<string>?> CopyDirectoryAsync(string container, string path, string dir, string name, CancellationToken ct)
    {
        var target = Path.Combine(dir, name);
        var copy = await ProcessRunner.RunAsync("docker", ["cp", $"{container}:{path}", target], Timeout, ct);
        if (!copy.Success || !Directory.Exists(target))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(target).Where(f => new FileInfo(f).Length <= MaxFileBytes).Take(MaxPackages).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var file in files)
        {
            texts.Add(await File.ReadAllTextAsync(file, ct));
        }

        return texts;
    }

    /// <summary>ID und VERSION_ID aus /etc/os-release (Werte ggf. in Anführungszeichen).</summary>
    public static (string? Id, string? VersionId) ParseOsRelease(string text)
    {
        string? id = null, version = null;
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            switch (line[..eq].Trim())
            {
                case "ID":
                    id = value;
                    break;
                case "VERSION_ID":
                    version = value;
                    break;
            }
        }

        return (id, version);
    }

    /// <summary>dpkg-Statusdatei: nur installierte Pakete; "Source: name (version)" ergibt Quellpaket und -version.</summary>
    public static List<ImagePackage> ParseDpkgStatus(string text, bool requireInstalled = true)
    {
        var result = new List<ImagePackage>();
        foreach (var stanza in Stanzas(text))
        {
            var name = stanza.GetValueOrDefault("Package");
            var version = stanza.GetValueOrDefault("Version");
            var status = stanza.GetValueOrDefault("Status");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) || requireInstalled && status?.EndsWith(" installed", StringComparison.Ordinal) != true)
            {
                continue;
            }

            string? source = null, sourceVersion = null;
            if (stanza.GetValueOrDefault("Source") is { Length: > 0 } src)
            {
                var paren = src.IndexOf('(');
                source = (paren < 0 ? src : src[..paren]).Trim();
                sourceVersion = paren < 0 ? null : src[(paren + 1)..].TrimEnd(')', ' ').Trim();
            }

            result.Add(new ImagePackage(name, version, source == name ? null : source, sourceVersion));
            if (result.Count >= MaxPackages)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>apk-Datenbank: "P:" Name, "V:" Version, "o:" Ursprungspaket.</summary>
    public static List<ImagePackage> ParseApkInstalled(string text)
    {
        var result = new List<ImagePackage>();
        string? name = null, version = null, origin = null;

        void Flush()
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version) && result.Count < MaxPackages)
            {
                result.Add(new ImagePackage(name, version, origin == name ? null : origin));
            }

            name = version = origin = null;
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                Flush();
                continue;
            }

            if (raw.Length > 2 && raw[1] == ':')
            {
                var value = raw[2..].Trim();
                switch (raw[0])
                {
                    case 'P':
                        name = value;
                        break;
                    case 'V':
                        version = value;
                        break;
                    case 'o':
                        origin = value;
                        break;
                }
            }
        }

        Flush();
        return result;
    }

    /// <summary>Debian-Kontrolldatei: Absätze aus "Feld: Wert" (Fortsetzungszeilen beginnen mit Leerzeichen).</summary>
    private static IEnumerable<Dictionary<string, string>> Stanzas(string text)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return current;
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                continue; // Fortsetzung (Beschreibung, Conffiles): brauchen wir nicht
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                current[line[..colon]] = line[(colon + 1)..].Trim();
            }
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}
