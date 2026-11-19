using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services.Integrations;

namespace AIOPatcher.Server.Services.Monitoring;

/// <summary>Woher das Paket für einen Host kommt.</summary>
/// <param name="Sha512Url">Wazuh: offizielle Prüfsumme zum Abgleich.</param>
/// <param name="Fingerprint">Angepinntes TLS-Zertifikat (selbstsignierte Checkmk-Site).</param>
public sealed record PackageSource(string Url, string FileName, string? Version, string? Sha512Url = null, string? Fingerprint = null);

/// <summary>
/// Pakete für Monitoring-Clients: Der Server lädt sie (Wazuh-Paketserver bzw. Checkmk-Site), prüft sie und legt sie
/// unter &lt;DataDirectory&gt;/monitoring-packages ab. Agents laden sie dann über ihren eigenen Kanal (mTLS), brauchen
/// also keinen Internetzugang.
/// </summary>
public sealed partial class MonitoringPackages(AIOPatcherOptions options)
{
    public const long MaxSize = 300L * 1024 * 1024;
    private static readonly TimeSpan Reuse = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (string Sha256, long Size, DateTime At)> _byUrl = new();

    public string Folder => Path.Combine(options.DataDirectory, "monitoring-packages");

    /// <summary>Paketquelle für diesen Host. Wirft <see cref="IntegrationException"/> mit verständlicher Meldung.</summary>
    public static PackageSource Resolve(MonitoringTarget target, ManagedHost host)
    {
        var windows = host.OsFamily == OsFamily.Windows;
        if (target.Product == MonitoringProducts.Wazuh)
        {
            var version = MonitoringVersion.Display(target.ManagerVersion);
            if (MonitoringVersion.Parse(version) is not { } parts)
            {
                throw new IntegrationException(L.T("Für die Installation fehlt die Manager-Version im Ziel (z. B. 4.9.2).",
                    "The target needs the manager version for installing (e.g. 4.9.2)."));
            }

            if (parts[0] != 4)
            {
                throw new IntegrationException(L.T("Installieren geht bisher nur für Wazuh 4.x (die Paketquelle für 5.x ist noch nicht offiziell). Umstellen geht trotzdem.",
                    "Installing is only supported for Wazuh 4.x so far (the 5.x package source is not official yet). Configuring still works."));
            }

            var file = windows
                ? $"wazuh-agent-{version}-1.msi"
                : $"wazuh-agent_{version}-1_{(host.Architecture == "arm64" ? "arm64" : "amd64")}.deb";
            var url = windows
                ? $"https://packages.wazuh.com/4.x/windows/{file}"
                : $"https://packages.wazuh.com/4.x/apt/pool/main/w/wazuh-agent/{file}";
            return new PackageSource(url, file, version, $"https://packages.wazuh.com/4.x/checksums/wazuh/{version}/{file}.sha512");
        }

        if (MonitoringService.ParseSiteUrl(target.SiteUrl) is not { } site)
        {
            throw new IntegrationException(L.T("Die Site-URL des Ziels fehlt oder ist ungültig.", "The target's site URL is missing or invalid."));
        }

        var agents = target.SiteUrl!.TrimEnd('/') + "/check_mk/agents/";
        var siteVersion = MonitoringVersion.Display(target.SiteVersion);
        if (!windows)
        {
            if (siteVersion is null)
            {
                throw new IntegrationException(L.T("Für die Installation unter Linux fehlt die Version der Site im Ziel (z. B. 2.3.0p12).",
                    "Installing on Linux needs the site version in the target (e.g. 2.3.0p12)."));
            }

            var file = $"check-mk-agent_{siteVersion}-1_all.deb";
            return new PackageSource(agents + file, file, siteVersion, Fingerprint: target.TlsFingerprintSha256);
        }

        if (WindowsBuild(host) is { } build && build < MinimumWindowsBuild(siteVersion))
        {
            if (string.IsNullOrWhiteSpace(target.LegacyPackageUrl))
            {
                throw new IntegrationException(L.T(
                    $"Der Checkmk-Agent dieser Site unterstützt {host.OsName} nicht mehr. Im Ziel eine Legacy-Paket-URL (z. B. MSI eines 2.1-Agents) eintragen.",
                    $"This site's Checkmk agent no longer supports {host.OsName}. Enter a legacy package URL in the target (e.g. the MSI of a 2.1 agent)."));
            }

            var legacy = new Uri(target.LegacyPackageUrl);
            var name = Path.GetFileName(legacy.AbsolutePath) is { } n && n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? n : "check_mk_agent_legacy.msi";
            return new PackageSource(legacy.ToString(), name, null,
                Fingerprint: string.Equals(legacy.Host, site.Host, StringComparison.OrdinalIgnoreCase) ? target.TlsFingerprintSha256 : null);
        }

        return new PackageSource(agents + "windows/check_mk_agent.msi", "check_mk_agent.msi", siteVersion, Fingerprint: target.TlsFingerprintSha256);
    }

    /// <summary>
    /// Kleinste Windows-Build-Nummer, die der Checkmk-Agent der Site laut Doku unterstützt:
    /// bis 2.1 ab 2008 R2/7 (7601), 2.2 ab 2012 (9200), ab 2.3 ab 2016 (14393). Unbekannte Version: wie neueste.
    /// </summary>
    public static int MinimumWindowsBuild(string? siteVersion) =>
        MonitoringVersion.Parse(siteVersion) is not { } v ? 14393
        : (v[0], v[1]) switch
        {
            (< 2, _) or (2, <= 1) => 7601,
            (2, 2) => 9200,
            _ => 14393,
        };

    /// <summary>Build-Nummer aus der Windows-Version des Hosts ("21H2 (Build 20348.1)", "Build 7601").</summary>
    public static int? WindowsBuild(ManagedHost host) =>
        host.OsFamily == OsFamily.Windows && host.OsVersion is { } version && BuildPattern().Match(version) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>Paket bereitstellen (aus dem Zwischenspeicher oder neu geladen und geprüft).</summary>
    public async Task<MonitoringPackage> GetAsync(MonitoringTarget target, ManagedHost host, CancellationToken ct)
    {
        var source = Resolve(target, host);
        if (_byUrl.TryGetValue(source.Url, out var cached) && DateTime.UtcNow - cached.At < Reuse && Find(cached.Sha256) is not null)
        {
            return new MonitoringPackage(cached.Sha256, source.FileName, cached.Sha256, cached.Size, source.Version);
        }

        var (sha256, size) = await DownloadAsync(source, ct);
        _byUrl[source.Url] = (sha256, size, DateTime.UtcNow);
        return new MonitoringPackage(sha256, source.FileName, sha256, size, source.Version);
    }

    /// <summary>Paketdatei zur Kennung (SHA-256, Kleinbuchstaben); andere Werte werden nie zu Pfaden.</summary>
    public FileInfo? Find(string id) =>
        Sha256Pattern().IsMatch(id) && new FileInfo(Path.Combine(Folder, id + ".pkg")) is { Exists: true } file ? file : null;

    /// <summary>Pakete löschen, die länger nicht gebraucht wurden.</summary>
    public int CleanUp(DateTime olderThan)
    {
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in new DirectoryInfo(Folder).GetFiles("*.pkg").Where(f => f.LastWriteTimeUtc < olderThan))
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // wird gerade ausgeliefert
            }
        }

        return removed;
    }

    private async Task<(string Sha256, long Size)> DownloadAsync(PackageSource source, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var temp = Path.Combine(Folder, $"download-{Guid.NewGuid():N}.tmp");
        using var client = new HttpClient(IntegrationHttp.CreateHandler(source.Fingerprint, out var presented)) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AIOPatcher/{AgentDownloads.AgentVersion}");
        try
        {
            using var response = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new IntegrationException(L.T($"Paket nicht gefunden: {source.Url} (HTTP {(int)response.StatusCode}). Version prüfen.",
                    $"Package not found: {source.Url} (HTTP {(int)response.StatusCode}). Check the version."));
            }

            if (response.Content.Headers.ContentLength > MaxSize)
            {
                throw new IntegrationException(L.T("Paket ist zu groß.", "Package is too large."));
            }

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long size = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxSize)
                    {
                        throw new IntegrationException(L.T("Paket ist zu groß.", "Package is too large."));
                    }

                    sha256.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (source.Sha512Url is { } checksumUrl)
            {
                var expected = HexPattern().Match(await client.GetStringAsync(checksumUrl, ct)) is { Success: true } m ? m.Value : null;
                if (!string.Equals(expected, Convert.ToHexString(sha512.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                {
                    throw new IntegrationException(L.T("Die Prüfsumme des Pakets stimmt nicht mit der des Herstellers überein.",
                        "The package checksum does not match the vendor's checksum."));
                }
            }

            var hash = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            File.Move(temp, Path.Combine(Folder, hash + ".pkg"), overwrite: true);
            return (hash, size);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var fp = presented();
            throw new IntegrationException(e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } && fp is not null
                ? L.T($"Zertifikat von {new Uri(source.Url).Host} nicht vertrauenswürdig. Fingerprint zum Anpinnen: {fp}",
                    $"Certificate of {new Uri(source.Url).Host} not trusted. Fingerprint to pin: {fp}")
                : L.T($"Download von {source.Url} fehlgeschlagen: {e.Message}", $"Downloading {source.Url} failed: {e.Message}"), fp);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [GeneratedRegex(@"Build (\d+)")]
    private static partial Regex BuildPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("[0-9a-fA-F]{128}")]
    private static partial Regex HexPattern();
}
