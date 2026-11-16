using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Patchpausen, zurückgehaltene Updates und Tags.</summary>
public static class PatchRules
{
    /// <summary>Aktive Patchpause, die für eine der Sites gilt (oder global ist). null = keine.</summary>
    public static async Task<PatchFreeze?> ActiveFreezeAsync(AppDbContext db, IReadOnlyCollection<Guid> siteIds, DateTime now, CancellationToken ct) =>
        await db.PatchFreezes
            .Where(f => f.StartsAt <= now && f.EndsAt > now && (f.SiteId == null || siteIds.Contains(f.SiteId.Value)))
            .OrderByDescending(f => f.EndsAt)
            .FirstOrDefaultAsync(ct);

    public static Task<PatchFreeze?> ActiveFreezeAsync(AppDbContext db, Guid siteId, DateTime now, CancellationToken ct) =>
        ActiveFreezeAsync(db, [siteId], now, ct);

    public static string FreezeNote(PatchFreeze freeze) =>
        L.T($"Patchpause „{freeze.Name}“ bis {freeze.EndsAt:yyyy-MM-dd HH:mm} UTC.", $"Patch freeze \"{freeze.Name}\" until {freeze.EndsAt:yyyy-MM-dd HH:mm} UTC.");

    /// <summary>Muster aller Regeln, die für den Host gelten (Host, seine Site oder global), ohne abgelaufene.</summary>
    public static async Task<List<string>> HoldsForHostAsync(AppDbContext db, ManagedHost host, DateTime now, CancellationToken ct) =>
        await db.PackageHolds
            .Where(h => (h.ExpiresAt == null || h.ExpiresAt > now)
                        && (h.HostId == host.Id || (h.HostId == null && (h.SiteId == null || h.SiteId == host.SiteId))))
            .Select(h => h.Pattern)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>KB-Nummer aus einem Windows-Update-Titel, z. B. "… (KB5034441)" → "KB5034441".</summary>
    public static string? KbOf(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"\bKB\d{6,8}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    public const int MaxTagLength = 32;

    /// <summary>"Prod, DB ,prod" → "db,prod". Erlaubt: Buchstaben, Ziffern, - _ . :</summary>
    public static string? NormalizeTags(IEnumerable<string>? tags)
    {
        var normalized = (tags ?? [])
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= MaxTagLength && t.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        return normalized.Count == 0 ? null : string.Join(',', normalized);
    }

    public static string[] SplitTags(string? tags) => string.IsNullOrEmpty(tags) ? [] : tags.Split(',');
}
