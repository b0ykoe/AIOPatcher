using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Sso;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// "Erste Schritte": welche Einrichtungsschritte erledigt sind. Grundlagen (Site, Host, Plan, Benachrichtigung) und
/// "Mehr entdecken" (von außen erreichbar, Passkey, Compliance-Vorlagen …). Schritte lassen sich pro Benutzer
/// überspringen; Admin-Schritte sehen nur Admins.
/// </summary>
public static class GettingStartedEndpoints
{
    public const string Basics = "basics";
    public const string Discover = "discover";

    /// <param name="Status">done, open oder skipped.</param>
    public sealed record StepDto(string Key, string Group, string Status, bool AdminOnly);

    public sealed record GettingStartedDto(IReadOnlyList<StepDto> Steps);

    /// <summary>Alle Schritte in Anzeigereihenfolge (Key, Gruppe, nur für Admins).</summary>
    public static readonly IReadOnlyList<(string Key, string Group, bool AdminOnly)> Steps =
    [
        ("site", Basics, false),
        ("host", Basics, false),
        ("plan", Basics, false),
        ("notifications", Basics, true),
        ("publicAccess", Discover, true),
        ("passkey", Discover, false),
        ("compliance", Discover, true),
        ("integrations", Discover, false),
        ("monitoring", Discover, true),
        ("backups", Discover, true),
        ("serverUpdate", Discover, true),
        ("sso", Discover, true),
    ];

    public static void MapGettingStartedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/getting-started", GetAsync).RequireAuthorization();
        var me = app.MapGroup("/api/me/getting-started").RequireAuthorization();
        me.MapPut("/{key}/skip", (string key, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) => SetSkippedAsync(key, true, principal, db, ct));
        me.MapDelete("/{key}/skip", (string key, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) => SetSkippedAsync(key, false, principal, db, ct));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal principal, AppDbContext db, FeatureFlags features, PublicAccess publicAccess, SsoService sso, CancellationToken ct)
    {
        if (await AuthEndpoints.CurrentAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await ComputeAsync(user, db, features, publicAccess.AgentUrl is not null, sso.IsConfigured, ct));
    }

    /// <summary>Status aller Schritte für diesen Benutzer (testbar ohne HTTP).</summary>
    public static async Task<GettingStartedDto> ComputeAsync(UserAccount user, AppDbContext db, FeatureFlags features, bool publicAccessConfigured, bool ssoConfigured,
        CancellationToken ct)
    {
        var admin = user.Role == UserRole.Admin;
        var skipped = Skipped(user);
        var steps = new List<StepDto>();
        foreach (var (key, group, adminOnly) in Steps)
        {
            if (adminOnly && !admin)
            {
                continue;
            }

            var done = key switch
            {
                "site" => await db.Sites.AnyAsync(ct),
                "host" => await db.Hosts.AnyAsync(h => !h.IsRevoked, ct),
                "plan" => await db.Runbooks.AnyAsync(r => r.ScheduleEnabled, ct),
                "notifications" => await db.NotificationChannels.AnyAsync(c => c.Enabled && c.Events != 0, ct),
                "publicAccess" => publicAccessConfigured,
                "passkey" => await db.Passkeys.AnyAsync(p => p.UserId == user.Id, ct),
                "compliance" => features.IsOn(Feature.ComplianceChecks) && await db.ComplianceBaselines.AnyAsync(ct),
                "integrations" => await db.Integrations.AnyAsync(i => i.Enabled, ct),
                "monitoring" => features.IsOn(Feature.MonitoringClients) && await db.MonitoringTargets.AnyAsync(ct),
                "backups" => features.IsOn(Feature.BackupJobChecks),
                "serverUpdate" => await ServerUpdateReadyAsync(db, ct),
                "sso" => ssoConfigured,
                _ => false,
            };
            steps.Add(new StepDto(key, group, done ? "done" : skipped.Contains(key) ? "skipped" : "open", adminOnly));
        }

        return new GettingStartedDto(steps);
    }

    /// <summary>Ein Host kann den AIOPatcher-Server aktualisieren (wie /api/server-info).</summary>
    private static async Task<bool> ServerUpdateReadyAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Hosts.AsNoTracking().Where(h => !h.IsRevoked && h.ReportedModules != null && h.ReportedModules.Contains(AgentModules.ServerUpdate)).ToListAsync(ct))
        .Any(h => ModuleJson.Usable(h, AgentModules.ServerUpdate) && ModuleJson.Supports(h, AgentModules.ServerUpdate, ModuleActions.Update));

    public static async Task<IResult> SetSkippedAsync(string key, bool skip, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.CurrentAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        return SetSkipped(user, key, skip) ? Results.Ok(await SaveAsync(db, ct)) : Results.BadRequest();

        static async Task<bool> SaveAsync(AppDbContext db, CancellationToken ct)
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
    }

    /// <summary>Überspringen setzen bzw. aufheben; false bei unbekanntem Schritt.</summary>
    public static bool SetSkipped(UserAccount user, string key, bool skip)
    {
        if (!Steps.Any(s => s.Key == key))
        {
            return false;
        }

        var set = Skipped(user);
        if (skip)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
        }

        user.SkippedSteps = set.Count == 0 ? null : string.Join(',', set.Order(StringComparer.Ordinal));
        return true;
    }

    private static HashSet<string> Skipped(UserAccount user) =>
        new((user.SkippedSteps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
}
