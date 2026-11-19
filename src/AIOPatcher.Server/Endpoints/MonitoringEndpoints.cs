using System.Security.Claims;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Modules;
using AIOPatcher.Server.Services.Monitoring;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Monitoring-Clients (Wazuh, Checkmk): Ziele pflegen (Admin), Zustand aller Hosts, Clients installieren, umstellen und
/// deinstallieren (Admin, Schalter "Monitoring-Clients verwalten", auf dem Host policy.json allowMonitoringChanges).
/// </summary>
public static class MonitoringEndpoints
{
    public const string SecretPurpose = "AIOPatcher.Monitoring.Secret";

    public sealed record TargetDto(
        Guid Id, string Name, string Product, Guid? SiteId, string? SiteName,
        string? Address, int? Port, string? Protocol, string Enrollment, int? EnrollmentPort, string? Groups, string? ManagerVersion,
        string? SiteUrl, int? ReceiverPort, string? RegistrationUser, string? OnlyFrom, string? LegacyPackageUrl, string? TlsFingerprintSha256, string? SiteVersion,
        bool HasSecret, DateTime UpdatedAt, string? LastError);

    /// <summary>Secret null/leer = unverändert; ClearSecret = entfernen.</summary>
    public sealed record TargetRequest(
        string Name, string Product, Guid? SiteId,
        string? Address, int? Port, string? Protocol, string? Enrollment, int? EnrollmentPort, string? Groups, string? ManagerVersion,
        string? SiteUrl, int? ReceiverPort, string? RegistrationUser, string? OnlyFrom, string? LegacyPackageUrl, string? TlsFingerprintSha256, string? SiteVersion,
        string? Secret, bool ClearSecret = false);

    public sealed record ClientDto(string Variant, string? Version, bool ServiceRunning, IReadOnlyList<string> Addresses, bool? Connected, string? Detail,
        bool CanConfigure, bool Held, DateTime SeenAt, DateTime? DisconnectedSince);

    public sealed record ProductStateDto(string Product, Guid? TargetId, string? TargetName, string State, ClientDto? Client);

    /// <param name="AgentSupports">Agent kennt das Modul monitoring (sonst zu alt).</param>
    /// <param name="ChangesAllowed">policy.json allowMonitoringChanges laut letztem Status (null = noch nicht gemeldet).</param>
    public sealed record HostRowDto(Guid HostId, string HostName, Guid SiteId, string? SiteName, OsFamily OsFamily, string? OsName, DateTime? LastSeenAt,
        bool AgentSupports, bool? ChangesAllowed, DateTime? ScannedAt, IReadOnlyList<ProductStateDto> Products);

    public sealed record OverviewDto(bool Enabled, IReadOnlyList<TargetDto> Targets, IReadOnlyList<HostRowDto> Hosts);

    /// <param name="Action">"install" (auch aktualisieren), "configure" oder "uninstall".</param>
    public sealed record ActionRequest(string Action, string Product, Guid[] HostIds);

    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var admin = api.MapGroup("").RequireAuthorization(UserAccounts.AdminPolicy);

        api.MapGet("/monitoring", Overview);
        api.MapGet("/hosts/{id:guid}/monitoring", ForHost);
        admin.MapPost("/monitoring/targets", Create);
        admin.MapPut("/monitoring/targets/{id:guid}", Update);
        admin.MapDelete("/monitoring/targets/{id:guid}", Delete);
        admin.MapPost("/monitoring/actions", Act);
    }

    private static async Task<OverviewDto> Overview(AppDbContext db, FeatureFlags features, CancellationToken ct)
    {
        var targets = await db.MonitoringTargets.AsNoTracking().Include(t => t.Site).OrderBy(t => t.Product).ThenBy(t => t.Site == null ? "" : t.Site.Name).ToListAsync(ct);
        var hosts = await db.Hosts.AsNoTracking().Include(h => h.Site).Where(h => !h.IsRevoked).OrderBy(h => h.Name).ToListAsync(ct);
        var clients = (await db.MonitoringClients.AsNoTracking().ToListAsync(ct)).ToLookup(c => c.HostId);
        return new OverviewDto(features.IsOn(Feature.MonitoringClients), targets.Select(ToDto).ToList(),
            hosts.Select(h => Row(h, targets, clients[h.Id].ToList())).ToList());
    }

    private static async Task<IResult> ForHost(Guid id, AppDbContext db, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().Include(h => h.Site).FirstOrDefaultAsync(h => h.Id == id, ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        var targets = await db.MonitoringTargets.AsNoTracking().ToListAsync(ct);
        var clients = await db.MonitoringClients.AsNoTracking().Where(c => c.HostId == id).ToListAsync(ct);
        return Results.Ok(Row(host, targets, clients));
    }

    public static HostRowDto Row(ManagedHost host, IReadOnlyList<MonitoringTarget> targets, IReadOnlyList<MonitoringClientState> clients)
    {
        var products = MonitoringProducts.All
            .Select(product =>
            {
                var target = MonitoringService.TargetFor(targets, host, product);
                var client = clients.FirstOrDefault(c => c.Product == product);
                return new ProductStateDto(product, target?.Id, target?.Name, MonitoringService.Evaluate(target, client),
                    client is null ? null : new ClientDto(client.Variant, client.Version, client.ServiceRunning, client.AddressList, client.Connected, client.Detail,
                        client.CanConfigure, client.Held, client.SeenAt, client.DisconnectedSince));
            })
            .ToList();
        return new HostRowDto(host.Id, host.Name, host.SiteId, host.Site?.Name, host.OsFamily, host.OsName, host.LastSeenAt,
            ModuleJson.Supports(host, AgentModules.Monitoring, ModuleActions.Change), host.MonitoringChangesAllowed, host.MonitoringScannedAt, products);
    }

    private static TargetDto ToDto(MonitoringTarget t) => new(
        t.Id, t.Name, t.Product, t.SiteId, t.Site?.Name,
        t.Address, t.Port, t.Protocol, t.Enrollment, t.EnrollmentPort, t.Groups, t.ManagerVersion,
        t.SiteUrl, t.ReceiverPort, t.RegistrationUser, t.OnlyFrom, t.LegacyPackageUrl, t.TlsFingerprintSha256, t.SiteVersion,
        t.ProtectedSecret is not null, t.UpdatedAt, t.LastError);

    // --- Ziele ----------------------------------------------------------------------------------------

    private static async Task<IResult> Create(TargetRequest request, AppDbContext db, AuditLog audit, IDataProtectionProvider protection, CancellationToken ct)
    {
        var target = new MonitoringTarget { Name = "", Product = request.Product };
        return await SaveAsync(target, request, isNew: true, db, audit, protection, ct);
    }

    private static async Task<IResult> Update(Guid id, TargetRequest request, AppDbContext db, AuditLog audit, IDataProtectionProvider protection, CancellationToken ct)
    {
        var target = await db.MonitoringTargets.FindAsync([id], ct);
        return target is null ? Results.NotFound() : await SaveAsync(target, request, isNew: false, db, audit, protection, ct);
    }

    private static async Task<IResult> SaveAsync(MonitoringTarget target, TargetRequest request, bool isNew, AppDbContext db, AuditLog audit,
        IDataProtectionProvider protection, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors);
        }

        if (request.SiteId is { } siteId && !await db.Sites.AnyAsync(s => s.Id == siteId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = [L.T("Site nicht gefunden.", "Site not found.")] });
        }

        if (await db.MonitoringTargets.AnyAsync(t => t.Id != target.Id && t.Product == request.Product && t.SiteId == request.SiteId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["siteId"] = [L.T("Für dieses Produkt und diese Site gibt es schon ein Ziel.", "There is already a target for this product and site.")],
            });
        }

        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        target.Name = request.Name.Trim();
        target.Product = request.Product;
        target.SiteId = request.SiteId;
        target.Address = Clean(request.Address);
        target.Port = request.Port;
        target.Protocol = Clean(request.Protocol)?.ToLowerInvariant();
        target.Enrollment = request.Enrollment == "keys" ? "keys" : "new";
        target.EnrollmentPort = request.EnrollmentPort;
        target.Groups = Clean(request.Groups);
        target.ManagerVersion = Clean(request.ManagerVersion)?.TrimStart('v', 'V');
        target.SiteUrl = Clean(request.SiteUrl)?.TrimEnd('/');
        target.ReceiverPort = request.ReceiverPort;
        target.RegistrationUser = Clean(request.RegistrationUser);
        target.OnlyFrom = MonitoringService.SplitOnlyFrom(request.OnlyFrom) is { Count: > 0 } onlyFrom ? string.Join(' ', onlyFrom) : null;
        target.LegacyPackageUrl = Clean(request.LegacyPackageUrl);
        target.TlsFingerprintSha256 = IntegrationHttp.NormalizeFingerprint(request.TlsFingerprintSha256);
        target.SiteVersion = Clean(request.SiteVersion)?.TrimStart('v', 'V');
        target.UpdatedAt = DateTime.UtcNow;
        if (request.ClearSecret)
        {
            target.ProtectedSecret = null;
        }
        else if (!string.IsNullOrEmpty(request.Secret))
        {
            target.ProtectedSecret = protection.CreateProtector(SecretPurpose).Protect(request.Secret);
        }

        if (isNew)
        {
            db.MonitoringTargets.Add(target);
        }

        // Nie das Geheimnis ins Audit-Log
        audit.Add(isNew ? "monitoring.target.created" : "monitoring.target.updated", $"{target.Product}: {target.Name}", siteId: target.SiteId);
        await db.SaveChangesAsync(ct);
        await db.Entry(target).Reference(t => t.Site).LoadAsync(ct);
        return Results.Ok(ToDto(target));
    }

    public static Dictionary<string, string[]> Validate(TargetRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        void Error(string field, string de, string en) => errors[field] = [L.T(de, en)];

        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100)
        {
            Error("name", "Name angeben (höchstens 100 Zeichen).", "Enter a name (at most 100 characters).");
        }

        if (!MonitoringProducts.All.Contains(r.Product))
        {
            Error("product", "Unbekanntes Produkt.", "Unknown product.");
            return errors;
        }

        if (r.Port is < 1 or > 65535)
        {
            Error("port", "Ungültiger Port.", "Invalid port.");
        }

        if (r.EnrollmentPort is < 1 or > 65535)
        {
            Error("enrollmentPort", "Ungültiger Port.", "Invalid port.");
        }

        if (r.ReceiverPort is < 1 or > 65535)
        {
            Error("receiverPort", "Ungültiger Port.", "Invalid port.");
        }

        if (r.Secret is { Length: > 512 } || r.Secret?.IndexOfAny(['\r', '\n']) >= 0)
        {
            Error("secret", "Ungültiges Passwort.", "Invalid password.");
        }

        if (!string.IsNullOrWhiteSpace(r.ManagerVersion) && MonitoringVersion.Parse(r.ManagerVersion) is null)
        {
            Error("managerVersion", "Version wie 4.9.2 angeben.", "Enter a version like 4.9.2.");
        }

        if (!string.IsNullOrWhiteSpace(r.SiteVersion) && MonitoringVersion.Parse(r.SiteVersion) is null)
        {
            Error("siteVersion", "Version wie 2.3.0p12 angeben.", "Enter a version like 2.3.0p12.");
        }

        if (r.Product == MonitoringProducts.Wazuh)
        {
            if (!HostLike(r.Address))
            {
                Error("address", "Adresse des Managers angeben (Hostname oder IP).", "Enter the manager address (host name or IP).");
            }

            if (r.Protocol is { Length: > 0 } protocol && protocol.ToLowerInvariant() is not ("tcp" or "udp"))
            {
                Error("protocol", "tcp oder udp.", "tcp or udp.");
            }

            if (!string.IsNullOrWhiteSpace(r.Groups) && !r.Groups.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ','))
            {
                Error("groups", "Gruppen durch Komma getrennt (Buchstaben, Ziffern, _ - .).", "Groups separated by commas (letters, digits, _ - .).");
            }

            return errors;
        }

        if (MonitoringService.ParseSiteUrl(r.SiteUrl) is null)
        {
            Error("siteUrl", "URL der Site wie https://cmk.example.com/mysite angeben.", "Enter the site URL like https://cmk.example.com/mysite.");
        }

        if (!string.IsNullOrWhiteSpace(r.RegistrationUser) && !r.RegistrationUser.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '@'))
        {
            Error("registrationUser", "Ungültiger Benutzername.", "Invalid user name.");
        }

        if (MonitoringService.SplitOnlyFrom(r.OnlyFrom).FirstOrDefault(v => !System.Net.IPAddress.TryParse(v, out _) && !System.Net.IPNetwork.TryParse(v, out _)) is { } bad)
        {
            Error("onlyFrom", $"„{bad}“ ist keine IP-Adresse und kein Netz.", $"\"{bad}\" is not an IP address or network.");
        }

        if (!string.IsNullOrWhiteSpace(r.LegacyPackageUrl) && !(Uri.TryCreate(r.LegacyPackageUrl, UriKind.Absolute, out var legacy) && legacy.Scheme is "https" or "http"))
        {
            Error("legacyPackageUrl", "Ungültige URL.", "Invalid URL.");
        }

        if (!IntegrationHttp.IsValidFingerprint(IntegrationHttp.NormalizeFingerprint(r.TlsFingerprintSha256)))
        {
            Error("tlsFingerprintSha256", "SHA-256-Fingerprint (64 Hex-Zeichen).", "SHA-256 fingerprint (64 hex characters).");
        }

        return errors;
    }

    private static bool HostLike(string? value) =>
        value is { Length: > 0 and <= 253 } && value.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']');

    private static async Task<IResult> Delete(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var target = await db.MonitoringTargets.FindAsync([id], ct);
        if (target is null)
        {
            return Results.NotFound();
        }

        db.MonitoringTargets.Remove(target);
        audit.Add("monitoring.target.deleted", $"{target.Product}: {target.Name}", siteId: target.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- Aktionen -------------------------------------------------------------------------------------

    private static async Task<IResult> Act(ActionRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, FeatureFlags features,
        IDataProtectionProvider protection, MonitoringPackages packages, CancellationToken ct)
    {
        if (!features.IsOn(Feature.MonitoringClients))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.MonitoringClients), statusCode: StatusCodes.Status403Forbidden);
        }

        var kind = request.Action switch
        {
            "install" => SystemChangeKind.MonitoringInstall,
            "configure" => SystemChangeKind.MonitoringConfigure,
            "uninstall" => SystemChangeKind.MonitoringUninstall,
            _ => (SystemChangeKind?)null,
        };
        if (kind is null || !MonitoringProducts.All.Contains(request.Product))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = [L.T("Unbekannte Aktion.", "Unknown action.")] });
        }

        if (request.HostIds is null || request.HostIds.Length == 0 || request.HostIds.Length > JobPlanner.MaxHostsPerRequest)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostIds"] = [L.T("Hosts auswählen.", "Select hosts.")] });
        }

        var targets = await db.MonitoringTargets.AsNoTracking().ToListAsync(ct);
        var name = request.Product == MonitoringProducts.Wazuh ? "Wazuh" : "Checkmk";
        var created = 0;
        var skipped = new List<JobPlanner.Skip>();
        Guid? lastJob = null;

        foreach (var hostId in request.HostIds.Distinct())
        {
            var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hostId, ct);
            if (host is null)
            {
                skipped.Add(new JobPlanner.Skip(hostId, hostId.ToString(), JobPlanner.HostNotFound));
                continue;
            }

            var (change, secret, label, reason) = await PrepareAsync(kind.Value, request.Product, name, host, targets, protection, packages, ct);
            if (reason is not null)
            {
                skipped.Add(new JobPlanner.Skip(host.Id, host.Name, reason));
                continue;
            }

            var protectedSecret = secret is null ? null : ComplianceEndpoints.SecretProtector(protection).Protect(secret);
            var (job, skip) = await ComplianceEndpoints.PlanChangeAsync(db, host.Id, change!, protectedSecret, null, label!, user, ct);
            if (skip is not null)
            {
                skipped.Add(skip);
                continue;
            }

            created++;
            lastJob = job!.Id;
            audit.Add($"monitoring.{request.Action}", $"{label} on {host.Name}", siteId: host.SiteId, hostId: host.Id);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ComplianceEndpoints.ChangeResultDto(created, skipped, created == 1 ? lastJob : null));
    }

    /// <summary>Änderung für einen Host bauen. reason = warum nicht (Host wird übersprungen).</summary>
    public static async Task<(SystemChange? Change, string? Secret, string? Label, string? Reason)> PrepareAsync(SystemChangeKind kind, string product, string name,
        ManagedHost host, IReadOnlyList<MonitoringTarget> targets, IDataProtectionProvider protection, MonitoringPackages packages, CancellationToken ct)
    {
        if (!ModuleJson.Supports(host, AgentModules.Monitoring, ModuleActions.Change) || !ModuleJson.Usable(host, AgentModules.Monitoring))
        {
            return (null, null, null, L.T("Der Agent kann noch keine Monitoring-Clients verwalten (zu alt oder Modul gesperrt).",
                "The agent cannot manage monitoring clients yet (too old or module disabled)."));
        }

        if (host.MonitoringChangesAllowed == false)
        {
            return (null, null, null, L.T("Auf dem Host nicht freigegeben: policy set allowMonitoringChanges true",
                "Not allowed on the host: policy set allowMonitoringChanges true"));
        }

        if (kind == SystemChangeKind.MonitoringUninstall)
        {
            return (new SystemChange(kind, Monitoring: new MonitoringSpec(product)), null, L.T($"{name}-Client deinstallieren", $"Uninstall {name} client"), null);
        }

        if (MonitoringService.TargetFor(targets, host, product) is not { } target)
        {
            return (null, null, null, L.T($"Kein {name}-Ziel für diesen Host (Einstellungen → Integrationen).", $"No {name} target for this host (Settings → Integrations)."));
        }

        string? secret = null;
        if (target.ProtectedSecret is { } stored)
        {
            try
            {
                secret = protection.CreateProtector(SecretPurpose).Unprotect(stored);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return (null, null, null, L.T("Das gespeicherte Passwort des Ziels ist nicht mehr lesbar; bitte neu eingeben.",
                    "The target's stored password can no longer be read; enter it again."));
            }
        }

        if (product == MonitoringProducts.Wazuh && target.Address is null)
        {
            return (null, null, null, L.T("Im Ziel fehlt die Manager-Adresse.", "The target has no manager address."));
        }

        if (product == MonitoringProducts.Checkmk && (target.RegistrationUser is null || secret is null) && target.OnlyFrom is null)
        {
            return (null, null, null, L.T("Im Ziel fehlen Registrierungsbenutzer und Passwort (bzw. only_from für Legacy-Agents).",
                "The target needs a registration user and password (or only_from for legacy agents)."));
        }

        MonitoringPackage? package = null;
        if (kind == SystemChangeKind.MonitoringInstall)
        {
            try
            {
                package = await packages.GetAsync(target, host, ct);
            }
            catch (IntegrationException e)
            {
                return (null, null, null, e.Message);
            }
        }

        var spec = MonitoringService.Spec(target, host, package);
        var label = kind == SystemChangeKind.MonitoringInstall
            ? L.T($"{name}-Client installieren ({package!.Version ?? package.FileName})", $"Install {name} client ({package!.Version ?? package.FileName})")
            : L.T($"{name}-Client auf {target.Name} umstellen", $"Point {name} client to {target.Name}");
        return (new SystemChange(kind, Monitoring: spec), secret, label, null);
    }
}
