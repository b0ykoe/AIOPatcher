using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Wartungsfenster-Aufsicht: Geplante Jobs (aus Patch-Plänen), deren Fenster abgelaufen ist (z. B. weil der Host offline
/// war), werden verworfen. Außerhalb des Fensters wird nie ein automatischer Job vergeben. Die Jobs selbst legen die
/// Patch-Pläne an (<see cref="RunbookEngine"/>); früheres Auto-Patch pro Host wird beim Start zu Plänen
/// (<see cref="PatchPlans.MigrateHostAutoPatchAsync"/>).
/// </summary>
public sealed class AutoPatchScheduler(IServiceScopeFactory scopes, ILogger<AutoPatchScheduler> logger) : BackgroundService
{
    /// <summary>Wer automatische Jobs und Läufe anlegt (Audit-Log, Jobliste).</summary>
    public const string Actor = "scheduler";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RunOnceAsync(db, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Maintenance window check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Verwirft geplante Jobs, deren Wartungsfenster vorbei ist. Rückgabe: Anzahl.</summary>
    public static async Task<int> RunOnceAsync(AppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var expired = await db.Jobs
            .Include(j => j.Host)
            .Where(j => j.Status == JobStatus.Queued && j.NotAfter != null && j.NotAfter <= nowUtc)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            ExpireJob(job, nowUtc);
            db.AuditEntries.Add(new AuditEntry
            {
                Actor = Actor, Action = "auto-patch.missed", Details = $"{job.Host!.Name}: Host did not check in during the maintenance window.",
                SiteId = job.Host.SiteId, HostId = job.HostId,
            });
        }

        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    public static void ExpireJob(PatchJob job, DateTime nowUtc)
    {
        job.Status = JobStatus.Cancelled;
        job.FinishedAt = nowUtc;
        job.Summary = L.T("Wartungsfenster verpasst: Der Host hat sich im Fenster nicht gemeldet. Nächster Versuch im nächsten Fenster.",
            "Maintenance window missed: the host did not check in during the window. Next attempt in the next window.");
    }
}
