using System.Security.Claims;
using System.Text.Json;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Compliance: Firewall, SSH/RDP, lokale Benutzer. Lesen braucht den Schalter "Compliance-Prüfung", jede Änderung
/// ihren eigenen Schalter (alle ab Werk aus) und die Rolle Admin. Auf dem Host muss zusätzlich
/// policy.json "allowSystemChanges" erlaubt sein; das prüft der Agent.
/// </summary>
public static class ComplianceEndpoints
{
    public sealed record ComplianceSummaryDto(
        Guid HostId,
        string HostName,
        string SiteName,
        OsFamily OsFamily,
        DateTime? CollectedAt,
        string? FirewallProduct,
        bool FirewallEnabled,
        bool FirewallManageable,
        FirewallVersionRefDto? FirewallVersion,
        bool FirewallDrift,
        int RemoteAccessIssues,
        int Users,
        int Admins,
        bool BuiltInAdminActive,
        Guid? BaselineId = null,
        string? BaselineName = null,
        int BaselineViolations = 0,
        int BaselineUnknown = 0);

    /// <param name="Drift">Der Host meldet einen anderen Stand als die zuletzt ausgerollte Version (lokal geändert oder Original wiederhergestellt).</param>
    public sealed record FirewallVersionRefDto(Guid VersionId, Guid ProfileId, string ProfileName, int Number, DateTime? AppliedAt);

    public sealed record HostComplianceDto(ComplianceReport? Report, DateTime? CollectedAt, FirewallVersionRefDto? FirewallVersion, bool FirewallDrift, bool HasPendingChange,
        HostBaselineDto? Baseline = null);

    /// <summary>Vorgabe des Hosts mit Soll/Ist je Regel.</summary>
    public sealed record HostBaselineDto(Guid Id, string Name, PlanVia Via, IReadOnlyList<RuleResult> Results);

    public sealed record FirewallVersionDto(Guid Id, int Number, FirewallConfig Config, string Hash, string? Note, string CreatedBy, DateTime CreatedAt, int Hosts);

    public sealed record FirewallProfileDto(Guid Id, string Name, string? Description, DateTime CreatedAt, IReadOnlyList<FirewallVersionDto> Versions);

    public sealed record SaveFirewallProfileRequest(string Name, string? Description, FirewallConfig Config, string? Note);

    public sealed record NewFirewallVersionRequest(FirewallConfig Config, string? Note);

    public sealed record DeployFirewallRequest(Guid VersionId, Guid[] HostIds);

    public sealed record RemoteAccessRequest(string[] Settings);

    public sealed record UserActionRequest(string UserName, SystemChangeKind Action, string? Password = null, string? SshKey = null);

    public sealed record ChangeResultDto(int Created, IReadOnlyList<JobPlanner.Skip> Skipped, Guid? JobId = null);

    public static void MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var admin = api.MapGroup("").RequireAuthorization(UserAccounts.AdminPolicy);

        api.MapGet("/compliance", List);
        api.MapGet("/hosts/{id:guid}/compliance", ForHost);

        api.MapGet("/firewall/profiles", Profiles);
        admin.MapPost("/firewall/profiles", CreateProfile);
        admin.MapPost("/firewall/profiles/{id:guid}/versions", AddVersion);
        admin.MapDelete("/firewall/profiles/{id:guid}", DeleteProfile);
        admin.MapPost("/firewall/deploy", Deploy);
        admin.MapPost("/hosts/{id:guid}/firewall/restore-original", (Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct) =>
            CreateChangeAsync(db, audit, features, user, id, new SystemChange(SystemChangeKind.FirewallRestoreOriginal), null, null,
                L.T("Firewall: Original wiederherstellen", "Firewall: restore original"), ct));
        admin.MapPost("/hosts/{id:guid}/remote-access/fix", FixRemoteAccess);
        admin.MapPost("/hosts/{id:guid}/remote-access/revert", (Guid id, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct) =>
            CreateChangeAsync(db, audit, features, user, id, new SystemChange(SystemChangeKind.RemoteAccessRevert), null, null,
                L.T("SSH/RDP: Einstellungen von AIOPatcher zurücknehmen", "SSH/RDP: revert AIOPatcher settings"), ct));
        admin.MapPost("/hosts/{id:guid}/users/action", UserAction);

        // Eigene Update-Automatik des Hosts: gehört zum Patchen, daher schon für Operatoren
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);
        op.MapPost("/hosts/{id:guid}/auto-updates", (Guid id, AutoUpdatesRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct) =>
            CreateChangeAsync(db, audit, features, user, id, new SystemChange(request.Enabled ? SystemChangeKind.AutoUpdatesOn : SystemChangeKind.AutoUpdatesOff), null, null,
                request.Enabled
                    ? L.T("Update-Automatik des Systems einschalten", "Turn on the system's automatic updates")
                    : L.T("Update-Automatik des Systems abschalten (nur AIOPatcher patcht)", "Turn off the system's automatic updates (only AIOPatcher patches)"), ct));
    }

    public sealed record AutoUpdatesRequest(bool Enabled);

    // --- Hilfen für die Agent-API ---------------------------------------------------------------------

    public static IDataProtector SecretProtector(IDataProtectionProvider provider) => provider.CreateProtector("AIOPatcher.JobSecrets.v1");

    /// <summary>Schalter, die für diese Änderung an sein müssen (Sammel-Job: die aller Schritte).</summary>
    public static IReadOnlyList<Feature> FeaturesFor(SystemChange change) =>
        change.Kind == SystemChangeKind.Batch
            ? (change.Steps ?? []).Select(s => ComplianceBaselines.FeatureFor(s.Kind)).Distinct().ToList()
            : [ComplianceBaselines.FeatureFor(change.Kind)];

    /// <summary>Hauptschalter einer Änderung (Sammel-Job: der des ersten Schritts).</summary>
    public static Feature? FeatureFor(PatchJob job) => Deserialize(job.Payload) is { } change ? FeaturesFor(change).Cast<Feature?>().FirstOrDefault() : null;

    /// <summary>Erster ausgeschaltete Schalter, den dieser Job braucht (null = darf laufen).</summary>
    public static Feature? BlockedFeature(PatchJob job, FeatureFlags features) =>
        Deserialize(job.Payload) is { } change ? FeaturesFor(change).Cast<Feature?>().FirstOrDefault(f => !features.IsOn(f!.Value)) : null;

    /// <summary>Änderung für den Agent: Payload plus entschlüsseltes Geheimnis. Das Geheimnis wird dabei vom Job gelöscht.</summary>
    public static SystemChange? TakeChange(PatchJob job, IDataProtector? secrets)
    {
        var change = Deserialize(job.Payload);
        if (change is not null && job.ProtectedSecret is { } protectedSecret && secrets is not null)
        {
            try
            {
                change = change with { Secret = secrets.Unprotect(protectedSecret) };
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Schlüssel gewechselt: ohne Passwort weiter, der Agent lehnt dann ab.
            }
        }

        job.ProtectedSecret = null;
        return change;
    }

    public static void OnChangeSucceeded(ManagedHost host, PatchJob job)
    {
        var change = Deserialize(job.Payload);
        var kind = change?.Kind == SystemChangeKind.Batch && change.Steps?.Any(s => s.Kind == SystemChangeKind.FirewallApply) == true
            ? SystemChangeKind.FirewallApply
            : change?.Kind;
        switch (kind)
        {
            case SystemChangeKind.FirewallApply:
                host.FirewallVersionId = job.FirewallVersionId;
                host.FirewallAppliedAt = DateTime.UtcNow;
                break;
            case SystemChangeKind.FirewallRestoreOriginal:
                host.FirewallVersionId = null;
                host.FirewallAppliedAt = DateTime.UtcNow;
                break;
        }
    }

    private static SystemChange? Deserialize(string? payload) =>
        payload is null ? null : JsonSerializer.Deserialize(payload, AgentJsonContext.Default.SystemChange);

    /// <summary>Pull-Agent: Firewall, SSH/RDP und Benutzer kommen aus eigenen Modulen. Fehlende Teile bleiben wie gespeichert.</summary>
    public static async Task StorePartAsync(AppDbContext db, ManagedHost host, DateTime now, CancellationToken ct,
        FirewallStatus? firewall = null, IReadOnlyList<RemoteAccessSetting>? remoteAccess = null, IReadOnlyList<LocalUser>? users = null)
    {
        var row = await db.HostCompliance.AsNoTracking().SingleOrDefaultAsync(r => r.HostId == host.Id, ct);
        var current = row is null ? null : JsonSerializer.Deserialize(row.Json, AgentJsonContext.Default.ComplianceReport);
        var report = new ComplianceReport(
            firewall ?? current?.Firewall ?? new FirewallStatus("unknown", false, false, null, null, [], 0, null, false),
            remoteAccess ?? current?.RemoteAccess ?? [],
            users ?? current?.Users ?? [],
            now);
        await StoreAsync(db, host, report, now, ct);
    }

    public static async Task StoreAsync(AppDbContext db, ManagedHost host, ComplianceReport report, DateTime now, CancellationToken ct)
    {
        // Größen begrenzen: ein Agent soll die DB nicht fluten können.
        report = report with
        {
            Firewall = report.Firewall with { Rules = report.Firewall.Rules.Take(300).Select(r => r with { Text = Cut(r.Text, 300) }).ToList(), Detail = report.Firewall.Detail is null ? null : Cut(report.Firewall.Detail, 1000) },
            RemoteAccess = report.RemoteAccess.Take(50).ToList(),
            Users = report.Users.Take(500).ToList(),
        };

        var row = await db.HostCompliance.FindAsync([host.Id], ct);
        if (row is null)
        {
            db.HostCompliance.Add(row = new HostCompliance { HostId = host.Id, Json = "" });
        }

        row.CollectedAt = now;
        row.Json = JsonSerializer.Serialize(report, AgentJsonContext.Default.ComplianceReport);
        row.FirewallProduct = Cut(report.Firewall.Product, 32);
        row.FirewallEnabled = report.Firewall.Enabled;
        row.FirewallManageable = report.Firewall.Manageable;
        row.AppliedFirewallHash = report.Firewall.AppliedConfigHash is { } hash ? Cut(hash, 32) : null;
        row.RemoteAccessIssues = report.RemoteAccess.Count(s => !s.Compliant);
        row.Users = report.Users.Count;
        row.Admins = report.Users.Count(u => u.IsAdmin && u.Enabled);
        row.BuiltInAdminActive = report.Users.Any(u => u.IsBuiltIn && u.Enabled && u.PasswordState is "set" or "empty" or "unknown");
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    // --- Lesen ----------------------------------------------------------------------------------------

    private static IResult? Disabled(FeatureFlags features, Feature feature) =>
        features.IsOn(feature) ? null : Results.Problem(FeatureFlags.DisabledMessage(feature), statusCode: StatusCodes.Status403Forbidden);

    private static async Task<Dictionary<Guid, (FirewallVersionRefDto Ref, string Hash)>> VersionRefsAsync(AppDbContext db, IEnumerable<ManagedHost> hosts, CancellationToken ct)
    {
        var ids = hosts.Where(h => h.FirewallVersionId is not null).Select(h => h.FirewallVersionId!.Value).Distinct().ToList();
        var versions = await db.FirewallProfileVersions.Where(v => ids.Contains(v.Id))
            .Select(v => new { v.Id, v.ProfileId, ProfileName = v.Profile!.Name, v.Number, v.Hash })
            .ToDictionaryAsync(v => v.Id, ct);
        return hosts.Where(h => h.FirewallVersionId is { } id && versions.ContainsKey(id))
            .ToDictionary(h => h.Id, h =>
            {
                var v = versions[h.FirewallVersionId!.Value];
                return (new FirewallVersionRefDto(v.Id, v.ProfileId, v.ProfileName, v.Number, h.FirewallAppliedAt), v.Hash);
            });
    }

    private static async Task<IResult> List(AppDbContext db, FeatureFlags features, CancellationToken ct)
    {
        if (Disabled(features, Feature.ComplianceChecks) is { } off)
        {
            return off;
        }

        var hosts = await db.Hosts.AsNoTracking().Include(h => h.Site).Where(h => !h.IsRevoked).OrderBy(h => h.Name).ToListAsync(ct);
        var rows = await db.HostCompliance.AsNoTracking().ToDictionaryAsync(r => r.HostId, ct);
        var refs = await VersionRefsAsync(db, hosts, ct);
        var checks = await BaselineChecksAsync(db, hosts, rows, ct);
        return Results.Ok(hosts.Select(h =>
        {
            rows.TryGetValue(h.Id, out var r);
            refs.TryGetValue(h.Id, out var v);
            checks.TryGetValue(h.Id, out var b);
            return new ComplianceSummaryDto(h.Id, h.Name, h.Site!.Name, h.OsFamily, r?.CollectedAt, r?.FirewallProduct, r?.FirewallEnabled ?? false,
                r?.FirewallManageable ?? false, v.Ref, v.Ref is not null && r is not null && r.AppliedFirewallHash != v.Hash,
                r?.RemoteAccessIssues ?? 0, r?.Users ?? 0, r?.Admins ?? 0, r?.BuiltInAdminActive ?? false,
                b?.Id, b?.Name, b?.Results.Count(x => x.State == RuleState.Violation) ?? 0, b?.Results.Count(x => x.State == RuleState.Unknown) ?? 0);
        }).ToList());
    }

    /// <summary>Soll/Ist aller Hosts mit Vorgabe (Bericht aus HostCompliance).</summary>
    public static async Task<Dictionary<Guid, HostBaselineDto>> BaselineChecksAsync(AppDbContext db, IReadOnlyList<ManagedHost> hosts,
        IReadOnlyDictionary<Guid, HostCompliance> rows, CancellationToken ct)
    {
        var (baselines, members) = await ComplianceBaselines.LoadAsync(db, ct);
        if (members.Count == 0)
        {
            return [];
        }

        var rules = baselines.ToDictionary(b => b.Id, b => ComplianceBaselines.Parse(b.RulesJson));
        var firewall = await ComplianceBaselines.FirewallTargetsAsync(db, rules.Values, ct);
        var result = new Dictionary<Guid, HostBaselineDto>();
        foreach (var host in hosts)
        {
            if (!members.TryGetValue(host.Id, out var member))
            {
                continue;
            }

            var report = rows.TryGetValue(host.Id, out var row) ? JsonSerializer.Deserialize(row.Json, AgentJsonContext.Default.ComplianceReport) : null;
            result[host.Id] = new HostBaselineDto(member.BaselineId, member.BaselineName, member.Via,
                ComplianceBaselines.Evaluate(rules[member.BaselineId], host.OsFamily, report, host.FirewallVersionId, firewall));
        }

        return result;
    }

    private static async Task<IResult> ForHost(Guid id, AppDbContext db, FeatureFlags features, CancellationToken ct)
    {
        if (Disabled(features, Feature.ComplianceChecks) is { } off)
        {
            return off;
        }

        var host = await db.Hosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var row = await db.HostCompliance.AsNoTracking().SingleOrDefaultAsync(r => r.HostId == id, ct);
        var refs = await VersionRefsAsync(db, [host], ct);
        refs.TryGetValue(id, out var v);
        var report = row is null ? null : JsonSerializer.Deserialize(row.Json, AgentJsonContext.Default.ComplianceReport);
        var pending = await db.Jobs.AnyAsync(j => j.HostId == id && j.Type == JobType.SystemChange && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        var checks = await BaselineChecksAsync(db, [host], row is null ? new Dictionary<Guid, HostCompliance>() : new() { [id] = row }, ct);
        return Results.Ok(new HostComplianceDto(report, row?.CollectedAt, v.Ref, v.Ref is not null && row is not null && row.AppliedFirewallHash != v.Hash, pending,
            checks.GetValueOrDefault(id)));
    }

    // --- Firewall-Profile -----------------------------------------------------------------------------

    private static FirewallConfig Normalize(FirewallConfig config) => config with
    {
        Rules = config.Rules.Select(r => r with
        {
            Ports = string.IsNullOrWhiteSpace(r.Ports) ? null : r.Ports.Replace(" ", ""),
            Source = string.IsNullOrWhiteSpace(r.Source) ? null : FirewallRules.NormalizeSource(r.Source) ?? r.Source,
            Comment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim(),
        }).ToList(),
    };

    private static IResult? Invalid(FirewallConfig? config) =>
        config is null ? Results.ValidationProblem(new Dictionary<string, string[]> { ["config"] = ["missing"] })
        : FirewallRules.Validate(config) is { Count: > 0 } errors ? Results.ValidationProblem(new Dictionary<string, string[]> { ["config"] = errors.ToArray() })
        : null;

    private static async Task<IResult> Profiles(AppDbContext db, CancellationToken ct)
    {
        var profiles = await db.FirewallProfiles.AsNoTracking().Include(p => p.Versions).OrderBy(p => p.Name).ToListAsync(ct);
        var counts = await db.Hosts.Where(h => h.FirewallVersionId != null && !h.IsRevoked).GroupBy(h => h.FirewallVersionId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return Results.Ok(profiles.Select(p => new FirewallProfileDto(p.Id, p.Name, p.Description, p.CreatedAt,
            p.Versions.OrderByDescending(v => v.Number).Select(v => new FirewallVersionDto(v.Id, v.Number,
                JsonSerializer.Deserialize(v.ConfigJson, AgentJsonContext.Default.FirewallConfig)!, v.Hash, v.Note, v.CreatedBy, v.CreatedAt,
                counts.GetValueOrDefault(v.Id))).ToList())).ToList());
    }

    private static FirewallProfileVersion NewVersion(FirewallProfile profile, int number, FirewallConfig config, string? note, ClaimsPrincipal user) => new()
    {
        Profile = profile,
        Number = number,
        ConfigJson = JsonSerializer.Serialize(config, AgentJsonContext.Default.FirewallConfig),
        Hash = FirewallRules.Hash(config),
        Note = string.IsNullOrWhiteSpace(note) ? null : Cut(note.Trim(), 500),
        CreatedBy = user.Identity!.Name!,
    };

    private static async Task<IResult> CreateProfile(SaveFirewallProfileRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [L.T("Name fehlt (max. 100 Zeichen).", "Name missing (max. 100 characters).")] });
        }

        var config = request.Config is null ? null : Normalize(request.Config);
        if (Invalid(config) is { } invalid)
        {
            return invalid;
        }

        if (await db.FirewallProfiles.AnyAsync(p => p.Name == name, ct))
        {
            return Results.Problem(L.T("Ein Regelsatz mit diesem Namen existiert schon.", "A rule set with this name already exists."), statusCode: StatusCodes.Status409Conflict);
        }

        var profile = new FirewallProfile { Name = name, Description = string.IsNullOrWhiteSpace(request.Description) ? null : Cut(request.Description.Trim(), 500) };
        db.FirewallProfiles.Add(profile);
        db.FirewallProfileVersions.Add(NewVersion(profile, 1, config!, request.Note, user));
        audit.Add("firewall.profile.created", $"{name} v1 ({config!.Rules.Count} rules)");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { profile.Id });
    }

    private static async Task<IResult> AddVersion(Guid id, NewFirewallVersionRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var profile = await db.FirewallProfiles.Include(p => p.Versions).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var config = request.Config is null ? null : Normalize(request.Config);
        if (Invalid(config) is { } invalid)
        {
            return invalid;
        }

        var number = profile.Versions.Count == 0 ? 1 : profile.Versions.Max(v => v.Number) + 1;
        var version = NewVersion(profile, number, config!, request.Note, user);
        db.FirewallProfileVersions.Add(version);
        audit.Add("firewall.profile.version", $"{profile.Name} v{number} ({config!.Rules.Count} rules)");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { version.Id, version.Number });
    }

    private static async Task<IResult> DeleteProfile(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var profile = await db.FirewallProfiles.Include(p => p.Versions).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var versionIds = profile.Versions.Select(v => v.Id).ToList();
        if (await db.Hosts.AnyAsync(h => h.FirewallVersionId != null && versionIds.Contains(h.FirewallVersionId.Value), ct))
        {
            return Results.Problem(L.T("Der Regelsatz ist auf Hosts aktiv. Erst einen anderen ausrollen oder das Original wiederherstellen.",
                "The rule set is active on hosts. Deploy another one or restore the original first."), statusCode: StatusCodes.Status409Conflict);
        }

        db.FirewallProfiles.Remove(profile);
        audit.Add("firewall.profile.deleted", profile.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Deploy(DeployFirewallRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (Disabled(features, Feature.FirewallManagement) is { } off)
        {
            return off;
        }

        if (request.HostIds is null || request.HostIds.Length == 0 || request.HostIds.Length > JobPlanner.MaxHostsPerRequest)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostIds"] = [L.T("Hosts auswählen.", "Select hosts.")] });
        }

        var version = await db.FirewallProfileVersions.Include(v => v.Profile).SingleOrDefaultAsync(v => v.Id == request.VersionId, ct);
        if (version is null)
        {
            return Results.NotFound();
        }

        var config = JsonSerializer.Deserialize(version.ConfigJson, AgentJsonContext.Default.FirewallConfig)!;
        var change = new SystemChange(SystemChangeKind.FirewallApply, Firewall: config);
        var label = L.T($"Firewall: {version.Profile!.Name} v{version.Number}", $"Firewall: {version.Profile!.Name} v{version.Number}");
        var created = 0;
        var skipped = new List<JobPlanner.Skip>();
        Guid? lastJob = null;
        foreach (var hostId in request.HostIds.Distinct())
        {
            var (job, skip) = await PlanChangeAsync(db, hostId, change, null, version.Id, label, user, ct);
            if (skip is not null)
            {
                skipped.Add(skip);
                continue;
            }

            created++;
            lastJob = job!.Id;
            audit.Add("firewall.deploy", $"{version.Profile.Name} v{version.Number} → {job.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ChangeResultDto(created, skipped, created == 1 ? lastJob : null));
    }

    private static Task<IResult> FixRemoteAccess(Guid id, RemoteAccessRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        var settings = (request.Settings ?? []).Where(s => s.Length is > 0 and <= 64 && (s.StartsWith("ssh.") || s.StartsWith("rdp."))).Distinct().ToList();
        if (settings.Count == 0)
        {
            return Task.FromResult(Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [L.T("Mindestens eine Empfehlung auswählen.", "Select at least one recommendation.")] }));
        }

        return CreateChangeAsync(db, audit, features, user, id, new SystemChange(SystemChangeKind.RemoteAccessFix, Settings: settings), null, null,
            L.T($"SSH/RDP: {string.Join(", ", settings)}", $"SSH/RDP: {string.Join(", ", settings)}"), ct);
    }

    private static Task<IResult> UserAction(Guid id, UserActionRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features,
        IDataProtectionProvider protection, CancellationToken ct)
    {
        IResult Bad(string field, string de, string en) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [L.T(de, en)] });

        if (request.Action is < SystemChangeKind.UserLock or > SystemChangeKind.UserRemoveSshKey || string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Length > 32)
        {
            return Task.FromResult(Bad("action", "Ungültige Aktion oder Benutzer.", "Invalid action or user."));
        }

        string? protectedSecret = null;
        string? sshKey = null;
        if (request.Action == SystemChangeKind.UserSetPassword)
        {
            if (request.Password is not { Length: >= 12 and <= 128 } password || password.Any(c => c is '\n' or '\r' or '\0'))
            {
                return Task.FromResult(Bad("password", "Passwort: 12 bis 128 Zeichen, ohne Zeilenumbruch.", "Password: 12 to 128 characters, no line breaks."));
            }

            protectedSecret = SecretProtector(protection).Protect(password);
        }

        if (request.Action is SystemChangeKind.UserAddSshKey or SystemChangeKind.UserRemoveSshKey)
        {
            sshKey = request.SshKey?.Trim();
            if (sshKey is null or { Length: 0 or > 16000 } || sshKey.Contains('\n'))
            {
                return Task.FromResult(Bad("sshKey", "Genau ein öffentlicher Schlüssel (eine Zeile).", "Exactly one public key (one line)."));
            }
        }

        var change = new SystemChange(request.Action, UserName: request.UserName.Trim(), SshKey: sshKey);
        return CreateChangeAsync(db, audit, features, user, id, change, protectedSecret, null,
            L.T($"Benutzer {change.UserName}: {request.Action}", $"User {change.UserName}: {request.Action}"), ct);
    }

    // --- Jobs anlegen ---------------------------------------------------------------------------------

    private static async Task<IResult> CreateChangeAsync(AppDbContext db, AuditLog audit, FeatureFlags features, ClaimsPrincipal user, Guid hostId,
        SystemChange change, string? protectedSecret, Guid? firewallVersionId, string label, CancellationToken ct)
    {
        if (FeaturesFor(change).Select(f => Disabled(features, f)).FirstOrDefault(r => r is not null) is { } off)
        {
            return off;
        }

        var (job, skip) = await PlanChangeAsync(db, hostId, change, protectedSecret, firewallVersionId, label, user, ct);
        if (skip is not null)
        {
            return skip.Reason == JobPlanner.HostNotFound ? Results.NotFound() : Results.Problem(skip.Reason, statusCode: StatusCodes.Status409Conflict);
        }

        // Nie das Passwort ins Audit-Log: nur Art der Änderung und Benutzer.
        audit.Add("system-change.created", $"{label} on {job!.Host!.Name}", siteId: job.Host.SiteId, hostId: job.HostId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ChangeResultDto(1, [], job.Id));
    }

    internal static async Task<(PatchJob? Job, JobPlanner.Skip? Skip)> PlanChangeAsync(AppDbContext db, Guid hostId, SystemChange change, string? protectedSecret,
        Guid? firewallVersionId, string label, ClaimsPrincipal user, CancellationToken ct)
    {
        var host = await db.Hosts.SingleOrDefaultAsync(h => h.Id == hostId, ct);
        if (host is null)
        {
            return (null, new JobPlanner.Skip(hostId, hostId.ToString(), JobPlanner.HostNotFound));
        }

        if (host.IsRevoked)
        {
            return (null, new JobPlanner.Skip(hostId, host.Name, L.T("Host ist gesperrt.", "Host is revoked.")));
        }

        if (await db.Jobs.AnyAsync(j => j.HostId == hostId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return (null, new JobPlanner.Skip(hostId, host.Name, L.T("Auf dem Host läuft oder wartet schon ein Job.", "A job is already running or queued on this host.")));
        }

        var job = new PatchJob
        {
            Host = host,
            HostId = host.Id,
            Type = JobType.SystemChange,
            RequestedBy = $"admin:{user.Identity!.Name}",
            Payload = JsonSerializer.Serialize(change with { Secret = null }, AgentJsonContext.Default.SystemChange),
            ProtectedSecret = protectedSecret,
            FirewallVersionId = firewallVersionId,
            Summary = label,
        };
        db.Jobs.Add(job);
        return (job, null);
    }
}
