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
        int AgentUpdateMinutes, IReadOnlyList<int> AllowedAgentMinutes, IReadOnlyList<TimingDto>? Timings = null);

    /// <summary>Weiteres Intervall (siehe <see cref="Services.Timings"/>): eingestellt, Standard, wählbare Werte in Minuten.</summary>
    public sealed record TimingDto(string Key, int Minutes, int DefaultMinutes, IReadOnlyList<int> Allowed);

    public sealed record TimingRequest(string Key, int Minutes);

    /// <param name="AgentUpdateMinutes">null = unverändert lassen.</param>
    public sealed record ScanSettingsRequest(int UpdateScanHours, int ImageCheckHours, int? AgentUpdateMinutes = null);

    /// <summary>null = Standard aus den Einstellungen.</summary>
    public sealed record HostScanRequest(int? UpdateScanHours, int? ImageCheckHours);

    public static void MapScanSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/settings/scan", (ScanSchedule schedule) => Dto(schedule.Current));
        api.MapPut("/settings/scan", SaveAsync).RequireAuthorization(UserAccounts.AdminPolicy);
        api.MapPut("/settings/scan/timings", SaveTimingAsync).RequireAuthorization(UserAccounts.AdminPolicy);
        api.MapPut("/hosts/{id:guid}/scan", SaveHostAsync).RequireAuthorization(UserAccounts.OperatorPolicy);
    }

    private static ScanSettingsDto Dto(ScanSettingsRecord s)
    {
        var overrides = Timings.Parse(s.TimingsJson);
        var timings = Timings.All
            .Select(d => new TimingDto(d.Key, overrides.TryGetValue(d.Key, out var m) ? m : d.DefaultMinutes, d.DefaultMinutes, d.Allowed))
            .ToList();
        return new(s.UpdateScanHours, s.ImageCheckHours, ScanSchedule.AllowedHours, s.UpdatedBy, s.UpdatedAt, s.AgentUpdateMinutes, ScanSchedule.AllowedAgentMinutes, timings);
    }

    /// <summary>Ein weiteres Intervall setzen (nur wählbare Werte; der Standard entfernt die Abweichung).</summary>
    public static async Task<IResult> SaveTimingAsync(TimingRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, ScanSchedule schedule, CancellationToken ct)
    {
        if (Timings.Find(request.Key) is not { } definition)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["key"] = [L.T("Unbekanntes Intervall.", "Unknown interval.")] });
        }

        if (!definition.Allowed.Contains(request.Minutes))
        {
            var allowed = string.Join(", ", definition.Allowed);
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["minutes"] = [L.T($"Erlaubt sind {allowed} Minuten.", $"Allowed are {allowed} minutes.")] });
        }

        var row = await db.ScanSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            db.ScanSettings.Add(row = new ScanSettingsRecord());
        }

        row.TimingsJson = Timings.With(row.TimingsJson, request.Key, request.Minutes);
        row.UpdatedBy = user.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;
        audit.Add("scan.timing", $"{request.Key} every {request.Minutes} min{(request.Minutes == definition.DefaultMinutes ? " (default)" : "")}");
        await db.SaveChangesAsync(ct);
        schedule.Use(row);
        return Results.Ok(Dto(row));
    }

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
