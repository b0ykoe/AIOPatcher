using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Auto-Patch: Legt einmal pro Wartungsfenster einen Patch-Job an und verwirft geplante Auto-Jobs,
/// deren Fenster abgelaufen ist (z. B. weil der Host offline war). Außerhalb des Fensters wird nie
/// ein Auto-Job vergeben.
/// </summary>
public sealed class AutoPatchScheduler(IServiceScopeFactory scopes, ServerClock clock, ILogger<AutoPatchScheduler> logger) : BackgroundService
{
    public const string Actor = "scheduler";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>So alt darf der letzte Scan sein, damit "nichts zu tun" ohne Job übersprungen wird.</summary>
    private static readonly TimeSpan FreshScan = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RunOnceAsync(db, clock.Zone, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Auto-patch scheduler failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public sealed record RunResult(int Created, int Skipped, int Expired);

    public static async Task<RunResult> RunOnceAsync(AppDbContext db, TimeZoneInfo zone, DateTime nowUtc, CancellationToken ct)
    {
        int created = 0, skipped = 0;

        var hosts = await db.Hosts
            .Where(h => h.AutoPatchEnabled && !h.IsRevoked && h.MaintenanceDays != MaintenanceDays.None)
            .ToListAsync(ct);

        // Hosts in einem geplanten Ablauf werden vom Ablauf gepatcht, nicht zusätzlich vom Host-Auto-Patch.
        var managedByRunbook = (await db.RunbookWaveHosts
                .Where(m => m.Wave!.Runbook!.ScheduleEnabled)
                .Select(m => m.HostId)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var host in hosts)
        {
            if (managedByRunbook.Contains(host.Id))
            {
                continue;
            }

            if (MaintenanceWindow.Current(host.MaintenanceDays, host.MaintenanceStart, host.MaintenanceDurationMinutes, nowUtc, zone) is not { } window
                || host.LastAutoPatchWindowStart == window.StartUtc)
            {
                continue; // kein Fenster offen oder dieses Fenster schon behandelt
            }

            // Pro Fenster genau eine Entscheidung, egal wie sie ausfällt.
            host.LastAutoPatchWindowStart = window.StartUtc;

            if (await PatchRules.ActiveFreezeAsync(db, host.SiteId, nowUtc, ct) is { } freeze)
            {
                host.LastAutoPatchNote = L.T("Übersprungen: ", "Skipped: ") + PatchRules.FreezeNote(freeze);
                AddAudit(db, host, "auto-patch.skipped", $"Patch freeze {freeze.Name}");
                skipped++;
                continue;
            }

            if (await db.Jobs.AnyAsync(j => j.HostId == host.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
            {
                host.LastAutoPatchNote = L.T("Übersprungen: Ein anderer Job war noch geplant oder aktiv.", "Skipped: another job was still queued or running.");
                AddAudit(db, host, "auto-patch.skipped", "Another job is still queued or running.");
                skipped++;
                continue;
            }

            if (await NothingToDoAsync(db, host, nowUtc, ct))
            {
                // Frischer Scan zeigt nichts Offenes, kein (erlaubter) Neustart nötig: keinen Job anlegen.
                host.LastAutoPatchNote = host.RebootRequired
                    ? L.T("Nichts zu installieren. Ein Neustart steht aus, ist für Auto-Patch aber nicht erlaubt.",
                        "Nothing to install. A reboot is pending but not allowed for auto-patch.")
                    : L.T("Nichts zu tun: keine offenen Updates laut letztem Scan.", "Nothing to do: no pending updates according to the last scan.");
                skipped++;
                continue;
            }

            host.LastAutoPatchNote = L.T("Patch-Job angelegt.", "Patch job created.");

            db.Jobs.Add(new PatchJob
            {
                HostId = host.Id,
                Type = JobType.Patch,
                Scope = host.AutoPatchScope,
                AllowReboot = host.AutoRebootAllowed,
                RequestedBy = Actor,
                CreatedAt = nowUtc,
                NotAfter = window.EndUtc,
            });
            AddAudit(db, host, "auto-patch.job-created",
                $"{host.AutoPatchScope}, reboot {(host.AutoRebootAllowed ? "allowed" : "no")}, window until {window.EndUtc:u}");
            created++;
        }

        // Geplante Auto-Jobs, deren Fenster vorbei ist, nicht mehr ausführen.
        var expired = await db.Jobs
            .Include(j => j.Host)
            .Where(j => j.Status == JobStatus.Queued && j.NotAfter != null && j.NotAfter <= nowUtc)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            ExpireJob(job, nowUtc);
            if (job.RequestedBy == Actor)
            {
                job.Host!.LastAutoPatchNote = L.T("Verpasst: Der Host hat sich im Fenster nicht gemeldet.", "Missed: the host did not check in during the window.");
            }

            AddAudit(db, job.Host!, "auto-patch.missed", "Host did not check in during the maintenance window.");
        }

        await db.SaveChangesAsync(ct);
        return new RunResult(created, skipped, expired.Count);
    }

    public static void ExpireJob(PatchJob job, DateTime nowUtc)
    {
        job.Status = JobStatus.Cancelled;
        job.FinishedAt = nowUtc;
        job.Summary = L.T("Wartungsfenster verpasst: Der Host hat sich im Fenster nicht gemeldet. Nächster Versuch im nächsten Fenster.",
            "Maintenance window missed: the host did not check in during the window. Next attempt in the next window.");
    }

    private static async Task<bool> NothingToDoAsync(AppDbContext db, ManagedHost host, DateTime nowUtc, CancellationToken ct)
    {
        if (host.LastScanAt is null || nowUtc - host.LastScanAt > FreshScan)
        {
            return false; // Stand unbekannt: lieber patchen (der Agent prüft selbst)
        }

        if (host.RebootRequired && host.AutoRebootAllowed)
        {
            return false; // ausstehender Neustart soll im Fenster passieren
        }

        var pending = db.PendingUpdates.Where(u => u.HostId == host.Id);
        if (host.AutoPatchScope == PatchScope.Security)
        {
            pending = pending.Where(u => u.IsSecurity);
        }

        return !await pending.AnyAsync(ct);
    }

    private static void AddAudit(AppDbContext db, ManagedHost host, string action, string details) =>
        db.AuditEntries.Add(new AuditEntry { Actor = Actor, Action = action, Details = $"{host.Name}: {details}", SiteId = host.SiteId, HostId = host.Id });
}
