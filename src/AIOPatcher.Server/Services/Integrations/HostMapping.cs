using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>
/// Zuordnung Host ↔ VM/Gast (Proxmox) bzw. Server (Hetzner Cloud). Eine VM gehört höchstens zu einem Host. Von Hand
/// gesetzte oder gelöste Zuordnungen (<see cref="ManagedHost.MappingManual"/>) lässt Auto-Map in Ruhe.
/// </summary>
public static class HostMapping
{
    public const int MaxExternalIdLength = 100;

    /// <summary>
    /// Ordnet den Host von Hand der VM zu. Ein anderer Host, der dieselbe VM hatte, verliert seine Zuordnung (als
    /// "auto", damit Auto-Map ihn später wieder bedienen darf). Gibt diesen Host zurück (oder null).
    /// </summary>
    public static async Task<ManagedHost?> AssignAsync(AppDbContext db, ManagedHost host, Guid integrationId, string externalId, CancellationToken ct)
    {
        externalId = Normalize(externalId);
        var previous = await db.Hosts
            .Where(h => h.Id != host.Id && h.IntegrationId == integrationId && h.ExternalId == externalId)
            .ToListAsync(ct);
        foreach (var other in previous)
        {
            other.IntegrationId = null;
            other.ExternalId = null;
            other.MappingManual = false;
        }

        host.IntegrationId = integrationId;
        host.ExternalId = externalId;
        host.MappingManual = true;
        return previous.FirstOrDefault();
    }

    /// <summary>Löst die Zuordnung von Hand; Auto-Map ordnet den Host danach nicht wieder zu.</summary>
    public static void Unassign(ManagedHost host)
    {
        host.IntegrationId = null;
        host.ExternalId = null;
        host.MappingManual = true;
    }

    /// <summary>Beim Löschen einer Integration: Zuordnung ganz zurücksetzen.</summary>
    public static void Reset(ManagedHost host)
    {
        host.IntegrationId = null;
        host.ExternalId = null;
        host.MappingManual = false;
    }

    public static string Normalize(string externalId)
    {
        var trimmed = externalId.Trim();
        return trimmed.Length > MaxExternalIdLength ? trimmed[..MaxExternalIdLength] : trimmed;
    }

    /// <summary>
    /// Auto-Map über den Namen (Hostname ohne Domain = VM-/Servername, Groß-/Kleinschreibung egal). Bedient nur Hosts
    /// ohne Zuordnung und ohne <see cref="ManagedHost.MappingManual"/>; VMs, die schon einem Host gehören
    /// (<paramref name="takenExternalIds"/>), und mehrdeutige Namen werden nicht vergeben. Setzt die Zuordnung an den
    /// Hosts und gibt die zugeordneten Hosts zurück.
    /// </summary>
    public static List<ManagedHost> AutoMap(Guid integrationId, IEnumerable<ManagedHost> hosts, IEnumerable<MachineInfo> machines, IEnumerable<string> takenExternalIds)
    {
        var taken = new HashSet<string>(takenExternalIds, StringComparer.Ordinal);
        var byName = machines
            .GroupBy(m => m.Name.ToLowerInvariant())
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        var mapped = new List<ManagedHost>();
        foreach (var host in hosts)
        {
            if (host.IntegrationId is not null || host.MappingManual || host.IsRevoked)
            {
                continue;
            }

            var machine = new[] { host.Name, host.Hostname, host.Hostname.Split('.')[0] }
                .Select(n => byName.GetValueOrDefault(n.ToLowerInvariant()))
                .FirstOrDefault(m => m is not null && !taken.Contains(m.ExternalId));
            if (machine is null)
            {
                continue;
            }

            host.IntegrationId = integrationId;
            host.ExternalId = Normalize(machine.ExternalId);
            host.MappingManual = false;
            taken.Add(machine.ExternalId);
            mapped.Add(host);
        }

        return mapped;
    }
}
