using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Plant Jobs für einen oder viele Hosts nach denselben Regeln: kein Job für gesperrte Hosts,
/// höchstens ein geplanter/aktiver Job pro Host. Speichert nicht selbst (Aufrufer ergänzt Audit und speichert).
/// </summary>
public static class JobPlanner
{
    public const int MaxHostsPerRequest = 500;

    public sealed record Skip(Guid HostId, string HostName, string Reason);

    public sealed record PlanResult(IReadOnlyList<PatchJob> Created, IReadOnlyList<Skip> Skipped);

    /// <summary>Grund für unbekannte Host-IDs (Aufrufer vergleichen darauf, gleiche Sprache innerhalb der Anfrage).</summary>
    public static string HostNotFound => L.T("Host existiert nicht.", "Host does not exist.");

    public static async Task<PlanResult> PlanAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> hostIds,
        JobType type,
        PatchScope scope,
        bool allowReboot,
        string requestedBy,
        CancellationToken ct)
    {
        var wanted = hostIds.ToHashSet();

        // Homelab-Größenordnung: Hosts und aktive Jobs passen bequem in den Speicher.
        var hosts = (await db.Hosts.ToListAsync(ct)).Where(h => wanted.Contains(h.Id)).ToList();
        var busy = (await db.Jobs
                .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                .Select(j => j.HostId)
                .ToListAsync(ct))
            .ToHashSet();

        var created = new List<PatchJob>();
        var skipped = new List<Skip>();

        foreach (var id in wanted.Where(id => hosts.All(h => h.Id != id)))
        {
            skipped.Add(new Skip(id, id.ToString(), HostNotFound));
        }

        foreach (var host in hosts.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (host.IsRevoked)
            {
                skipped.Add(new Skip(host.Id, host.Name, L.T("Host ist gesperrt.", "Host is revoked.")));
                continue;
            }

            if (busy.Contains(host.Id))
            {
                skipped.Add(new Skip(host.Id, host.Name, L.T("Es ist bereits ein Job geplant oder aktiv.", "A job is already queued or running.")));
                continue;
            }

            var job = new PatchJob
            {
                HostId = host.Id,
                Host = host,
                Type = type,
                Scope = scope,
                AllowReboot = type == JobType.Reboot || allowReboot,
                RequestedBy = requestedBy,
            };
            db.Jobs.Add(job);
            created.Add(job);
        }

        return new PlanResult(created, skipped);
    }
}
