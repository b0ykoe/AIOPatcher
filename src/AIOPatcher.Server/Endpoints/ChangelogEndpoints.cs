using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
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
        DateTime? FetchedAt = null);

    public static void MapChangelogEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api").RequireAuthorization().MapGet("/hosts/{id:guid}/updates/changelog", GetAsync);

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
        if (changelogs.Get(name, version, now) is { } entry)
        {
            return Results.Ok(entry.Text is { } text
                ? new ChangelogDto("ready", name, update.CurrentVersion, version, UpdateChangelogs.Since(text, update.CurrentVersion), FetchedAt: entry.FetchedAt)
                : new ChangelogDto("unavailable", name, update.CurrentVersion, version, Error: entry.Error, FetchedAt: entry.FetchedAt));
        }

        if (!ModuleJson.Supports(host, AgentModules.Updates, ModuleActions.Changelog))
        {
            return Results.Ok(new ChangelogDto("unsupported", name, update.CurrentVersion, version, Reason: "agent"));
        }

        changelogs.Request(host.Id, [(name, version)], now);
        return Results.Ok(new ChangelogDto("pending", name, update.CurrentVersion, version));
    }
}
