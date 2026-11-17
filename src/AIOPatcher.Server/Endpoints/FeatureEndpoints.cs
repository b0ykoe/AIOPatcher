using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Funktionen &amp; Sicherheit: Schalter lesen (alle), ändern (nur Admins mit Browser-Login).</summary>
public static class FeatureEndpoints
{
    public sealed record FeatureDto(Feature Feature, bool Enabled, bool Default, FeatureRisk Risk, string Group, string? LockedBy, string? ChangedBy, DateTime? ChangedAt);
    public sealed record FeatureRequest(bool Enabled);

    public static void MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/features", (FeatureFlags features) => List(features)).RequireAuthorization();
        app.MapPut("/api/features/{feature}", Update)
            .RequireAuthorization(UserAccounts.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.User.HasClaim(c => c.Type == "aiopatcher:token") ? Results.Forbid() : await next(context));
    }

    public static List<FeatureDto> List(FeatureFlags features) =>
        FeatureFlags.Definitions.Select(d =>
        {
            var stored = features.Stored(d.Feature);
            return new FeatureDto(d.Feature, features.IsOn(d.Feature), d.Default, d.Risk, d.Group, features.LockReason(d.Feature), stored?.ChangedBy, stored?.ChangedAt);
        }).ToList();

    private static async Task<IResult> Update(string feature, FeatureRequest request, ClaimsPrincipal user, AppDbContext db, FeatureFlags features, AuditLog audit, CancellationToken ct)
    {
        if (!Enum.TryParse<Feature>(feature, ignoreCase: false, out var f) || !Enum.IsDefined(f))
        {
            return Results.NotFound();
        }

        if (!await features.SetAsync(db, f, request.Enabled, user.Identity?.Name ?? "?", ct))
        {
            return Results.Problem(
                L.T($"Diese Funktion ist per Server-Konfiguration gesperrt ({features.LockReason(f)}) und lässt sich hier nicht einschalten.",
                    $"This feature is locked by the server configuration ({features.LockReason(f)}) and cannot be enabled here."),
                statusCode: StatusCodes.Status409Conflict);
        }

        audit.Add("feature.changed", $"{f}: {(request.Enabled ? "on" : "off")}");
        await db.SaveChangesAsync(ct);
        return Results.Ok(List(features));
    }
}
