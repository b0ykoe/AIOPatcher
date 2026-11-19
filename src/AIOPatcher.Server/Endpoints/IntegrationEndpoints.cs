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

/// <summary>Proxmox-VE-Cluster mit Backup-Jobs (und mit Schalter: Gäste ohne Job, fehlgeschlagene Läufe).</summary>
    public sealed record BackupClusterDto(Guid Id, string Name, string? SiteName, bool Enabled, PveBackupStatus? Status);

    /// <summary>Inventar → Backups: Proxmox Backup Server und Backup-Jobs der Proxmox-VE-Cluster.</summary>
    /// <param name="ChecksEnabled">Schalter "Backup-Jobs prüfen".</param>
    public sealed record BackupOverviewDto(bool ChecksEnabled, IReadOnlyList<BackupServerDto> Servers, IReadOnlyList<BackupClusterDto> Clusters);

    /// <summary>Proxmox Backup Server mit vollem Zustand (Inventar → Backups).</summary>
    public sealed record BackupServerDto(Guid Id, string Name, string? SiteName, string? BaseUrl, bool Enabled, DateTime? LastCheckAt, string? LastError,
        PbsStatus? Status);

    /// <summary>Secret leer lassen = unverändert (beim Bearbeiten).</summary>
    public sealed record IntegrationRequest(string Name, IntegrationType Type, Guid? SiteId, string? BaseUrl, string? Secret, string? TlsFingerprintSha256, int KeepSnapshots, bool Enabled);

    public sealed record TestResultDto(bool Ok, string Message, string? PresentedFingerprint);
    public sealed record MappingRequest(Guid? IntegrationId, string? ExternalId);
    /// <param name="Unmapped">VMs bzw. Server, die danach noch keinen Host haben (Name passt nicht: von Hand zuordnen).</param>
    public sealed record AutoMapResultDto(int Mapped, IReadOnlyList<string> Hosts, int Unmapped = 0);

    /// <summary>Zuordnung in den Einstellungen: alle VMs bzw. Server einer Integration und die wählbaren Hosts.</summary>
    public sealed record MappingOverviewDto(IReadOnlyList<MappingMachineDto> Machines, IReadOnlyList<MappingHostDto> Hosts);

    /// <param name="Missing">Einem Host zugeordnet, aber beim Anbieter nicht (mehr) vorhanden.</param>
    public sealed record MappingMachineDto(string ExternalId, string Name, string? Status, Guid? HostId, string? HostName, bool Manual, bool Missing);

    /// <param name="MappedTo">Schon zugeordnet: Name der VM bzw. "Integration · Maschine" bei einer anderen Integration.</param>
    public sealed record MappingHostDto(Guid Id, string Name, string Hostname, Guid? IntegrationId, string? ExternalId, string? MappedTo, string? SiteName = null);

    /// <summary>HostId null = Zuordnung dieser Maschine lösen.</summary>
    public sealed record MachineMappingRequest(string ExternalId, Guid? HostId);

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
        admin.MapPost("/{id:guid}/backup-jobs/refresh", RefreshBackupJobs);

        var read = app.MapGroup("/api/integrations").RequireAuthorization();
        read.MapGet("/", List);
        read.MapGet("/{id:guid}/machines", Machines).RequireAuthorization(UserAccounts.OperatorPolicy);
        read.MapGet("/{id:guid}/mapping", MappingOverview).RequireAuthorization(UserAccounts.OperatorPolicy);
        read.MapPut("/{id:guid}/mapping", SetMachineMapping).RequireAuthorization(UserAccounts.OperatorPolicy);

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

    /// <summary>Mit siteId: Backup-Server und Cluster dieser Site und solche ohne Site.</summary>
    private static async Task<BackupOverviewDto> BackupServers(Guid? siteId, AppDbContext db, FeatureFlags features, CancellationToken ct)
    {
        var integrations = await db.Integrations.AsNoTracking().Include(i => i.Site)
            .Where(i => i.Type == IntegrationType.ProxmoxBackup || i.Type == IntegrationType.Proxmox)
            .Where(i => siteId == null || i.SiteId == null || i.SiteId == siteId)
            .OrderBy(i => i.Name).ToListAsync(ct);
        return new BackupOverviewDto(features.IsOn(Feature.BackupJobChecks),
            integrations.Where(i => i.Type == IntegrationType.ProxmoxBackup)
                .Select(i => new BackupServerDto(i.Id, i.Name, i.Site?.Name, i.BaseUrl, i.Enabled, i.LastCheckAt, i.LastError, ProxmoxBackupMonitor.Parse(i.StatusJson)))
                .ToList(),
            integrations.Where(i => i.Type == IntegrationType.Proxmox)
                .Select(i => new BackupClusterDto(i.Id, i.Name, i.Site?.Name, i.Enabled, ProxmoxBackupJobsMonitor.Parse(i.StatusJson)))
                .ToList());
    }

    private static async Task<IResult> RefreshBackupJobs(Guid id, AppDbContext db, ProxmoxBackupJobsMonitor monitor, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is not { Type: IntegrationType.Proxmox })
        {
            return Results.NotFound();
        }

        var status = await monitor.RefreshAsync(db, integration, ct);
        return status.Error is { } error ? Results.Problem(error, statusCode: StatusCodes.Status502BadGateway) : Results.NoContent();
    }

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

        await db.Hosts.Where(h => h.IntegrationId == id).ExecuteUpdateAsync(s => s
            .SetProperty(h => h.IntegrationId, (Guid?)null)
            .SetProperty(h => h.ExternalId, (string?)null)
            .SetProperty(h => h.MappingManual, false), ct);
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

        // Von Hand gesetzte oder gelöste Zuordnungen bleiben unverändert, schon vergebene VMs werden nicht doppelt vergeben.
        var query = db.Hosts.Where(h => h.IntegrationId == null && !h.MappingManual && !h.IsRevoked);
        if (integration.SiteId is { } siteId)
        {
            query = query.Where(h => h.SiteId == siteId);
        }

        var taken = await db.Hosts.Where(h => h.IntegrationId == id && h.ExternalId != null).Select(h => h.ExternalId!).ToListAsync(ct);
        var mapped = HostMapping.AutoMap(integration.Id, await query.ToListAsync(ct), machines, taken).Select(h => h.Name).ToList();
        var unmapped = machines.Select(m => m.ExternalId).Distinct().Count(e => !taken.Contains(e)) - mapped.Count;

        audit.Add("integration.auto-map", $"{integration.Name}: {mapped.Count} hosts");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AutoMapResultDto(mapped.Count, mapped, Math.Max(0, unmapped)));
    }

    private static bool Mappable(IntegrationType type) => type is IntegrationType.Proxmox or IntegrationType.HetznerCloud;

    /// <summary>
    /// Alle VMs bzw. Server der Integration mit ihrem Host, dazu alle Hosts zur Auswahl (bewusst auch aus anderen Sites:
    /// von Hand darf jede VM jedem Host zugeordnet werden, z. B. "win2025-warz" → "WARZ-APP").
    /// </summary>
    private static async Task<IResult> MappingOverview(Guid id, AppDbContext db, VirtualizationProviderFactory providers, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null || !Mappable(integration.Type) || providers.For(integration) is not { } provider)
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

        var hosts = await db.Hosts.AsNoTracking().Include(h => h.Integration).Include(h => h.Site)
            .Where(h => !h.IsRevoked)
            .OrderBy(h => h.Name)
            .ToListAsync(ct);
        return Results.Ok(BuildOverview(id, machines, hosts));
    }

    /// <summary>Zeilen für die Zuordnungstabelle; Zuordnungen auf Maschinen, die der Anbieter nicht mehr kennt, als "Missing".</summary>
    public static MappingOverviewDto BuildOverview(Guid integrationId, IReadOnlyList<MachineInfo> machines, IReadOnlyList<ManagedHost> hosts)
    {
        var byExternal = hosts.Where(h => h.IntegrationId == integrationId && h.ExternalId != null)
            .GroupBy(h => h.ExternalId!).ToDictionary(g => g.Key, g => g.First());
        var names = machines.GroupBy(m => m.ExternalId).ToDictionary(g => g.Key, g => g.First().Name);

        var rows = machines
            .Select(m => byExternal.GetValueOrDefault(m.ExternalId) is { } h
                ? new MappingMachineDto(m.ExternalId, m.Name, m.Status, h.Id, h.Name, h.MappingManual, false)
                : new MappingMachineDto(m.ExternalId, m.Name, m.Status, null, null, false, false))
            .Concat(byExternal.Where(x => !names.ContainsKey(x.Key))
                .Select(x => new MappingMachineDto(x.Key, x.Key, null, x.Value.Id, x.Value.Name, x.Value.MappingManual, true)))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hostRows = hosts
            .Select(h => new MappingHostDto(h.Id, h.Name, h.Hostname, h.IntegrationId, h.ExternalId,
                h.IntegrationId is null ? null
                : h.IntegrationId == integrationId && h.ExternalId != null && names.TryGetValue(h.ExternalId, out var vm) ? vm
                : $"{h.Integration?.Name} · {h.ExternalId}",
                h.Site?.Name))
            .ToList();
        return new MappingOverviewDto(rows, hostRows);
    }

    /// <summary>Settings → Integrations → Zuordnen: Maschine einem Host zuordnen (HostId) oder ihre Zuordnung lösen (null).</summary>
    private static async Task<IResult> SetMachineMapping(Guid id, MachineMappingRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var integration = await db.Integrations.FindAsync([id], ct);
        if (integration is null || !Mappable(integration.Type))
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.ExternalId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["externalId"] = [L.T("Maschine wählen.", "Choose a machine.")] });
        }

        var externalId = HostMapping.Normalize(request.ExternalId);
        if (request.HostId is { } hostId)
        {
            var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId && !h.IsRevoked, ct);
            if (host is null)
            {
                return Results.NotFound();
            }

            var previous = await HostMapping.AssignAsync(db, host, id, externalId, ct);
            audit.Add("host.mapping", $"{host.Name}: {integration.Name} · {externalId}", siteId: host.SiteId, hostId: host.Id);
            if (previous is not null)
            {
                audit.Add("host.mapping", $"{previous.Name}: -", siteId: previous.SiteId, hostId: previous.Id);
            }
        }
        else
        {
            foreach (var host in await db.Hosts.Where(h => h.IntegrationId == id && h.ExternalId == externalId).ToListAsync(ct))
            {
                HostMapping.Unassign(host);
                audit.Add("host.mapping", $"{host.Name}: -", siteId: host.SiteId, hostId: host.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
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

            var previous = await HostMapping.AssignAsync(db, host, integrationId, request.ExternalId, ct);
            if (previous is not null)
            {
                audit.Add("host.mapping", $"{previous.Name}: -", siteId: previous.SiteId, hostId: previous.Id);
            }
        }
        else
        {
            HostMapping.Unassign(host);
        }

        audit.Add("host.mapping", $"{host.Name}: {host.ExternalId ?? "-"}", siteId: host.SiteId, hostId: host.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
