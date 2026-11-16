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
        bool RebootPerformed,
        DateTime? NotAfter);

    public sealed record JobLogLineDto(long Id, DateTime Timestamp, string Text);

    public sealed record JobDetailDto(JobDto Job, IReadOnlyList<JobLogLineDto> Lines);


    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);
        op.MapPost("/hosts/{hostId:guid}/jobs", CreateJob);
        op.MapPost("/jobs/bulk", CreateJobsBulk);
        api.MapGet("/jobs", ListJobs);
        api.MapGet("/jobs/{id:guid}", GetJob);
        op.MapPost("/jobs/{id:guid}/cancel", CancelJob);
    }

    private static IQueryable<JobDto> Project(IQueryable<PatchJob> jobs) =>
        jobs.Select(j => new JobDto(
            j.Id, j.HostId, j.Host!.Name, j.Host.Site!.Name, j.Type, j.Scope, j.AllowReboot, j.Status,
            j.RequestedBy, j.CreatedAt, j.StartedAt, j.FinishedAt, j.Summary, j.RebootPerformed, j.NotAfter));

    private static async Task<IResult> CreateJob(Guid hostId, CreateJobRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var plan = await JobPlanner.PlanAsync(db, [hostId], request.Type, request.Scope, request.AllowReboot, $"admin:{user.Identity!.Name}", ct);
        if (plan.Skipped.Count > 0)
        {
            var skip = plan.Skipped[0];
            return skip.Reason == JobPlanner.HostNotFound
                ? Results.NotFound()
                : Results.Problem(skip.Reason, statusCode: StatusCodes.Status409Conflict);
        }

        var job = plan.Created[0];
        audit.Add("job.created", $"{job.Type} ({job.Scope}, reboot {(job.AllowReboot ? "allowed" : "no")}) for {job.Host!.Name}",siteId: job.Host.SiteId, hostId: job.HostId);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/jobs/{job.Id}", await Project(db.Jobs.Where(j => j.Id == job.Id)).SingleAsync(ct));
    }

    public sealed record BulkJobRequest(Guid[] HostIds, JobType Type, PatchScope Scope = PatchScope.Security, bool AllowReboot = false);

    public sealed record BulkJobResult(int Created, IReadOnlyList<JobPlanner.Skip> Skipped);

    private static async Task<IResult> CreateJobsBulk(BulkJobRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (request.HostIds is null || request.HostIds.Length == 0 || request.HostIds.Length > JobPlanner.MaxHostsPerRequest)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostIds"] = [L.T($"1 bis {JobPlanner.MaxHostsPerRequest} Hosts auswählen.", $"Select 1 to {JobPlanner.MaxHostsPerRequest} hosts.")] });
        }

        var plan = await JobPlanner.PlanAsync(db, request.HostIds, request.Type, request.Scope, request.AllowReboot, $"admin:{user.Identity!.Name}", ct);
        foreach (var job in plan.Created)
        {
            audit.Add("job.created", $"{job.Type} ({job.Scope}, reboot {(job.AllowReboot ? "allowed" : "no")}) for {job.Host!.Name} (bulk)", siteId: job.Host.SiteId, hostId: job.HostId);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new BulkJobResult(plan.Created.Count, plan.Skipped));
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
            return Results.Problem(L.T("Nur geplante Jobs können abgebrochen werden.", "Only queued jobs can be cancelled."),statusCode: StatusCodes.Status409Conflict);
        }

        job.Status = JobStatus.Cancelled;
        job.FinishedAt = DateTime.UtcNow;
        audit.Add("job.cancelled", $"{job.Type} for {job.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
