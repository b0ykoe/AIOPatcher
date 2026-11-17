using System.Collections.Concurrent;
using System.Security.Cryptography;
using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Liefert die Agent-Binaries aus, die im Image unter /app/agents/&lt;rid&gt;/ liegen.
/// Eine Datei unter &lt;DataDirectory&gt;/agents/&lt;rid&gt;/ hat Vorrang (zum Testen oder für eigene Builds).
/// </summary>
public sealed class AgentDownloads(AIOPatcherOptions options, AIOPatcher.Server.Security.UpdateSigningKey? signingKey = null)
{
    public static readonly string[] RuntimeIds = ["linux-x64", "linux-arm64", "win-x64"];

    private readonly ConcurrentDictionary<string, (DateTime Modified, long Length, string Sha256)> _hashes = new();
    private readonly ConcurrentDictionary<string, string> _serverSignatures = new();

    public static string FileName(string rid) => rid.StartsWith("win", StringComparison.Ordinal) ? "aiopatcher-agent.exe" : "aiopatcher-agent";

    public FileInfo? Find(string rid)
    {
        if (!RuntimeIds.Contains(rid))
        {
            return null; // Whitelist: keine Pfade aus der URL ins Dateisystem
        }

        foreach (var root in new[] { Path.Combine(options.DataDirectory, "agents"), Path.Combine(AppContext.BaseDirectory, "agents") })
        {
            var file = new FileInfo(Path.Combine(root, rid, FileName(rid)));
            if (file.Exists)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Installationsskripte aus AgentAssets (feste Namen, keine Pfade aus der URL).</summary>
    public static readonly string[] ScriptNames = ["install.sh", "install.ps1"];

    public static FileInfo? FindScript(string name) =>
        ScriptNames.Contains(name) && new FileInfo(Path.Combine(AppContext.BaseDirectory, "AgentAssets", name)) is { Exists: true } file
            ? file
            : null;

    /// <summary>Version der ausgelieferten Agents: dieselbe wie der Server (gemeinsamer Build).</summary>
    public static string AgentVersion { get; } = (typeof(AgentDownloads).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string? RuntimeId(AIOPatcher.Contracts.OsFamily os, string? architecture) => (os, architecture) switch
    {
        (AIOPatcher.Contracts.OsFamily.Linux, "x64") => "linux-x64",
        (AIOPatcher.Contracts.OsFamily.Linux, "arm64") => "linux-arm64",
        (AIOPatcher.Contracts.OsFamily.Windows, "x64") => "win-x64",
        _ => null,
    };

    /// <summary>
    /// Update-Angebot, wenn der Host eine ältere Version hat. Signatur: "&lt;binary&gt;.sig" (Release-Schlüssel, Base64)
    /// hat Vorrang, sonst signiert der Server selbst (falls ein Update-Schlüssel da ist). Unsigniert wird nie angeboten.
    /// blockedVersion: Diese Version ist auf dem Host schon gescheitert (Rollback), nicht erneut anbieten.
    /// </summary>
    public AIOPatcher.Contracts.AgentUpdateOffer? OfferFor(AIOPatcher.Contracts.OsFamily os, string? architecture, string? hostVersion, string? blockedVersion = null)
    {
        if (RuntimeId(os, architecture) is not { } rid || !IsNewer(AgentVersion, hostVersion) || Find(rid) is not { } file
            || string.Equals(blockedVersion, AgentVersion, StringComparison.Ordinal))
        {
            return null;
        }

        var sha256 = Sha256(file);
        var release = new FileInfo(file.FullName + ".sig");
        if (release.Exists)
        {
            return new AIOPatcher.Contracts.AgentUpdateOffer(AgentVersion, $"/agent/download/{rid}", sha256, File.ReadAllText(release.FullName).Trim());
        }

        if (signingKey is null)
        {
            return null;
        }

        var signature = _serverSignatures.GetOrAdd(sha256, signingKey.SignSha256Hex);
        return new AIOPatcher.Contracts.AgentUpdateOffer(AgentVersion, $"/agent/download/{rid}", sha256, signature, AIOPatcher.Contracts.UpdateSigners.Server);
    }

    /// <summary>Wie Updates für diese Binaries signiert werden: "release", "server" oder null (nicht angeboten).</summary>
    public string? SignerFor(string rid) =>
        Find(rid) is not { } file ? null
        : File.Exists(file.FullName + ".sig") ? AIOPatcher.Contracts.UpdateSigners.Release
        : signingKey is not null ? AIOPatcher.Contracts.UpdateSigners.Server
        : null;

    public static bool IsNewer(string available, string? installed) =>
        Version.TryParse(available.Split('-')[0], out var a) && (installed is null || !Version.TryParse(installed.Split('+')[0].Split('-')[0], out var i) || a > i);

    public string Sha256(FileInfo file)
    {
        var key = file.FullName;
        if (_hashes.TryGetValue(key, out var cached) && cached.Modified == file.LastWriteTimeUtc && cached.Length == file.Length)
        {
            return cached.Sha256;
        }

        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashes[key] = (file.LastWriteTimeUtc, file.Length, hash);
        return hash;
    }
}
