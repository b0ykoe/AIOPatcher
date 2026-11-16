using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Dashboard-API für Abläufe (Vorlagen) und Läufe (Ausführungen).</summary>
public static class RunbookEndpoints
{
    public const int MaxWaves = 20;

    public sealed record WaveHostDto(Guid Id, string Name, string SiteName);

    public sealed record RunbookWaveDto(Guid Id, int Order, string Name, PatchScope Scope, bool AllowReboot, bool RequireApproval, IReadOnlyList<WaveHostDto> Hosts,
        bool SnapshotBeforePatch, int? RequireBackupWithinHours);

    public sealed record ScheduleDto(bool Enabled, int Days, TimeOnly Start, int DurationMinutes, DateTime? NextWindowStart, DateTime? NextWindowEnd, DateTime? LastWindowStart, string? LastWindowNote);

    public sealed record RunSummaryDto(
        Guid Id,
        Guid RunbookId,
        string RunbookName,
        RunStatus Status,
        string RequestedBy,
        DateTime CreatedAt,
        DateTime? FinishedAt,
        string? Message,
        int Total,
        int Succeeded,
        int Failed,
        int Skipped,
        int CurrentWave,
        string? CurrentWaveName,
        DateTime? NotAfter);

    public sealed record RunbookDto(
        Guid Id,
        string Name,
        string? Description,
        RunbookFailurePolicy FailurePolicy,
        ScheduleDto Schedule,
        IReadOnlyList<RunbookWaveDto> Waves,
        RunSummaryDto? LastRun);

    public sealed record RunItemDto(long Id, Guid? HostId, string HostName, RunItemStatus Status, Guid? JobId, string? Message, DateTime? StartedAt, DateTime? FinishedAt);

    public sealed record RunWaveDto(int Order, string Name, PatchScope Scope, bool AllowReboot, bool RequireApproval, bool Approved, IReadOnlyList<RunItemDto> Items,
        bool SnapshotBeforePatch, int? RequireBackupWithinHours);

    public sealed record RunDetailDto(RunSummaryDto Run, IReadOnlyList<RunWaveDto> Waves);

    public sealed record WaveRequest(string Name, PatchScope Scope, bool AllowReboot, bool RequireApproval, List<Guid> HostIds,
        bool SnapshotBeforePatch = false, int? RequireBackupWithinHours = null);

    public sealed record RunbookRequest(
        string Name,
        string? Description,
        RunbookFailurePolicy FailurePolicy,
        bool ScheduleEnabled,
        int ScheduleDays,
        string ScheduleStart,
        int ScheduleDurationMinutes,
        List<WaveRequest> Waves);

    public static void MapRunbookEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);
        api.MapGet("/runbooks", ListRunbooks);
        api.MapGet("/runbooks/{id:guid}", GetRunbook);
        op.MapPost("/runbooks", CreateRunbook);
        op.MapPut("/runbooks/{id:guid}", UpdateRunbook);
        op.MapDelete("/runbooks/{id:guid}", DeleteRunbook);
        op.MapPost("/runbooks/{id:guid}/runs", StartRun);

        api.MapGet("/runbook-runs", ListRuns);
        api.MapGet("/runbook-runs/{id:guid}", GetRun);
        op.MapPost("/runbook-runs/{id:guid}/continue", (Guid id, AppDbContext db, AuditLog audit, CancellationToken ct) => Control(id, "continue", db, audit, ct));
        op.MapPost("/runbook-runs/{id:guid}/approve", (Guid id, AppDbContext db, AuditLog audit, CancellationToken ct) => Control(id, "approve", db, audit, ct));
        op.MapPost("/runbook-runs/{id:guid}/cancel", (Guid id, AppDbContext db, AuditLog audit, CancellationToken ct) => Control(id, "cancel", db, audit, ct));
    }

    // --- Abläufe --------------------------------------------------------------------------------------

    private static IQueryable<Runbook> WithDetails(AppDbContext db) =>
        db.Runbooks
            .Include(r => r.Waves).ThenInclude(w => w.Hosts).ThenInclude(h => h.Host).ThenInclude(h => h!.Site)
            .AsSplitQuery();

    private static async Task<IResult> ListRunbooks(AppDbContext db, ServerClock clock, CancellationToken ct)
    {
        var runbooks = await WithDetails(db).OrderBy(r => r.Name).ToListAsync(ct);
        var lastRuns = await LastRunsAsync(db, ct);
        return Results.Ok(runbooks.Select(r => ToDto(r, lastRuns.GetValueOrDefault(r.Id), clock)));
    }

    private static async Task<IResult> GetRunbook(Guid id, AppDbContext db, ServerClock clock, CancellationToken ct)
    {
        var runbook = await WithDetails(db).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (runbook is null)
        {
            return Results.NotFound();
        }

        var lastRuns = await LastRunsAsync(db, ct);
        return Results.Ok(ToDto(runbook, lastRuns.GetValueOrDefault(runbook.Id), clock));
    }

    private static async Task<IResult> CreateRunbook(RunbookRequest request, AppDbContext db, ServerClock clock, AuditLog audit, CancellationToken ct)
    {
        var runbook = new Runbook { Name = "" };
        if (await ApplyAsync(runbook, request, db, ct) is { } problem)
        {
            return problem;
        }

        db.Runbooks.Add(runbook);
        audit.Add("runbook.created", runbook.Name);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/runbooks/{runbook.Id}", ToDto(await WithDetails(db).SingleAsync(r => r.Id == runbook.Id, ct), null, clock));
    }

    private static async Task<IResult> UpdateRunbook(Guid id, RunbookRequest request, AppDbContext db, ServerClock clock, AuditLog audit, CancellationToken ct)
    {
        var runbook = await db.Runbooks.Include(r => r.Waves).ThenInclude(w => w.Hosts).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (runbook is null)
        {
            return Results.NotFound();
        }

        if (await ApplyAsync(runbook, request, db, ct) is { } problem)
        {
            return problem;
        }

        audit.Add("runbook.updated", runbook.Name);
        await db.SaveChangesAsync(ct);
        var lastRuns = await LastRunsAsync(db, ct);
        return Results.Ok(ToDto(await WithDetails(db).SingleAsync(r => r.Id == id, ct), lastRuns.GetValueOrDefault(id), clock));
    }

    private static async Task<IResult> DeleteRunbook(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var runbook = await db.Runbooks.FindAsync([id], ct);
        if (runbook is null)
        {
            return Results.NotFound();
        }

        if (await db.RunbookRuns.AnyAsync(r => r.RunbookId == id && (r.Status == RunStatus.Running || r.Status == RunStatus.WaitingApproval || r.Status == RunStatus.Paused), ct))
        {
            return Results.Problem(L.T("Der Ablauf hat einen aktiven Lauf. Erst abbrechen.", "The runbook has an active run. Cancel it first."),statusCode: StatusCodes.Status409Conflict);
        }

        db.Runbooks.Remove(runbook);
        audit.Add("runbook.deleted", runbook.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Prüft die Eingabe und übernimmt sie. Wellen werden komplett ersetzt (laufende Läufe nutzen ihre eigene Kopie).</summary>
    private static async Task<IResult?> ApplyAsync(Runbook runbook, RunbookRequest request, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }
        else if (await db.Runbooks.AnyAsync(r => r.Name == name && r.Id != runbook.Id, ct))
        {
            errors["name"] = [L.T($"Ablauf „{name}“ existiert bereits.", $"Runbook \"{name}\" already exists.")];
        }

        var schedule = new AutoPatchRequest(request.ScheduleEnabled, PatchScope.Security, false, request.ScheduleDays, request.ScheduleStart, request.ScheduleDurationMinutes);
        foreach (var (key, value) in AutoPatchSettings.Validate(schedule, out _))
        {
            errors["schedule" + char.ToUpperInvariant(key[0]) + key[1..]] = value;
        }

        var waves = request.Waves ?? [];
        if (waves.Count is < 1 or > MaxWaves)
        {
            errors["waves"] = [L.T($"1 bis {MaxWaves} Wellen anlegen.", $"Create 1 to {MaxWaves} waves.")];
        }

        var allHostIds = waves.SelectMany(w => w.HostIds ?? []).ToList();
        if (allHostIds.Count != allHostIds.Distinct().Count())
        {
            errors["waves"] = [L.T("Jeder Host darf nur in einer Welle vorkommen.", "Each host may only appear in one wave.")];
        }

        var known = (await db.Hosts.Select(h => h.Id).ToListAsync(ct)).ToHashSet();
        if (allHostIds.Any(id => !known.Contains(id)))
        {
            errors["waves"] = [L.T("Unbekannter Host in einer Welle.", "Unknown host in a wave.")];
        }

        if (waves.Any(w => string.IsNullOrWhiteSpace(w.Name) || w.Name.Trim().Length > 100))
        {
            errors["waves"] = [L.T("Jede Welle braucht einen Namen (bis 100 Zeichen).", "Each wave needs a name (up to 100 characters).")];
        }

        if (waves.Any(w => w.RequireBackupWithinHours is < 1 or > 24 * 31))
        {
            errors["waves"] = [L.T("Backup-Alter: 1 bis 744 Stunden.", "Backup age: 1 to 744 hours.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        TimeOnly.TryParseExact(request.ScheduleStart, ["HH:mm", "HH:mm:ss"], out var start);
        runbook.Name = name;
        runbook.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        runbook.FailurePolicy = request.FailurePolicy;
        runbook.ScheduleEnabled = request.ScheduleEnabled;
        runbook.ScheduleDays = (MaintenanceDays)request.ScheduleDays;
        runbook.ScheduleStart = start;
        runbook.ScheduleDurationMinutes = request.ScheduleDurationMinutes;

        db.RunbookWaves.RemoveRange(runbook.Waves);
        runbook.Waves = waves.Select((w, index) => new RunbookWave
        {
            Order = index + 1,
            Name = w.Name.Trim(),
            Scope = w.Scope,
            AllowReboot = w.AllowReboot,
            RequireApproval = w.RequireApproval,
            SnapshotBeforePatch = w.SnapshotBeforePatch,
            RequireBackupWithinHours = w.RequireBackupWithinHours,
            Hosts = (w.HostIds ?? []).Select(id => new RunbookWaveHost { HostId = id }).ToList(),
        }).ToList();
        return null;
    }

    private static RunbookDto ToDto(Runbook r, RunSummaryDto? lastRun, ServerClock clock)
    {
        var next = r.ScheduleEnabled ? MaintenanceWindow.Next(r.ScheduleDays, r.ScheduleStart, r.ScheduleDurationMinutes, DateTime.UtcNow, clock.Zone) : null;
        return new RunbookDto(
            r.Id,
            r.Name,
            r.Description,
            r.FailurePolicy,
            new ScheduleDto(r.ScheduleEnabled, (int)r.ScheduleDays, r.ScheduleStart, r.ScheduleDurationMinutes, next?.StartUtc, next?.EndUtc, r.LastWindowStart, r.LastWindowNote),
            r.Waves.OrderBy(w => w.Order).Select(w => new RunbookWaveDto(
                w.Id, w.Order, w.Name, w.Scope, w.AllowReboot, w.RequireApproval,
                w.Hosts.Select(h => new WaveHostDto(h.HostId, h.Host?.Name ?? "?", h.Host?.Site?.Name ?? "")).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                w.SnapshotBeforePatch, w.RequireBackupWithinHours)).ToList(),
            lastRun);
    }

    // --- Läufe ---------------------------------------------------------------------------------------

    private static IQueryable<RunSummaryDto> ProjectRuns(IQueryable<RunbookRun> runs) =>
        runs.Select(r => new RunSummaryDto(
            r.Id,
            r.RunbookId,
            r.Runbook!.Name,
            r.Status,
            r.RequestedBy,
            r.CreatedAt,
            r.FinishedAt,
            r.Message,
            r.Items.Count,
            r.Items.Count(i => i.Status == RunItemStatus.Succeeded),
            r.Items.Count(i => i.Status == RunItemStatus.Failed),
            r.Items.Count(i => i.Status == RunItemStatus.Skipped),
            r.CurrentWave,
            r.Waves.Where(w => w.Order == r.CurrentWave).Select(w => w.Name).FirstOrDefault(),
            r.NotAfter));

    private static async Task<Dictionary<Guid, RunSummaryDto>> LastRunsAsync(AppDbContext db, CancellationToken ct)
    {
        var latestIds = await db.RunbookRuns
            .GroupBy(r => r.RunbookId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).Select(r => r.Id).First())
            .ToListAsync(ct);
        return (await ProjectRuns(db.RunbookRuns.Where(r => latestIds.Contains(r.Id))).ToListAsync(ct)).ToDictionary(r => r.RunbookId);
    }

    private static async Task<IResult> StartRun(Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        RunbookRun run;
        try
        {
            run = await RunbookEngine.StartRunAsync(db, id, $"admin:{user.Identity!.Name}", DateTime.UtcNow, notAfter: null, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        audit.Add("runbook.run-started", run.Runbook!.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ProjectRuns(db.RunbookRuns.Where(r => r.Id == run.Id)).SingleAsync(ct));
    }

    private static async Task<IResult> ListRuns(Guid? runbookId, int? limit, AppDbContext db, CancellationToken ct)
    {
        var query = db.RunbookRuns.AsQueryable();
        if (runbookId is not null)
        {
            query = query.Where(r => r.RunbookId == runbookId);
        }

        return Results.Ok(await ProjectRuns(query.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit ?? 30, 1, 200))).ToListAsync(ct));
    }

    private static async Task<IResult> GetRun(Guid id, AppDbContext db, CancellationToken ct)
    {
        var summary = await ProjectRuns(db.RunbookRuns.Where(r => r.Id == id)).SingleOrDefaultAsync(ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        var run = await db.RunbookRuns.AsNoTracking().Include(r => r.Waves).Include(r => r.Items).AsSplitQuery().SingleAsync(r => r.Id == id, ct);
        var waves = run.Waves.OrderBy(w => w.Order).Select(w => new RunWaveDto(
            w.Order, w.Name, w.Scope, w.AllowReboot, w.RequireApproval, w.Approved,
            run.Items.Where(i => i.WaveOrder == w.Order).OrderBy(i => i.HostName, StringComparer.OrdinalIgnoreCase)
                .Select(i => new RunItemDto(i.Id, i.HostId, i.HostName, i.Status, i.JobId, i.Message, i.StartedAt, i.FinishedAt)).ToList(),
            w.SnapshotBeforePatch, w.RequireBackupWithinHours)).ToList();

        return Results.Ok(new RunDetailDto(summary, waves));
    }

    private static async Task<IResult> Control(Guid id, string action, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var run = await db.RunbookRuns.Include(r => r.Runbook).Include(r => r.Waves).Include(r => r.Items).AsSplitQuery().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return Results.NotFound();
        }

        var error = action switch
        {
            "continue" => RunbookEngine.Continue(run),
            "approve" => RunbookEngine.Approve(run),
            _ => await RunbookEngine.CancelAsync(db, run, DateTime.UtcNow, ct),
        };
        if (error is not null)
        {
            return Results.Problem(error, statusCode: StatusCodes.Status409Conflict);
        }

        audit.Add($"runbook.run-{action}", run.Runbook!.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
