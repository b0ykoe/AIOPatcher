using System.Security.Claims;
using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Compliance-Vorgaben (Soll-Zustand je Host): anlegen, Hosts zuordnen (ausgewählt, Tag/Site, alle übrigen), Abweichungen
/// prüfen und per Sammel-Job angleichen. Nichts passiert automatisch: Angleichen startet ein Admin, der Agent braucht
/// allowSystemChanges, und jeder Schritt braucht seinen Schalter (Benutzer, SSH/RDP, Firewall).
/// </summary>
public static class ComplianceBaselineEndpoints
{
    public sealed record BaselineMemberDto(Guid HostId, string HostName, string SiteName, OsFamily OsFamily, PlanVia Via, int Violations, int Unknown, int Fixable);

    public sealed record BaselineDto(
        Guid Id, string Name, string? Description, BaselineRules Rules, IReadOnlyList<Guid> HostIds, string? MemberTags, Guid? MemberSiteId,
        bool IncludeOthers, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<BaselineMemberDto> Members);

    public sealed record SaveBaselineRequest(string Name, string? Description, BaselineRules Rules, Guid[]? HostIds, string? MemberTags, Guid? MemberSiteId, bool IncludeOthers);

    public sealed record AlignRequest(string[]? Keys);

    public static void MapComplianceBaselineEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var admin = api.MapGroup("").RequireAuthorization(UserAccounts.AdminPolicy);
        api.MapGet("/compliance/baselines", ListAsync);
        admin.MapPost("/compliance/baselines", CreateAsync);
        admin.MapPut("/compliance/baselines/{id:guid}", UpdateAsync);
        admin.MapDelete("/compliance/baselines/{id:guid}", DeleteAsync);
        admin.MapPost("/compliance/baselines/preview", PreviewAsync);
        admin.MapPost("/compliance/baselines/{id:guid}/align", AlignBaselineAsync);
        admin.MapPost("/hosts/{id:guid}/compliance/align", AlignHostAsync);
    }

    // --- Lesen ----------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var baselines = await db.ComplianceBaselines.AsNoTracking().Include(b => b.Hosts).OrderBy(b => b.CreatedAt).ThenBy(b => b.Name).ToListAsync(ct);
        var members = await MembersAsync(db, baselines, ct);
        return Results.Ok(baselines.Select(b => ToDto(b, members.Where(m => m.BaselineId == b.Id).Select(m => m.Member).ToList())).ToList());
    }

    private static BaselineDto ToDto(ComplianceBaseline b, IReadOnlyList<BaselineMemberDto> members) =>
        new(b.Id, b.Name, b.Description, ComplianceBaselines.Parse(b.RulesJson), b.Hosts.Select(h => h.HostId).ToList(), b.MemberTags, b.MemberSiteId,
            b.IncludeOthers, b.CreatedAt, b.UpdatedAt, members);

    /// <summary>Wer gehört zu welcher Vorgabe, mit Zahl der Abweichungen (aus dem letzten Bericht).</summary>
    private static async Task<List<(Guid BaselineId, BaselineMemberDto Member)>> MembersAsync(AppDbContext db, IReadOnlyList<ComplianceBaseline> baselines, CancellationToken ct)
    {
        var hosts = await db.Hosts.AsNoTracking().Include(h => h.Site).Where(h => !h.IsRevoked).OrderBy(h => h.Name).ToListAsync(ct);
        var membership = ComplianceBaselines.Resolve(baselines, hosts.Select(h => new ComplianceBaselines.HostInfo(h.Id, h.SiteId, h.Tags)).ToList());
        var rows = await db.HostCompliance.AsNoTracking().Where(r => membership.Keys.Contains(r.HostId)).ToDictionaryAsync(r => r.HostId, ct);
        var rules = baselines.ToDictionary(b => b.Id, b => ComplianceBaselines.Parse(b.RulesJson));
        var firewall = await ComplianceBaselines.FirewallTargetsAsync(db, rules.Values, ct);
        return hosts.Where(h => membership.ContainsKey(h.Id)).Select(h =>
        {
            var m = membership[h.Id];
            var report = rows.TryGetValue(h.Id, out var row) ? JsonSerializer.Deserialize(row.Json, AgentJsonContext.Default.ComplianceReport) : null;
            var results = ComplianceBaselines.Evaluate(rules[m.BaselineId], h.OsFamily, report, h.FirewallVersionId, firewall);
            return (m.BaselineId, new BaselineMemberDto(h.Id, h.Name, h.Site!.Name, h.OsFamily, m.Via,
                results.Count(r => r.State == RuleState.Violation), results.Count(r => r.State == RuleState.Unknown),
                results.Count(r => r.State == RuleState.Violation && r.Fixable)));
        }).ToList();
    }

    // --- Schreiben ------------------------------------------------------------------------------------

    private static IResult Invalid(string field, params string[] errors) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = errors });

    private static async Task<IResult?> ValidateAsync(AppDbContext db, SaveBaselineRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            return Invalid("name", L.T("Name fehlt (max. 100 Zeichen).", "Name missing (max. 100 characters)."));
        }

        if (await db.ComplianceBaselines.AnyAsync(b => b.Name == name && b.Id != id, ct))
        {
            return Results.Problem(L.T("Eine Vorgabe mit diesem Namen existiert schon.", "A baseline with this name already exists."), statusCode: StatusCodes.Status409Conflict);
        }

        if (request.Rules is null)
        {
            return Invalid("rules", "missing");
        }

        if (ComplianceBaselines.Validate(ComplianceBaselines.Clean(request.Rules)) is { Count: > 0 } errors)
        {
            return Invalid("rules", errors.ToArray());
        }

        if (request.Rules.FirewallProfileId is { } profile && !await db.FirewallProfiles.AnyAsync(p => p.Id == profile, ct))
        {
            return Invalid("rules", L.T("Firewall-Regelsatz nicht gefunden.", "Firewall rule set not found."));
        }

        if (request.MemberSiteId is { } site && !await db.Sites.AnyAsync(s => s.Id == site, ct))
        {
            return Invalid("memberSiteId", L.T("Site nicht gefunden.", "Site not found."));
        }

        if (request.IncludeOthers && await db.ComplianceBaselines.AnyAsync(b => b.IncludeOthers && b.Id != id, ct))
        {
            return Invalid("includeOthers", L.T("\"Alle übrigen Hosts\" hat schon eine andere Vorgabe.", "\"All other hosts\" is already used by another baseline."));
        }

        if ((request.HostIds?.Length ?? 0) > 500 || (request.MemberTags?.Length ?? 0) > 500)
        {
            return Invalid("hostIds", L.T("Zu viele Hosts oder Tags.", "Too many hosts or tags."));
        }

        return null;
    }

    /// <summary>Werte übernehmen; ausgewählte Hosts verlassen ihre bisherige Vorgabe (ein Host, eine Vorgabe).</summary>
    private static async Task ApplyAsync(AppDbContext db, ComplianceBaseline baseline, SaveBaselineRequest request, CancellationToken ct)
    {
        baseline.Name = request.Name.Trim();
        baseline.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()[..Math.Min(request.Description.Trim().Length, 500)];
        baseline.RulesJson = ComplianceBaselines.Serialize(ComplianceBaselines.Clean(request.Rules));
        baseline.MemberTags = string.Join(',', PatchRules.SplitTags(request.MemberTags)) is { Length: > 0 } tags ? tags : null;
        baseline.MemberSiteId = request.MemberSiteId;
        baseline.IncludeOthers = request.IncludeOthers;
        baseline.UpdatedAt = DateTime.UtcNow;

        var wanted = (request.HostIds ?? []).Distinct().ToList();
        var existingHosts = await db.Hosts.Where(h => wanted.Contains(h.Id)).Select(h => h.Id).ToListAsync(ct);
        var current = await db.ComplianceBaselineHosts.Where(m => m.BaselineId == baseline.Id || existingHosts.Contains(m.HostId)).ToListAsync(ct);
        db.ComplianceBaselineHosts.RemoveRange(current.Where(m => !(m.BaselineId == baseline.Id && existingHosts.Contains(m.HostId))));
        foreach (var hostId in existingHosts.Where(h => !current.Any(m => m.HostId == h && m.BaselineId == baseline.Id)))
        {
            db.ComplianceBaselineHosts.Add(new ComplianceBaselineHost { HostId = hostId, BaselineId = baseline.Id });
        }
    }

    private static async Task<IResult> CreateAsync(SaveBaselineRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (await ValidateAsync(db, request, null, ct) is { } invalid)
        {
            return invalid;
        }

        var baseline = new ComplianceBaseline { Name = request.Name.Trim(), RulesJson = "{}" };
        db.ComplianceBaselines.Add(baseline);
        await ApplyAsync(db, baseline, request, ct);
        audit.Add("compliance.baseline.created", baseline.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { baseline.Id });
    }

    private static async Task<IResult> UpdateAsync(Guid id, SaveBaselineRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var baseline = await db.ComplianceBaselines.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (baseline is null)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(db, request, id, ct) is { } invalid)
        {
            return invalid;
        }

        await ApplyAsync(db, baseline, request, ct);
        audit.Add("compliance.baseline.updated", baseline.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { baseline.Id });
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var baseline = await db.ComplianceBaselines.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (baseline is null)
        {
            return Results.NotFound();
        }

        db.ComplianceBaselines.Remove(baseline);
        audit.Add("compliance.baseline.deleted", baseline.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Mitglieder, wenn diese (ungespeicherte) Vorgabe so gespeichert würde.</summary>
    private static async Task<IResult> PreviewAsync(Guid? id, SaveBaselineRequest request, AppDbContext db, CancellationToken ct)
    {
        var baselines = await db.ComplianceBaselines.AsNoTracking().Include(b => b.Hosts).Where(b => b.Id != id).ToListAsync(ct);
        var original = id is null ? null : await db.ComplianceBaselines.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);
        var wanted = (request.HostIds ?? []).ToHashSet();
        foreach (var other in baselines)
        {
            other.Hosts.RemoveAll(h => wanted.Contains(h.HostId));
            other.IncludeOthers &= !request.IncludeOthers;
        }

        var draft = new ComplianceBaseline
        {
            Id = id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "?" : request.Name.Trim(),
            RulesJson = ComplianceBaselines.Serialize(ComplianceBaselines.Clean(request.Rules ?? BaselineRules.Empty)),
            MemberTags = request.MemberTags,
            MemberSiteId = request.MemberSiteId,
            IncludeOthers = request.IncludeOthers,
            CreatedAt = original?.CreatedAt ?? DateTime.UtcNow,
            Hosts = wanted.Select(h => new ComplianceBaselineHost { HostId = h }).ToList(),
        };
        baselines.Add(draft);
        var members = await MembersAsync(db, baselines, ct);
        return Results.Ok(members.Where(m => m.BaselineId == draft.Id).Select(m => m.Member).ToList());
    }

    // --- Angleichen -----------------------------------------------------------------------------------

    /// <summary>Plant den Sammel-Job für einen Host. Rückgabe: Job oder Grund, warum nicht.</summary>
    private static async Task<(PatchJob? Job, JobPlanner.Skip? Skip)> PlanAlignAsync(AppDbContext db, FeatureFlags features, ClaimsPrincipal user, Guid hostId,
        IReadOnlyCollection<string>? keys, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == hostId, ct);
        if (host is null)
        {
            return (null, new JobPlanner.Skip(hostId, hostId.ToString(), JobPlanner.HostNotFound));
        }

        var row = await db.HostCompliance.AsNoTracking().SingleOrDefaultAsync(r => r.HostId == hostId, ct);
        var checks = await ComplianceEndpoints.BaselineChecksAsync(db, [host], row is null ? new Dictionary<Guid, HostCompliance>() : new() { [hostId] = row }, ct);
        if (!checks.TryGetValue(hostId, out var check))
        {
            return (null, new JobPlanner.Skip(hostId, host.Name, L.T("Der Host hat keine Compliance-Vorgabe.", "The host has no compliance baseline.")));
        }

        var chosen = check.Results.Where(r => keys is null || keys.Count == 0 || keys.Contains(r.Key)).ToList();
        var steps = ComplianceBaselines.Steps(chosen);
        var off = steps.Select(s => ComplianceBaselines.FeatureFor(s.Kind)).Distinct().Where(f => !features.IsOn(f)).ToList();
        if (off.Count > 0)
        {
            // Schritte mit ausgeschaltetem Schalter weglassen, den Rest trotzdem angleichen
            steps = steps.Where(s => features.IsOn(ComplianceBaselines.FeatureFor(s.Kind))).ToList();
        }

        if (steps.Count == 0)
        {
            return (null, new JobPlanner.Skip(hostId, host.Name, off.Count > 0
                ? string.Join(" ", off.Select(FeatureFlags.DisabledMessage))
                : L.T("Nichts anzugleichen: keine behebbaren Abweichungen.", "Nothing to align: no fixable deviations.")));
        }

        var firewallVersion = chosen.FirstOrDefault(r => r.FirewallVersionId is not null && r.State == RuleState.Violation && r.Fixable)?.FirewallVersionId;
        var change = new SystemChange(SystemChangeKind.Batch, Steps: steps);
        var label = L.T($"Vorgabe {check.Name} angleichen ({steps.Count} Schritte)", $"Align baseline {check.Name} ({steps.Count} steps)");
        return await ComplianceEndpoints.PlanChangeAsync(db, hostId, change, null, steps.Any(s => s.Kind == SystemChangeKind.FirewallApply) ? firewallVersion : null, label, user, ct);
    }

    private static async Task<IResult> AlignHostAsync(Guid id, AlignRequest? request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.ComplianceChecks))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.ComplianceChecks), statusCode: StatusCodes.Status403Forbidden);
        }

        var (job, skip) = await PlanAlignAsync(db, features, user, id, request?.Keys, ct);
        if (skip is not null)
        {
            return skip.Reason == JobPlanner.HostNotFound ? Results.NotFound() : Results.Problem(skip.Reason, statusCode: StatusCodes.Status409Conflict);
        }

        audit.Add("compliance.baseline.align", $"{job!.Summary} on {job.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ComplianceEndpoints.ChangeResultDto(1, [], job.Id));
    }

    private static async Task<IResult> AlignBaselineAsync(Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.ComplianceChecks))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.ComplianceChecks), statusCode: StatusCodes.Status403Forbidden);
        }

        var (_, members) = await ComplianceBaselines.LoadAsync(db, ct);
        var hostIds = members.Where(m => m.Value.BaselineId == id).Select(m => m.Key).ToList();
        var created = 0;
        var skipped = new List<JobPlanner.Skip>();
        foreach (var hostId in hostIds)
        {
            var (job, skip) = await PlanAlignAsync(db, features, user, hostId, null, ct);
            if (skip is not null)
            {
                skipped.Add(skip);
                continue;
            }

            created++;
            audit.Add("compliance.baseline.align", $"{job!.Summary} on {job.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ComplianceEndpoints.ChangeResultDto(created, skipped));
    }
}
