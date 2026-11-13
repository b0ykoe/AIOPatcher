using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

public static class JobEndpoints
{
    public sealed record CreateJobRequest(JobType Type, PatchScope Scope = PatchScope.Security, bool AllowReboot = false);

    public sealed record JobDto(
        Guid Id,
        Guid HostId,
        string HostName,
        string SiteName,
        JobType Type,
        PatchScope Scope,
        bool AllowReboot,
        JobStatus Status,
        string RequestedBy,
        DateTime CreatedAt,
        DateTime? StartedAt,
        DateTime? FinishedAt,
        string? Summary,
        bool RebootPerformed);

    public sealed record JobLogLineDto(long Id, DateTime Timestamp, string Text);

    public sealed record JobDetailDto(JobDto Job, IReadOnlyList<JobLogLineDto> Lines);


    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/hosts/{hostId:guid}/jobs", CreateJob);
        api.MapGet("/jobs", ListJobs);
        api.MapGet("/jobs/{id:guid}", GetJob);
        api.MapPost("/jobs/{id:guid}/cancel", CancelJob);
    }

    private static IQueryable<JobDto> Project(IQueryable<PatchJob> jobs) =>
        jobs.Select(j => new JobDto(
            j.Id, j.HostId, j.Host!.Name, j.Host.Site!.Name, j.Type, j.Scope, j.AllowReboot, j.Status,
            j.RequestedBy, j.CreatedAt, j.StartedAt, j.FinishedAt, j.Summary, j.RebootPerformed));

    private static async Task<IResult> CreateJob(Guid hostId, CreateJobRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([hostId], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        if (host.IsRevoked)
        {
            return Results.Problem("Host ist gesperrt.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == hostId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return Results.Problem("Für diesen Host ist bereits ein Job geplant oder aktiv.", statusCode: StatusCodes.Status409Conflict);
        }

        var job = new PatchJob
        {
            HostId = hostId,
            Type = request.Type,
            Scope = request.Scope,
            AllowReboot = request.Type == JobType.Reboot || request.AllowReboot,
            RequestedBy = $"admin:{user.Identity!.Name}",
        };
        db.Jobs.Add(job);
        audit.Add("job.created", $"{job.Type} ({job.Scope}, Neustart {(job.AllowReboot ? "erlaubt" : "nein")}) für {host.Name}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/jobs/{job.Id}", await Project(db.Jobs.Where(j => j.Id == job.Id)).SingleAsync(ct));
    }

    private static async Task<IResult> ListJobs(Guid? hostId, int? limit, AppDbContext db, CancellationToken ct)
    {
        var query = db.Jobs.AsQueryable();
        if (hostId is not null)
        {
            query = query.Where(j => j.HostId == hostId);
        }

        return Results.Ok(await Project(query.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500))).ToListAsync(ct));
    }

    /// <summary>Mit afterId nur neue Logzeilen (für das Live-Log im Dashboard).</summary>
    private static async Task<IResult> GetJob(Guid id, long? afterId, AppDbContext db, CancellationToken ct)
    {
        var job = await Project(db.Jobs.Where(j => j.Id == id)).SingleOrDefaultAsync(ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        var after = afterId ?? 0;
        var lines = await db.JobLogEntries
            .Where(l => l.JobId == id && l.Id > after)
            .OrderBy(l => l.Id)
            .Take(5000)
            .Select(l => new JobLogLineDto(l.Id, l.Timestamp, l.Text))
            .ToListAsync(ct);

        return Results.Ok(new JobDetailDto(job, lines));
    }

    private static async Task<IResult> CancelJob(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var job = await db.Jobs.Include(j => j.Host).SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        // Laufende Jobs lassen sich nicht abbrechen: ein halb fertiges apt/dpkg ist schlimmer als ein fertiges.
        if (job.Status != JobStatus.Queued)
        {
            return Results.Problem("Nur geplante Jobs können abgebrochen werden.", statusCode: StatusCodes.Status409Conflict);
        }

        job.Status = JobStatus.Cancelled;
        job.FinishedAt = DateTime.UtcNow;
        audit.Add("job.cancelled", $"{job.Type} für {job.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
