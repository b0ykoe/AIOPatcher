using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Wie ein Host in einen Plan kam: ausgewählt, über Tag/Site-Regel oder als "alle übrigen".</summary>
public enum PlanVia
{
    Host,
    Rule,
    Others,
}

/// <summary>Effektive Zugehörigkeit eines Hosts zu einem Patch-Plan (= Ablauf) und einer Gruppe (= Welle).</summary>
public sealed record PlanMembership(Guid PlanId, string PlanName, Guid GroupId, string GroupName, int GroupOrder, PlanVia Via);

/// <summary>
/// Patch-Pläne: Ein Host ist immer in höchstens einem Plan. Reihenfolge der Auflösung: ausgewählte Hosts (egal in welchem
/// Plan), dann Tag-/Site-Regeln, dann "alle übrigen"; innerhalb einer Stufe gewinnt der ältere Plan bzw. die frühere Gruppe.
/// </summary>
public static class PatchPlans
{
    /// <summary>So alt darf der letzte Scan sein, damit "nichts zu tun" ohne Job übersprungen wird.</summary>
    public static readonly TimeSpan FreshScan = TimeSpan.FromHours(24);

    public static async Task<Dictionary<Guid, PlanMembership>> ResolveAsync(AppDbContext db, CancellationToken ct)
    {
        var plans = await db.Runbooks.AsNoTracking()
            .Include(r => r.Waves).ThenInclude(w => w.Hosts)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Name)
            .AsSplitQuery()
            .ToListAsync(ct);
        var hosts = await db.Hosts.AsNoTracking().Where(h => !h.IsRevoked).Select(h => new HostInfo(h.Id, h.SiteId, h.Tags)).ToListAsync(ct);
        return Resolve(plans, hosts);
    }

    public sealed record HostInfo(Guid Id, Guid SiteId, string? Tags);

    public static Dictionary<Guid, PlanMembership> Resolve(IReadOnlyList<Runbook> plans, IReadOnlyList<HostInfo> hosts)
    {
        var result = new Dictionary<Guid, PlanMembership>();
        var groups = plans.SelectMany(p => p.Waves.OrderBy(w => w.Order).Select(w => (Plan: p, Group: w))).ToList();
        var active = hosts.ToDictionary(h => h.Id);

        PlanMembership Member((Runbook Plan, RunbookWave Group) g, PlanVia via) => new(g.Plan.Id, g.Plan.Name, g.Group.Id, g.Group.Name, g.Group.Order, via);

        foreach (var g in groups)
        {
            foreach (var m in g.Group.Hosts.Where(m => active.ContainsKey(m.HostId)))
            {
                result.TryAdd(m.HostId, Member(g, PlanVia.Host));
            }
        }

        foreach (var g in groups.Where(g => HasRule(g.Group)))
        {
            foreach (var host in hosts.Where(h => !result.ContainsKey(h.Id) && MatchesRule(g.Group, h)))
            {
                result[host.Id] = Member(g, PlanVia.Rule);
            }
        }

        if (groups.FirstOrDefault(g => g.Group.IncludeOthers) is { Plan: not null } others)
        {
            foreach (var host in hosts.Where(h => !result.ContainsKey(h.Id)))
            {
                result[host.Id] = Member(others, PlanVia.Others);
            }
        }

        return result;
    }

    public static bool HasRule(RunbookWave group) => !string.IsNullOrEmpty(group.MemberTags) || group.MemberSiteId is not null;

    /// <summary>Tags: einer muss passen. Site: muss passen. Beides gesetzt: beides.</summary>
    public static bool MatchesRule(RunbookWave group, HostInfo host)
    {
        if (!HasRule(group))
        {
            return false;
        }

        if (group.MemberSiteId is { } site && host.SiteId != site)
        {
            return false;
        }

        if (string.IsNullOrEmpty(group.MemberTags))
        {
            return true;
        }

        var tags = PatchRules.SplitTags(host.Tags);
        return PatchRules.SplitTags(group.MemberTags).Any(t => tags.Contains(t, StringComparer.Ordinal));
    }

    /// <summary>Frischer Scan zeigt nichts Offenes in diesem Umfang und kein (erlaubter) Neustart steht aus: kein Job nötig.</summary>
    public static async Task<bool> NothingToDoAsync(AppDbContext db, ManagedHost host, PatchScope scope, bool allowReboot, DateTime now, CancellationToken ct)
    {
        if (host.LastScanAt is null || now - host.LastScanAt > FreshScan)
        {
            return false; // Stand unbekannt: lieber patchen (der Agent prüft selbst)
        }

        if (host.RebootRequired && allowReboot)
        {
            return false; // ausstehender Neustart soll im Fenster passieren
        }

        var pending = db.PendingUpdates.Where(u => u.HostId == host.Id);
        if (scope == PatchScope.Security)
        {
            pending = pending.Where(u => u.IsSecurity);
        }

        return !await pending.AnyAsync(ct);
    }

    /// <summary>
    /// Einmalig beim Start: früheres Auto-Patch pro Host wird zu Plänen (je gleichem Zeitplan, Umfang und Neustart).
    /// Hosts, die schon in einem geplanten Ablauf waren, bleiben dort. Danach ist Auto-Patch pro Host aus.
    /// </summary>
    public static async Task<int> MigrateHostAutoPatchAsync(AppDbContext db, CancellationToken ct)
    {
        var hosts = await db.Hosts.Where(h => h.AutoPatchEnabled).ToListAsync(ct);
        if (hosts.Count == 0)
        {
            return 0;
        }

        var inPlan = (await db.RunbookWaveHosts.Select(m => m.HostId).ToListAsync(ct)).ToHashSet();
        var names = (await db.Runbooks.Select(r => r.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        foreach (var group in hosts.Where(h => !inPlan.Contains(h.Id) && !h.IsRevoked && h.MaintenanceDays != MaintenanceDays.None)
                     .GroupBy(h => (h.MaintenanceDays, h.MaintenanceStart, h.MaintenanceDurationMinutes, h.AutoPatchScope, h.AutoRebootAllowed)))
        {
            var (days, start, duration, scope, reboot) = group.Key;
            var name = UniqueName(names, $"Auto-Patch {start:HH\\:mm} ({scope})");
            names.Add(name);
            db.Runbooks.Add(new Runbook
            {
                Name = name,
                Description = L.T("Aus dem früheren Auto-Patch pro Host übernommen.", "Converted from the former per-host auto-patch."),
                FailurePolicy = RunbookFailurePolicy.FinishWaveThenPause,
                ScheduleEnabled = true,
                ScheduleDays = days,
                ScheduleStart = start,
                ScheduleDurationMinutes = duration,
                Waves =
                [
                    new RunbookWave
                    {
                        Order = 1,
                        Name = L.T("Alle", "All"),
                        Scope = scope,
                        AllowReboot = reboot,
                        Hosts = group.Select(h => new RunbookWaveHost { HostId = h.Id }).ToList(),
                    },
                ],
            });
            created++;
        }

        foreach (var host in hosts)
        {
            host.AutoPatchEnabled = false;
        }

        db.AuditEntries.Add(new AuditEntry { Actor = "system", Action = "auto-patch.migrated", Details = $"{hosts.Count} hosts, {created} plans" });
        await db.SaveChangesAsync(ct);
        return created;
    }

    private static string UniqueName(HashSet<string> names, string name)
    {
        var candidate = name;
        for (var i = 2; names.Contains(candidate); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }
}
