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
