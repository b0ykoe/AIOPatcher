using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Modules;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Vorrat für den Compose-Editor, damit er sofort öffnet statt bis zum nächsten Abruf des Agents zu warten. Die Inventur
/// bringt je Datei einen Hash; nur bei neuem Hash holt der Server den Inhalt (docker.composecache) und speichert ihn per
/// Data Protection verschlüsselt. Ohne Freigabe (Funktionsschalter, allowComposeEdit) wird nichts gespeichert und Vorhandenes
/// gelöscht; beim Entfernen des Hosts löscht die Datenbank mit.
/// </summary>
public static class ComposeCache
{
    public const string ProtectorPurpose = "AIOPatcher.ComposeCache.v1";

    /// <summary>Stacks, deren Inhalt der Agent beim nächsten Abruf schicken soll (je Host).</summary>
    private static readonly ConcurrentDictionary<Guid, List<string>> Pending = new();

    public static IDataProtector Protector(IDataProtectionProvider provider) => provider.CreateProtector(ProtectorPurpose);

    public static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Hashes aus der Inventur übernehmen. null (Host erlaubt es nicht) oder Schalter aus: alles dieses Hosts löschen.
    /// Rückgabe: Stacks, deren Inhalt fehlt oder veraltet ist (werden beim nächsten Abruf angefordert).
    /// </summary>
    public static async Task<List<string>> SyncAsync(AppDbContext db, ManagedHost host, IReadOnlyList<ComposeFileHash>? reported, bool featureOn, DateTime now,
        CancellationToken ct)
    {
        var rows = await db.ComposeCache.Where(c => c.HostId == host.Id).ToListAsync(ct);
        if (reported is null || !featureOn)
        {
            db.ComposeCache.RemoveRange(rows);
            Pending.TryRemove(host.Id, out _);
            return [];
        }

        var byProject = reported.Where(r => ContainerOps.IsValidProjectName(r.Project) && r.Sha256.Length == 64)
            .DistinctBy(r => r.Project).ToDictionary(r => r.Project);
        db.ComposeCache.RemoveRange(rows.Where(r => !byProject.ContainsKey(r.Project)));

        var stale = new List<string>();
        foreach (var (project, hash) in byProject)
        {
            var row = rows.FirstOrDefault(r => r.Project == project);
            if (row is null)
            {
                db.ComposeCache.Add(row = new ComposeCacheEntry { HostId = host.Id, Project = project, Path = Cut(hash.Path, 1000), ReportedSha256 = hash.Sha256 });
            }

            row.Path = Cut(hash.Path, 1000);
            row.ReportedSha256 = hash.Sha256;
            row.ReportedAt = now;
            if (row.Sha256 != hash.Sha256)
            {
                stale.Add(project);
            }
        }

        if (stale.Count > 0)
        {
            Pending[host.Id] = stale.Take(ComposeCacheRequest.MaxFiles).ToList();
        }
        else
        {
            Pending.TryRemove(host.Id, out _);
        }

        return stale;
    }

    /// <summary>Auftrag für den nächsten Abruf, falls Inhalte fehlen (nur Agents mit docker.composecache).</summary>
    public static ComposeCacheRequest? TakeRequest(ManagedHost host) =>
        ModuleJson.Supports(host, AgentModules.Docker, ModuleActions.ComposeCache) && Pending.TryRemove(host.Id, out var projects) && projects.Count > 0
            ? new ComposeCacheRequest(projects)
            : null;

    /// <summary>Inhalte verschlüsselt speichern; nur für schon gemeldete Stacks und nur, wenn der Inhalt zu seinem Hash passt.</summary>
    public static async Task<int> StoreAsync(AppDbContext db, ManagedHost host, IEnumerable<ComposeFileResult> files, IDataProtector protector, DateTime now,
        CancellationToken ct)
    {
        var rows = await db.ComposeCache.Where(c => c.HostId == host.Id).ToListAsync(ct);
        var stored = 0;
        foreach (var file in files)
        {
            if (rows.FirstOrDefault(r => r.Project == file.Project) is not { } row || Encoding.UTF8.GetByteCount(file.Content) > ContainerOps.MaxComposeBytes)
            {
                continue;
            }

            var sha = Sha256(file.Content);
            if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            row.Sha256 = sha;
            row.Path = Cut(file.Path, 1000);
            row.ProtectedContent = protector.Protect(file.Content);
            row.CachedAt = now;
            stored++;
        }

        return stored;
    }

    /// <summary>Gespeicherter Stand eines Stacks (entschlüsselt); null, wenn keiner da ist oder sich nicht entschlüsseln lässt.</summary>
    public static async Task<(ComposeFileResult File, ComposeCacheEntry Entry)?> ReadAsync(AppDbContext db, Guid hostId, string project, IDataProtector protector,
        CancellationToken ct)
    {
        var row = await db.ComposeCache.AsNoTracking().FirstOrDefaultAsync(c => c.HostId == hostId && c.Project == project, ct);
        if (row is not { ProtectedContent: { } secret, Sha256: { } sha })
        {
            return null;
        }

        try
        {
            return (new ComposeFileResult(row.Project, row.Path, protector.Unprotect(secret), sha), row);
        }
        catch (CryptographicException)
        {
            return null; // Schlüssel gewechselt: beim nächsten Hash neu holen
        }
    }

    /// <summary>Host erlaubt Compose nicht mehr (gemeldete Policy): Vorrat sofort löschen.</summary>
    public static async Task ForgetIfNotAllowedAsync(AppDbContext db, ManagedHost host, CancellationToken ct)
    {
        var policy = PolicyReport.Parse(host.ReportedPolicy);
        if (policy?.AllowContainerManagement != true || policy.AllowComposeEdit != true)
        {
            await ForgetAsync(db, host.Id, ct);
        }
    }

    public static async Task ForgetAsync(AppDbContext db, Guid? hostId, CancellationToken ct)
    {
        db.ComposeCache.RemoveRange(await db.ComposeCache.Where(c => hostId == null || c.HostId == hostId).ToListAsync(ct));
        if (hostId is { } id)
        {
            Pending.TryRemove(id, out _);
        }
        else
        {
            Pending.Clear();
        }
    }

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
}
