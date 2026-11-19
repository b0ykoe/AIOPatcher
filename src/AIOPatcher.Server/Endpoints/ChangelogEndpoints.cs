using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Ai;
using AIOPatcher.Server.Services.Modules;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Patchnotes eines anstehenden Updates ("i" in der Update-Liste eines Hosts).</summary>
public static class ChangelogEndpoints
{
    /// <param name="State">"ready" (Sections), "pending" (wird beim nächsten Abruf des Agents geholt), "unavailable" (Error),
    /// "unsupported" (Reason: "windows", "agent" = Agent zu alt oder Lite-Agent, "notFound" = kein solches Update).</param>
    public sealed record ChangelogDto(
        string State,
        string Name,
        string? CurrentVersion,
        string? AvailableVersion,
        IReadOnlyList<UpdateChangelogs.Section>? Sections = null,
        string? Error = null,
        string? Reason = null,
        DateTime? FetchedAt = null,
        string? Summary = null,
        string? SummaryError = null,
        string? SummaryModel = null);

    /// <summary>Ein Update in einem Patch-Job (beim Start festgehalten).</summary>
    public sealed record JobUpdateItem(string Name, string? From, string? To, bool Security);

    /// <param name="State">"planned" (Job läuft noch), "installed", "notInstalled" (steht weiter an), "unknown" (Job gescheitert).</param>
    public sealed record JobUpdateDto(string Name, string? From, string? To, bool Security, string State, bool HasChangelog, string? Summary);

    /// <param name="State">"ready", "pending" (KI arbeitet), "error", "off" (keine KI eingerichtet), "none" (keine Updates).</param>
    public sealed record HostSummaryDto(string State, string? Text, string? Error, string? Model, int UpdateCount, DateTime? CreatedAt);

    private static readonly System.Text.Json.JsonSerializerOptions SnapshotJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Anstehende Updates eines Hosts im Umfang des Jobs (Security oder alle) als JSON.</summary>
    public static async Task<string> SnapshotAsync(AppDbContext db, Guid hostId, PatchScope scope, CancellationToken ct)
    {
        var items = await db.PendingUpdates.AsNoTracking()
            .Where(u => u.HostId == hostId && (scope == PatchScope.All || u.IsSecurity))
            .OrderBy(u => u.Name)
            .Take(500)
            .Select(u => new JobUpdateItem(u.Name, u.CurrentVersion, u.AvailableVersion, u.IsSecurity))
            .ToListAsync(ct);
        return System.Text.Json.JsonSerializer.Serialize(items, SnapshotJson);
    }

    public static void MapChangelogEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/hosts/{id:guid}/updates/changelog", GetAsync);
        api.MapGet("/changelogs", GetByVersionAsync);
        api.MapGet("/jobs/{id:guid}/updates", JobUpdatesAsync);
        api.MapGet("/hosts/{id:guid}/updates/summary", HostSummaryAsync);
        api.MapPost("/hosts/{id:guid}/updates/summary", RefreshHostSummary).RequireAuthorization(UserAccounts.OperatorPolicy);
    }

    private static ChangelogDto Ready(PackageChangelogRecord entry, string name, string? from, string version) =>
        entry.Text is { } text
            ? new ChangelogDto("ready", name, from, version, UpdateChangelogs.Since(text, from), FetchedAt: entry.FetchedAt,
                // kurzer Changelog ohne KI: steht ohnehin darunter
                Summary: entry.SummaryModel == UpdateSummaries.FromChangelog ? null : entry.Summary, SummaryError: entry.SummaryError,
                SummaryModel: entry.SummaryModel)
            : new ChangelogDto("unavailable", name, from, version, Error: entry.Error, FetchedAt: entry.FetchedAt);

    /// <summary>Patchnotes nach Paket und Version (z. B. aus der Historie eines Patch-Jobs, das Update steht nicht mehr an).</summary>
    private static async Task<IResult> GetByVersionAsync(string name, string version, string? from, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await UpdateChangelogs.GetAsync(db, name, version, ct) is { } entry
            ? Ready(entry, name, from, version)
            : new ChangelogDto("unsupported", name, from, version, Reason: "notFound"));

    private static async Task<IResult> JobUpdatesAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.UpdatesSnapshot is null)
        {
            return Results.Ok(Array.Empty<JobUpdateDto>());
        }

        var items = System.Text.Json.JsonSerializer.Deserialize<List<JobUpdateItem>>(job.UpdatesSnapshot, SnapshotJson) ?? [];
        var names = items.Select(i => i.Name).ToList();
        var stillPending = (await db.PendingUpdates.AsNoTracking().Where(u => u.HostId == job.HostId && names.Contains(u.Name))
            .Select(u => new { u.Name, u.AvailableVersion }).ToListAsync(ct)).Select(u => (u.Name, u.AvailableVersion)).ToHashSet();
        var changelogs = (await db.PackageChangelogs.AsNoTracking().Where(c => names.Contains(c.Name))
            .Select(c => new { c.Name, c.Version, HasText = c.Text != null, c.Summary }).ToListAsync(ct)).ToDictionary(c => (c.Name, c.Version));

        return Results.Ok(items.Select(i =>
        {
            var state = job.Status switch
            {
                JobStatus.Queued or JobStatus.Running => "planned",
                JobStatus.Succeeded => stillPending.Contains((i.Name, i.To)) ? "notInstalled" : "installed",
                _ => stillPending.Contains((i.Name, i.To)) ? "notInstalled" : "unknown",
            };
            var c = i.To is null ? null : changelogs.GetValueOrDefault((i.Name, i.To));
            return new JobUpdateDto(i.Name, i.From, i.To, i.Security, state, c?.HasText ?? false, c?.Summary);
        }).ToList());
    }

    private static async Task<IResult> HostSummaryAsync(Guid id, AppDbContext db, UpdateSummaries summaries, CancellationToken ct)
    {
        if (!summaries.Active)
        {
            return Results.Ok(new HostSummaryDto("off", null, null, null, 0, null));
        }

        var count = await db.PendingUpdates.CountAsync(u => u.HostId == id && u.AvailableVersion != null, ct);
        if (count == 0)
        {
            return Results.Ok(new HostSummaryDto("none", null, null, null, 0, null));
        }

        var summary = await db.HostUpdateSummaries.AsNoTracking().SingleOrDefaultAsync(s => s.HostId == id, ct);
        if (summary is null)
        {
            summaries.Enqueue(id);
            return Results.Ok(new HostSummaryDto("pending", null, null, null, count, null));
        }

        // Liste geändert (Anzahl weicht ab): alte Zusammenfassung zeigen, neue wird erstellt
        var stale = summary.UpdateCount != count;
        if (stale)
        {
            summaries.Enqueue(id);
        }

        return Results.Ok(new HostSummaryDto(summary.Text is not null ? (stale ? "pending" : "ready") : "error",
            summary.Text, summary.Error, summary.Model, summary.UpdateCount, summary.CreatedAt));
    }

    /// <summary>"Neu zusammenfassen": vorhandene Zusammenfassung des Hosts verwerfen und neu erstellen lassen.</summary>
    private static async Task<IResult> RefreshHostSummary(Guid id, AppDbContext db, UpdateSummaries summaries, CancellationToken ct)
    {
        if (!summaries.Active)
        {
            return Results.Problem(L.T("Keine KI eingerichtet (Einstellungen → KI).", "No AI set up (Settings → AI)."), statusCode: StatusCodes.Status409Conflict);
        }

        await db.HostUpdateSummaries.Where(s => s.HostId == id).ExecuteDeleteAsync(ct);
        summaries.Enqueue(id);
        return Results.NoContent();
    }

    private static async Task<IResult> GetAsync(Guid id, string name, AppDbContext db, UpdateChangelogs changelogs, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var update = await db.PendingUpdates.AsNoTracking().FirstOrDefaultAsync(u => u.HostId == id && u.Name == name, ct);
        if (update?.AvailableVersion is not { } version)
        {
            return Results.Ok(new ChangelogDto("unsupported", name, update?.CurrentVersion, update?.AvailableVersion, Reason: "notFound"));
        }

        if (host.OsFamily == OsFamily.Windows)
        {
            return Results.Ok(new ChangelogDto("unsupported", name, update.CurrentVersion, version, Reason: "windows"));
        }

        var now = DateTime.UtcNow;
        if (await UpdateChangelogs.GetAsync(db, name, version, ct) is { } entry && UpdateChangelogs.IsUsable(entry, now))
        {
            return Results.Ok(Ready(entry, name, update.CurrentVersion, version));
        }

        if (!ModuleJson.Supports(host, AgentModules.Updates, ModuleActions.Changelog))
        {
            return Results.Ok(new ChangelogDto("unsupported", name, update.CurrentVersion, version, Reason: "agent"));
        }

        await changelogs.RequestAsync(db, host.Id, [(name, version)], now, ct);
        return Results.Ok(new ChangelogDto("pending", name, update.CurrentVersion, version));
    }
}
