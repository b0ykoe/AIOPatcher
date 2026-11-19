using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Proxmox, Hetzner Cloud und OPNsense: Verbindungen (Admin) und Zuordnung von Hosts (Operator).</summary>
public static class IntegrationEndpoints
{
    public sealed record IntegrationDto(
        Guid Id, string Name, IntegrationType Type, Guid? SiteId, string? BaseUrl, string? TlsFingerprintSha256, int KeepSnapshots,
        bool Enabled, DateTime? LastCheckAt, string? LastError, int MappedHosts, OpnsenseFirmware? Firmware, BackupSummaryDto? Backup = null);

    /// <summary>Proxmox Backup Server: Kurzfassung für die Karte in den Einstellungen.</summary>
    public sealed record BackupSummaryDto(string? Version, int Datastores, int FailedTasks, int StaleGroups, int Groups, DateTime CheckedAt);

    /// <summary>Proxmox Backup Server mit vollem Zustand (Inventar → Backups).</summary>
    public sealed record BackupServerDto(Guid Id, string Name, string? SiteName, string? BaseUrl, bool Enabled, DateTime? LastCheckAt, string? LastError,
        PbsStatus? Status);

    /// <summary>Secret leer lassen = unverändert (beim Bearbeiten).</summary>
    public sealed record IntegrationRequest(string Name, IntegrationType Type, Guid? SiteId, string? BaseUrl, string? Secret, string? TlsFingerprintSha256, int KeepSnapshots, bool Enabled);

    public sealed record TestResultDto(bool Ok, string Message, string? PresentedFingerprint);
    public sealed record MappingRequest(Guid? IntegrationId, string? ExternalId);
    public sealed record AutoMapResultDto(int Mapped, IReadOnlyList<string> Hosts);

    /// <summary>
    /// Proxmox-API-Token muss "benutzer@realm!token-id=secret" sein. Häufigster Fehler: nur das Secret (UUID) eingefügt,
    /// das endet in HTTP 401. null = Format passt.
    /// </summary>
    public static string? ProxmoxTokenProblem(string token)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^[^@\s!=]+@[^!\s=]+![^=\s]+=\S+$"))
        {
            return null;
        }

        return Guid.TryParse(token, out _)
            ? L.T("Das ist nur das Secret. Proxmox braucht Benutzer, Token-ID und Secret: root@pam!aiopatcher=" + token,
                "That is only the secret. Proxmox needs user, token ID and secret: root@pam!aiopatcher=" + token)
            : L.T("Format: benutzer@realm!token-id=secret, z. B. root@pam!aiopatcher=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
                "Format: user@realm!token-id=secret, e.g. root@pam!aiopatcher=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx");
    }

    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/integrations").RequireAuthorization(UserAccounts.AdminPolicy);
        admin.MapPost("/", Create);
        admin.MapPut("/{id:guid}", Update);
        admin.MapDelete("/{id:guid}", Delete);
        admin.MapPost("/{id:guid}/test", Test);
        admin.MapPost("/{id:guid}/auto-map", AutoMap);
        admin.MapPost("/{id:guid}/opnsense/refresh", RefreshOpnsense);
        admin.MapPost("/{id:guid}/opnsense/update", UpdateOpnsense);
        admin.MapPost("/{id:guid}/pbs/refresh", RefreshBackup);

        var read = app.MapGroup("/api/integrations").RequireAuthorization();
        read.MapGet("/", List);
        read.MapGet("/{id:guid}/machines", Machines).RequireAuthorization(UserAccounts.OperatorPolicy);

        app.MapPut("/api/hosts/{id:guid}/mapping", SetMapping).RequireAuthorization(UserAccounts.OperatorPolicy);
        app.MapGet("/api/backups", BackupServers).RequireAuthorization();
    }

    private static BackupSummaryDto? Summary(Integration integration)
    {
        if (integration.Type != IntegrationType.ProxmoxBackup || ProxmoxBackupMonitor.Parse(integration.StatusJson) is not { } status)
        {
            return null;
        }

        return new BackupSummaryDto(status.Version, status.Datastores.Count, status.FailedTasks.Count,
            status.Groups.Count(g => ProxmoxBackupMonitor.IsStale(g, status.CheckedAt)), status.Groups.Count, status.CheckedAt);
    }

    private static async Task<List<BackupServerDto>> BackupServers(AppDbContext db, CancellationToken ct) =>
        (await db.Integrations.AsNoTracking().Include(i => i.Site).Where(i => i.Type == IntegrationType.ProxmoxBackup).OrderBy(i => i.Name).ToListAsync(ct))
        .Select(i => new BackupServerDto(i.Id, i.Name, i.Site?.Name, i.BaseUrl, i.Enabled, i.LastCheckAt, i.LastError, ProxmoxBackupMonitor.Parse(i.StatusJson)))
        .ToList();

    private static async Task<IResult> RefreshBackup(Guid id, AppDbContext db, ProxmoxBackupMonitor monitor, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is not { Type: IntegrationType.ProxmoxBackup })
        {
            return Results.NotFound();
        }

        await monitor.RefreshAsync(db, integration, ct);
        return integration.LastError is { } error ? Results.Problem(error, statusCode: StatusCodes.Status502BadGateway) : Results.NoContent();
    }

    private static async Task<List<IntegrationDto>> List(AppDbContext db, CancellationToken ct) =>
        (await db.Integrations
            .OrderBy(i => i.Name)
            .Select(i => new { Integration = i, Mapped = db.Hosts.Count(h => h.IntegrationId == i.Id) })
            .ToListAsync(ct))
        .Select(x => new IntegrationDto(x.Integration.Id, x.Integration.Name, x.Integration.Type, x.Integration.SiteId, x.Integration.BaseUrl,
            x.Integration.TlsFingerprintSha256, x.Integration.KeepSnapshots, x.Integration.Enabled, x.Integration.LastCheckAt, x.Integration.LastError, x.Mapped,
            x.Integration.Type == IntegrationType.Opnsense ? OpnsenseMonitor.Parse(x.Integration.StatusJson) : null, Summary(x.Integration)))
        .ToList();

    private static async Task<IResult> RefreshOpnsense(Guid id, AppDbContext db, OpnsenseMonitor monitor, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is not { Type: IntegrationType.Opnsense })
        {
            return Results.NotFound();
        }

        var firmware = await monitor.RefreshAsync(db, integration, checkForUpdates: true, ct);
        return firmware is null
            ? Results.Problem(integration.LastError, statusCode: StatusCodes.Status502BadGateway)
            : Results.Ok(firmware);
    }

    /// <summary>Firmware-Update starten. Die Firewall installiert und startet ggf. selbst neu (kurze Unterbrechung!).</summary>
    private static async Task<IResult> UpdateOpnsense(Guid id, AppDbContext db, OpnsenseClientFactory clients, AuditLog audit, FeatureFlags features, CancellationToken ct)
    {
        if (!features.IsOn(Feature.OpnsenseUpdates))
        {
            return Results.Problem(FeatureFlags.DisabledMessage(Feature.OpnsenseUpdates), statusCode: StatusCodes.Status403Forbidden);
        }

        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is not { Type: IntegrationType.Opnsense })
        {
            return Results.NotFound();
        }

        try
        {
            await clients.For(integration).StartUpdateAsync(ct);
        }
        catch (IntegrationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        audit.Add("opnsense.update-started", integration.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Create(IntegrationRequest request, AppDbContext db, IntegrationHttp http, AuditLog audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["secret"] = [L.T("Zugangsdaten angeben.", "Enter credentials.")] });
        }

        var integration = new Integration { Name = "", ProtectedSecret = "" };
        if (Apply(integration, request, http) is { } problem)
        {
            return problem;
        }

        db.Integrations.Add(integration);
        audit.Add("integration.created", $"{integration.Name} ({integration.Type})");
        await db.SaveChangesAsync(ct);
        return Results.Ok(integration.Id);
    }

    private static async Task<IResult> Update(Guid id, IntegrationRequest request, AppDbContext db, IntegrationHttp http, AuditLog audit, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null)
        {
            return Results.NotFound();
        }

        if (Apply(integration, request, http) is { } problem)
        {
            return problem;
        }

        audit.Add("integration.updated", $"{integration.Name} ({integration.Type})");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult? Apply(Integration integration, IntegrationRequest request, IntegrationHttp http)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        if (!Enum.IsDefined(request.Type))
        {
            errors["type"] = [L.T("Unbekannter Typ.", "Unknown type.")];
        }

        var baseUrl = request.BaseUrl?.Trim().TrimEnd('/');
        if (request.Type != IntegrationType.HetznerCloud || !string.IsNullOrEmpty(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            {
                errors["baseUrl"] = [L.T("https-URL angeben, z. B. https://pve.example:8006", "Enter an https URL, e.g. https://pve.example:8006")];
            }
        }

        var fingerprint = IntegrationHttp.NormalizeFingerprint(request.TlsFingerprintSha256);
        if (!IntegrationHttp.IsValidFingerprint(fingerprint))
        {
            errors["tlsFingerprintSha256"] = [L.T("SHA-256-Fingerprint: 64 Hex-Zeichen.", "SHA-256 fingerprint: 64 hex characters.")];
        }

        if (request.Type == IntegrationType.Proxmox && !string.IsNullOrWhiteSpace(request.Secret) && ProxmoxTokenProblem(request.Secret.Trim()) is { } tokenProblem)
        {
            errors["secret"] = [tokenProblem];
        }

        if (request.Type == IntegrationType.ProxmoxBackup && !string.IsNullOrWhiteSpace(request.Secret) && ProxmoxBackupClient.TokenProblem(request.Secret) is { } pbsProblem)
        {
            errors["secret"] = [pbsProblem];
        }

        if (request.KeepSnapshots is < 1 or > 20)
        {
            errors["keepSnapshots"] = [L.T("1 bis 20 Snapshots behalten.", "Keep 1 to 20 snapshots.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        integration.Name = name;
        integration.Type = request.Type;
        integration.SiteId = request.SiteId;
        integration.BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl;
        integration.TlsFingerprintSha256 = fingerprint;
        integration.KeepSnapshots = request.KeepSnapshots;
        integration.Enabled = request.Enabled;
        if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            integration.ProtectedSecret = http.Protect(request.Secret.Trim());
        }

        return null;
    }

    private static async Task<IResult> Delete(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null)
        {
            return Results.NotFound();
        }

        await db.Hosts.Where(h => h.IntegrationId == id).ExecuteUpdateAsync(s => s.SetProperty(h => h.IntegrationId, (Guid?)null).SetProperty(h => h.ExternalId, (string?)null), ct);
        db.Integrations.Remove(integration);
        audit.Add("integration.deleted", integration.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Test(Guid id, AppDbContext db, VirtualizationProviderFactory providers, OpnsenseClientFactory opnsense,
        ProxmoxBackupClientFactory pbs, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null)
        {
            return Results.NotFound();
        }

        TestResultDto result;
        try
        {
            var message = integration.Type switch
            {
                IntegrationType.Opnsense => await opnsense.For(integration).TestAsync(ct),
                IntegrationType.ProxmoxBackup => await pbs.For(integration).TestAsync(ct),
                _ => await providers.For(integration)!.TestAsync(ct),
            };
            result = new TestResultDto(true, message, null);
            integration.LastError = null;
        }
        catch (IntegrationException ex)
        {
            result = new TestResultDto(false, ex.Message, ex.PresentedFingerprint);
            integration.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        integration.LastCheckAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> Machines(Guid id, AppDbContext db, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null || providers.For(integration) is not { } provider)
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Ok(await provider.ListMachinesAsync(ct));
        }
        catch (IntegrationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Ordnet Hosts ohne Zuordnung über den Namen zu (Hostname ohne Domain = VM-/Servername, Groß-/Kleinschreibung egal).</summary>
    private static async Task<IResult> AutoMap(Guid id, AppDbContext db, VirtualizationProviderFactory providers, AuditLog audit, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null || providers.For(integration) is not { } provider)
        {
            return Results.NotFound();
        }

        IReadOnlyList<MachineInfo> machines;
        try
        {
            machines = await provider.ListMachinesAsync(ct);
        }
        catch (IntegrationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        var byName = machines.GroupBy(m => m.Name.ToLowerInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var query = db.Hosts.Where(h => h.IntegrationId == null && !h.IsRevoked);
        if (integration.SiteId is { } siteId)
        {
            query = query.Where(h => h.SiteId == siteId);
        }

        var mapped = new List<string>();
        foreach (var host in await query.ToListAsync(ct))
        {
            var candidates = new[] { host.Name, host.Hostname, host.Hostname.Split('.')[0] }.Select(n => n.ToLowerInvariant());
            var machine = candidates.Select(c => byName.GetValueOrDefault(c)).FirstOrDefault(m => m is not null);
            if (machine is not null)
            {
                host.IntegrationId = integration.Id;
                host.ExternalId = machine.ExternalId;
                mapped.Add(host.Name);
            }
        }

        audit.Add("integration.auto-map", $"{integration.Name}: {mapped.Count} hosts");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AutoMapResultDto(mapped.Count, mapped));
    }

    private static async Task<IResult> SetMapping(Guid id, MappingRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var host = await db.Hosts.FindAsync([id], ct);
        if (host is null)
        {
            return Results.NotFound();
        }

        if (request.IntegrationId is { } integrationId)
        {
            if (!await db.Integrations.AnyAsync(i => i.Id == integrationId && (i.Type == IntegrationType.Proxmox || i.Type == IntegrationType.HetznerCloud), ct) || string.IsNullOrWhiteSpace(request.ExternalId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["externalId"] = [L.T("Integration und Maschine wählen.", "Choose integration and machine.")] });
            }

            host.IntegrationId = integrationId;
            host.ExternalId = request.ExternalId.Trim().Length > 100 ? request.ExternalId.Trim()[..100] : request.ExternalId.Trim();
        }
        else
        {
            host.IntegrationId = null;
            host.ExternalId = null;
        }

        audit.Add("host.mapping", $"{host.Name}: {host.ExternalId ?? "-"}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
