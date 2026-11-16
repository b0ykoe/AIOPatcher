using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Räumt periodisch auf:
/// - Jobs, die seit Stunden "Running" sind und keine Logzeilen mehr liefern (Host weg), werden als fehlgeschlagen markiert.
/// - Logzeilen alter Jobs werden nach der Aufbewahrungsfrist gelöscht (der Job selbst bleibt für die Historie).
/// </summary>
public sealed class JobJanitor(IServiceScopeFactory scopes, AIOPatcherOptions options, ILogger<JobJanitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Job cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var staleBefore = now - StaleAfter;

        var stale = await db.Jobs
            .Where(j => j.Status == JobStatus.Running && j.StartedAt < staleBefore)
            .Where(j => !j.LogEntries.Any(l => l.Timestamp > staleBefore))
            .ToListAsync(ct);
        foreach (var job in stale)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.Summary = L.T($"Zeitüberschreitung: seit {StaleAfter.TotalHours:0} Stunden kein Lebenszeichen vom Agent.",
                $"Timed out: no sign of life from the agent for {StaleAfter.TotalHours:0} hours.");
        }

        await db.SaveChangesAsync(ct);

        var retentionBefore = now.AddDays(-options.JobLogRetentionDays);
        var deleted = await db.JobLogEntries
            .Where(l => l.Job!.FinishedAt != null && l.Job.FinishedAt < retentionBefore)
            .ExecuteDeleteAsync(ct);

        if (stale.Count > 0 || deleted > 0)
        {
            logger.LogInformation("Cleaned up: {Stale} stale jobs, {Lines} old log lines", stale.Count, deleted);
        }
    }
}
