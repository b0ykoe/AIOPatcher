using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Scan-Intervalle: Standard für alle Hosts (Einstellungen, nur Admins ändern) und abweichende Werte je Host (Operatoren).
/// </summary>
public static class ScanSettingsEndpoints
{
    public sealed record ScanSettingsDto(int UpdateScanHours, int ImageCheckHours, IReadOnlyList<int> AllowedHours, string? UpdatedBy, DateTime? UpdatedAt,
        int AgentUpdateMinutes, IReadOnlyList<int> AllowedAgentMinutes);

    /// <param name="AgentUpdateMinutes">null = unverändert lassen.</param>
    public sealed record ScanSettingsRequest(int UpdateScanHours, int ImageCheckHours, int? AgentUpdateMinutes = null);

    /// <summary>null = Standard aus den Einstellungen.</summary>
    public sealed record HostScanRequest(int? UpdateScanHours, int? ImageCheckHours);

    public static void MapScanSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/settings/scan", (ScanSchedule schedule) => Dto(schedule.Current));
        api.MapPut("/settings/scan", SaveAsync).RequireAuthorization(UserAccounts.AdminPolicy);
        api.MapPut("/hosts/{id:guid}/scan", SaveHostAsync).RequireAuthorization(UserAccounts.OperatorPolicy);
    }

    private static ScanSettingsDto Dto(ScanSettingsRecord s) =>
        new(s.UpdateScanHours, s.ImageCheckHours, ScanSchedule.AllowedHours, s.UpdatedBy, s.UpdatedAt, s.AgentUpdateMinutes, ScanSchedule.AllowedAgentMinutes);

    private static Dictionary<string, string[]> Validate(int? updateScanHours, int? imageCheckHours)
    {
        var errors = new Dictionary<string, string[]>();
        var allowed = string.Join(", ", ScanSchedule.AllowedHours);
        if (!ScanSchedule.IsAllowed(updateScanHours))
        {
            errors["updateScanHours"] = [L.T($"Erlaubt sind {allowed} Stunden.", $"Allowed are {allowed} hours.")];
        }

        if (!ScanSchedule.IsAllowed(imageCheckHours))
        {
            errors["imageCheckHours"] = [L.T($"Erlaubt sind {allowed} Stunden.", $"Allowed are {allowed} hours.")];
        }

        return errors;
    }

    private static async Task<IResult> SaveAsync(ScanSettingsRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, ScanSchedule schedule, CancellationToken ct)
    {
        var errors = Validate(request.UpdateScanHours, request.ImageCheckHours);
        if (request.AgentUpdateMinutes is { } minutes && !ScanSchedule.IsAllowedAgentMinutes(minutes))
        {
            var allowed = string.Join(", ", ScanSchedule.AllowedAgentMinutes);
            errors["agentUpdateMinutes"] = [L.T($"Erlaubt sind {allowed} Minuten.", $"Allowed are {allowed} minutes.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var row = await db.ScanSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            db.ScanSettings.Add(row = new ScanSettingsRecord());
        }

        row.UpdateScanHours = request.UpdateScanHours;
        row.ImageCheckHours = request.ImageCheckHours;
        row.AgentUpdateMinutes = request.AgentUpdateMinutes ?? row.AgentUpdateMinutes;
        row.UpdatedBy = user.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;
        audit.Add("scan.settings", $"updates every {row.UpdateScanHours} h, images every {row.ImageCheckHours} h, agent updates every {row.AgentUpdateMinutes} min");
        await db.SaveChangesAsync(ct);
        schedule.Use(row);
        return Results.Ok(Dto(row));
    }

    private static async Task<IResult> SaveHostAsync(Guid id, HostScanRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (Validate(request.UpdateScanHours, request.ImageCheckHours) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors);
        }

        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        host.UpdateScanHours = request.UpdateScanHours;
        host.ImageCheckHours = request.ImageCheckHours;
        audit.Add("host.scan", $"{host.Name}: updates {(request.UpdateScanHours is { } u ? $"every {u} h" : "default")}, " +
            $"images {(request.ImageCheckHours is { } i ? $"every {i} h" : "default")}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
