using System.Collections.Concurrent;
using System.Security.Cryptography;
using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Liefert die Agent-Binaries aus, die im Image unter /app/agents/&lt;rid&gt;/ liegen.
/// Eine Datei unter &lt;DataDirectory&gt;/agents/&lt;rid&gt;/ hat Vorrang (zum Testen oder für eigene Builds).
/// </summary>
public sealed class AgentDownloads(AIOPatcherOptions options)
{
    public static readonly string[] RuntimeIds = ["linux-x64", "linux-arm64", "win-x64"];

    private readonly ConcurrentDictionary<string, (DateTime Modified, long Length, string Sha256)> _hashes = new();

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
    /// Update-Angebot, wenn der Host eine ältere Version hat und für sein Binary eine Signatur
    /// (Datei "&lt;binary&gt;.sig", Base64) vorliegt. Unsignierte Builds werden nie angeboten.
    /// </summary>
    public AIOPatcher.Contracts.AgentUpdateOffer? OfferFor(AIOPatcher.Contracts.OsFamily os, string? architecture, string? hostVersion)
    {
        if (RuntimeId(os, architecture) is not { } rid || !IsNewer(AgentVersion, hostVersion) || Find(rid) is not { } file)
        {
            return null;
        }

        var signature = new FileInfo(file.FullName + ".sig");
        if (!signature.Exists)
        {
            return null;
        }

        return new AIOPatcher.Contracts.AgentUpdateOffer(AgentVersion, $"/agent/download/{rid}", Sha256(file), File.ReadAllText(signature.FullName).Trim());
    }

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
