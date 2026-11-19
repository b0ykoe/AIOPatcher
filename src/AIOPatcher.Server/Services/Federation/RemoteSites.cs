using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Federation;

/// <summary>
/// Site-Sharing, Seite B (uns wurde geteilt): Einladung annehmen, Stand abgleichen, Ereignisse in eigene
/// Benachrichtigungen übersetzen, Aktionen an A weiterreichen. Daten von A sind fremd: nur anzeigen, nie in die
/// eigene Automatik (Auto-Patch, Abläufe, Regeln) übernehmen.
/// </summary>
public sealed class RemoteSites(AppDbContext db, FederationClient client, ServerIdentity identity, AuditLog audit, Notifier notifier, ILogger<RemoteSites> logger)
{
    /// <summary>Ab hier gilt der Stand als veraltet bzw. die Gegenstelle als nicht erreichbar.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromHours(1);

    public static FederationTarget Target(RemoteInstance i) => new(i.BaseUrl, i.CaFingerprint, i.CaCertificatePem, i.CertificatePem, i.ProtectedKey);

    public static string Status(RemoteSite site, DateTime now) =>
        site.EndedAt is not null ? "Ended"
        : site.ExpiresAt <= now ? "Ended"
        : site.Instance?.LastSuccessAt is not { } ok ? "Offline"
        : now - ok > OfflineAfter ? "Offline"
        : now - ok > StaleAfter ? "Stale"
        : "Live";

    public static SiteSnapshot? Snapshot(RemoteSite site)
    {
        if (site.SnapshotJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SiteSnapshot>(site.SnapshotJson, FederationProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- Annehmen -----------------------------------------------------------------------------------

    public sealed record Preview(string InstanceName, string Version, string BaseUrl, string CaFingerprint, string[] Capabilities, bool AlreadyConnected);

    public async Task<Preview> PreviewAsync(string link, CancellationToken ct)
    {
        var parsed = FederationProtocol.ParseLink(link) ?? throw new FederationException(400, InvalidLink);
        var info = await client.GetInfoAsync(parsed.BaseUrl, parsed.CaFingerprint, ct);
        if (info.InstanceId == identity.InstanceId)
        {
            throw new FederationException(400, L.T("Das ist ein Link dieser Instanz.", "This link belongs to this instance."));
        }

        var known = await db.RemoteInstances.AnyAsync(i => i.InstanceId == info.InstanceId && i.CaFingerprint == parsed.CaFingerprint, ct);
        return new Preview(info.InstanceName, info.Version, parsed.BaseUrl, parsed.CaFingerprint, info.Capabilities, known);
    }

    private static string InvalidLink => L.T("Kein gültiger Einladungslink (https://…/share/…#…).", "Not a valid invite link (https://…/share/…#…).");

    public async Task<RemoteSite> AddAsync(string link, string code, string user, DateTime now, CancellationToken ct)
    {
        var parsed = FederationProtocol.ParseLink(link) ?? throw new FederationException(400, InvalidLink);
        var info = await client.GetInfoAsync(parsed.BaseUrl, parsed.CaFingerprint, ct);
        if (info.InstanceId == identity.InstanceId)
        {
            throw new FederationException(400, L.T("Das ist ein Link dieser Instanz.", "This link belongs to this instance."));
        }

        if (!info.Protocols.Contains(FederationProtocol.Version))
        {
            throw new FederationException(400, L.T($"Die andere Instanz ({info.Version}) spricht ein anderes Protokoll. Beide auf den aktuellen Stand bringen.",
                $"The other instance ({info.Version}) speaks a different protocol. Update both."));
        }

        var instance = await db.RemoteInstances.Include(i => i.Sites)
            .SingleOrDefaultAsync(i => i.InstanceId == info.InstanceId && i.CaFingerprint == parsed.CaFingerprint, ct);
        var request = new JoinRequest(parsed.InviteId, code, null, identity.InstanceId, identity.Name, AgentDownloads.AgentVersion);
        JoinResponse response;
        if (instance is not null)
        {
            // Schon verbunden (andere Site derselben Instanz): mit dem vorhandenen Zertifikat signieren.
            instance.BaseUrl = parsed.BaseUrl;
            response = await JoinAsync(Target(instance), request, sign: true, ct);
        }
        else
        {
            var key = client.CreateKey();
            var target = new FederationTarget(parsed.BaseUrl, parsed.CaFingerprint, info.CaCertificatePem, null, null);
            response = await JoinAsync(target, request with { CsrPem = key.CsrPem }, sign: false, ct);
            if (response.CertificatePem is null)
            {
                throw new FederationException(0, "No certificate in the answer.");
            }

            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(response.CertificatePem);
            instance = new RemoteInstance
            {
                InstanceId = info.InstanceId,
                Name = FederationProtocol.CleanName(info.InstanceName, 100),
                BaseUrl = parsed.BaseUrl,
                CaFingerprint = parsed.CaFingerprint,
                CaCertificatePem = info.CaCertificatePem,
                ProtectedKey = key.ProtectedKey,
                CertificatePem = response.CertificatePem,
                CertificateExpiresAt = certificate.NotAfter.ToUniversalTime(),
                CreatedBy = user,
                CreatedAt = now,
            };
            db.RemoteInstances.Add(instance);
        }

        instance.Version = FederationProtocol.CleanName(info.Version, 32);
        instance.Capabilities = string.Join(',', info.Capabilities);
        instance.LastSuccessAt = now;
        instance.LastSyncAt = now;
        instance.LastError = null;

        var share = response.Share;
        var site = instance.Sites.FirstOrDefault(s => s.RemoteSiteId == share.SiteId);
        if (site is null)
        {
            site = new RemoteSite { Name = share.SiteName, CreatedBy = user, CreatedAt = now, Instance = instance };
            instance.Sites.Add(site);
        }

        Apply(site, share);
        site.EndedAt = null;
        site.JobsCheckedUntil = now; // nur Ereignisse ab jetzt melden
        audit.Add("remote-site.added", $"{share.SiteName} from {instance.Name} ({share.Level})");
        await db.SaveChangesAsync(ct);

        await TrySnapshotAsync(instance, site, now, notify: false, ct);
        await db.SaveChangesAsync(ct);
        return site;
    }

    /// <summary>Annahme bei A; dessen (englische, sprachneutrale) Ablehnungen in die Sprache dieser Instanz übersetzen.</summary>
    private async Task<JoinResponse> JoinAsync(FederationTarget target, JoinRequest request, bool sign, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync<JoinResponse>(target, HttpMethod.Post, FederationProtocol.JoinPath, request, ct, sign);
        }
        catch (FederationException ex) when (ex.Status is 403 or 404 or 429)
        {
            throw new FederationException(ex.Status, JoinError(ex));
        }
    }

    public static string JoinError(FederationException ex)
    {
        var left = System.Text.RegularExpressions.Regex.Match(ex.Message, @"\((\d+) attempts left\)");
        return ex.Status switch
        {
            404 => L.T("Einladung ungültig, schon benutzt oder abgelaufen. Bitte um eine neue.", "Invite is invalid, already used or expired. Ask for a new one."),
            429 => L.T("Zu viele Versuche. Bitte kurz warten.", "Too many attempts. Please wait a moment."),
            403 when left.Success => L.T($"Falscher Code (noch {left.Groups[1].Value} Versuche).", $"Wrong code ({left.Groups[1].Value} attempts left)."),
            403 when ex.Message.Contains("no longer valid", StringComparison.Ordinal) =>
                L.T("Falscher Code. Die Einladung ist jetzt ungültig; bitte um eine neue.", "Wrong code. The invite is no longer valid; ask for a new one."),
            _ => ex.Message,
        };
    }

    private static void Apply(RemoteSite site, ShareInfo share)
    {
        site.ShareId = share.ShareId;
        site.RemoteSiteId = share.SiteId;
        site.Name = FederationProtocol.CleanName(share.SiteName, 100);
        site.Level = share.Level;
        site.Data = share.Data;
        site.ExpiresAt = share.ExpiresAt;
    }

    // --- Abgleich -----------------------------------------------------------------------------------

    /// <summary>Freigaben und Stände einer Instanz abrufen; Fehler landen in LastError statt als Ausnahme.</summary>
    public async Task SyncAsync(RemoteInstance instance, DateTime now, CancellationToken ct)
    {
        instance.LastSyncAt = now;
        try
        {
            await RenewIfDueAsync(instance, now, ct);
            var shares = await client.SendAsync<SharesResponse>(Target(instance), HttpMethod.Get, FederationProtocol.SharesPath, null, ct);
            instance.Name = FederationProtocol.CleanName(shares.InstanceName, 100);
            instance.Version = FederationProtocol.CleanName(shares.Version, 32);
            instance.Capabilities = string.Join(',', shares.Capabilities);
            instance.LastSuccessAt = now;
            instance.LastError = null;

            foreach (var site in instance.Sites.Where(s => s.EndedAt is null))
            {
                var share = shares.Shares.FirstOrDefault(s => s.ShareId == site.ShareId);
                if (share is null)
                {
                    End(site, instance, now);
                    continue;
                }

                Apply(site, share);
                await TrySnapshotAsync(instance, site, now, notify: true, ct);
            }
        }
        catch (FederationException ex)
        {
            instance.LastError = Cut(ex.Message);
            if (ex.Status == 401 && ex.Message.Contains("Access ended", StringComparison.Ordinal))
            {
                foreach (var site in instance.Sites.Where(s => s.EndedAt is null))
                {
                    End(site, instance, now);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private void End(RemoteSite site, RemoteInstance instance, DateTime now)
    {
        site.EndedAt = now;
        audit.Add("remote-site.ended", $"{site.Name} from {instance.Name}", actor: "federation");
        notifier.Publish(new Notification(NotificationEvents.SharingActivity,
            L.T($"Freigabe {DisplayName(site)} beendet", $"Share {DisplayName(site)} ended"),
            L.T($"{instance.Name} teilt die Site nicht mehr (beendet oder abgelaufen).", $"{instance.Name} no longer shares the site (ended or expired)."),
            NotificationSeverity.Warning, LinkPath: "/sites", RemoteSiteId: site.Id));
    }

    public static string DisplayName(RemoteSite site) => site.LocalName ?? site.Name;

    private async Task TrySnapshotAsync(RemoteInstance instance, RemoteSite site, DateTime now, bool notify, CancellationToken ct)
    {
        try
        {
            var snapshot = await client.SendAsync<SiteSnapshot>(Target(instance), HttpMethod.Get, FederationProtocol.SnapshotPath(site.RemoteSiteId), null, ct);
            if (notify && Snapshot(site) is { } previous)
            {
                NotifyChanges(site, instance, previous, snapshot);
            }

            NotifyJobs(site, snapshot, notify);
            site.SnapshotJson = JsonSerializer.Serialize(snapshot, FederationProtocol.Json);
            site.SnapshotAt = now;
        }
        catch (FederationException ex) when (ex.Status == 404)
        {
            End(site, instance, now);
        }
    }

    private void NotifyChanges(RemoteSite site, RemoteInstance instance, SiteSnapshot before, SiteSnapshot after)
    {
        var old = before.Hosts.ToDictionary(h => h.Id);
        var where = L.T($"Geteilte Site {DisplayName(site)} ({instance.Name})", $"Shared site {DisplayName(site)} ({instance.Name})");
        foreach (var host in after.Hosts)
        {
            if (!old.TryGetValue(host.Id, out var was))
            {
                continue;
            }

            if (host.Offline && !was.Offline)
            {
                Publish(site, NotificationEvents.HostOffline, L.T($"{host.Name} ist offline", $"{host.Name} is offline"), where, NotificationSeverity.Warning);
            }
            else if (!host.Offline && was.Offline)
            {
                Publish(site, NotificationEvents.HostOnline, L.T($"{host.Name} ist wieder online", $"{host.Name} is back online"), where, NotificationSeverity.Success);
            }

            if (host.SecurityUpdates > was.SecurityUpdates)
            {
                Publish(site, NotificationEvents.NewSecurityUpdates,
                    L.T($"{host.Name}: {host.SecurityUpdates} Sicherheitsupdates", $"{host.Name}: {host.SecurityUpdates} security updates"), where, NotificationSeverity.Warning);
            }

            if (host.KevUpdates > was.KevUpdates)
            {
                Publish(site, NotificationEvents.KevDetected,
                    L.T($"{host.Name}: Lücke aus dem KEV-Katalog offen", $"{host.Name}: known exploited vulnerability open"), where, NotificationSeverity.Critical);
            }
        }
    }

    private void NotifyJobs(RemoteSite site, SiteSnapshot snapshot, bool notify)
    {
        var since = site.JobsCheckedUntil ?? DateTime.MinValue;
        var finished = snapshot.Jobs.Where(j => j.FinishedAt > since).ToList();
        if (notify)
        {
            foreach (var job in finished)
            {
                var failed = job.Status is "Failed" or "Rejected";
                if (!failed && job.Status != "Succeeded")
                {
                    continue;
                }

                Publish(site, failed ? NotificationEvents.JobFailed : NotificationEvents.JobSucceeded,
                    failed ? L.T($"{job.Type} auf {job.HostName} fehlgeschlagen", $"{job.Type} on {job.HostName} failed")
                           : L.T($"{job.Type} auf {job.HostName} erfolgreich", $"{job.Type} on {job.HostName} succeeded"),
                    $"{job.Summary} · {L.T("Geteilte Site", "Shared site")} {DisplayName(site)}", failed ? NotificationSeverity.Critical : NotificationSeverity.Success);
            }
        }

        if (finished.Count > 0)
        {
            site.JobsCheckedUntil = finished.Max(j => j.FinishedAt);
        }
    }

    private void Publish(RemoteSite site, NotificationEvents ev, string title, string message, NotificationSeverity severity) =>
        notifier.Publish(new Notification(ev, title, message, severity, LinkPath: $"/shared/{site.Id}", RemoteSiteId: site.Id));

    /// <summary>Zertifikat erneuern, wenn zwei Drittel der Laufzeit um sind (neuer Schlüssel).</summary>
    private async Task RenewIfDueAsync(RemoteInstance instance, DateTime now, CancellationToken ct)
    {
        if (instance.CertificateExpiresAt - now > FederationProtocol.PeerCertificateLifetime / 3)
        {
            return;
        }

        var key = client.CreateKey();
        var renewed = await client.SendAsync<RenewResponse>(Target(instance), HttpMethod.Post, FederationProtocol.RenewPath, new RenewRequest(key.CsrPem), ct);
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(renewed.CertificatePem);
        instance.CertificatePem = renewed.CertificatePem;
        instance.ProtectedKey = key.ProtectedKey;
        instance.CertificateExpiresAt = certificate.NotAfter.ToUniversalTime();
        logger.LogInformation("Federation certificate for {Instance} renewed, valid until {NotAfter:u}", instance.Name, instance.CertificateExpiresAt);
        await db.SaveChangesAsync(ct);
    }

    private static string Cut(string text) => text.Length > 500 ? text[..500] : text;

    // --- Entfernen, Live-Abfragen, Aktionen ---------------------------------------------------------

    public async Task RemoveAsync(RemoteSite site, CancellationToken ct)
    {
        var instance = site.Instance!;
        if (site.EndedAt is null)
        {
            try
            {
                await client.SendAsync<JsonElement>(Target(instance), HttpMethod.Post, FederationProtocol.LeavePath, new LeaveRequest(site.ShareId), ct);
            }
            catch (FederationException ex)
            {
                logger.LogInformation("Leaving share at {Instance} failed (removed locally anyway): {Error}", instance.Name, ex.Message);
            }
        }

        audit.Add("remote-site.removed", $"{site.Name} from {instance.Name}");
        db.RemoteSites.Remove(site);
        if (instance.Sites.All(s => s.Id == site.Id))
        {
            db.RemoteInstances.Remove(instance); // letzte Site: Schlüssel und Zertifikat mit löschen
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<RemoteHostDetail> HostAsync(RemoteSite site, Guid hostId, CancellationToken ct) =>
        client.SendAsync<RemoteHostDetail>(Target(site.Instance!), HttpMethod.Get, FederationProtocol.HostPath(site.RemoteSiteId, hostId), null, ct);

    public Task<RemoteJobDetail> JobAsync(RemoteSite site, Guid jobId, long afterId, CancellationToken ct) =>
        client.SendAsync<RemoteJobDetail>(Target(site.Instance!), HttpMethod.Get,
            FederationProtocol.JobPath(site.RemoteSiteId, jobId) + (afterId > 0 ? $"?afterId={afterId}" : ""), null, ct);

    public async Task<RemoteJobDto> ActAsync(RemoteSite site, Guid hostId, ShareAction action, AIOPatcher.Contracts.PatchScope scope, string user, CancellationToken ct)
    {
        var response = await client.SendAsync<ActionResponse>(Target(site.Instance!), HttpMethod.Post, FederationProtocol.ActionPath(site.RemoteSiteId, hostId),
            new ActionRequest(action, scope, user), ct);
        var job = response.Job;
        audit.Add("remote-site.job.requested", $"{action} ({scope}) for {job.HostName} on {DisplayName(site)} at {site.Instance!.Name}, job {job.Id}");
        await db.SaveChangesAsync(ct);
        return job;
    }
}

/// <summary>Gleicht alle verbundenen Instanzen alle 5 Minuten ab.</summary>
public sealed class FederationSync(IServiceScopeFactory scopes, ILogger<FederationSync> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var remote = scope.ServiceProvider.GetRequiredService<RemoteSites>();
                var instances = await db.RemoteInstances.Include(i => i.Sites).Where(i => i.Sites.Any(s => s.EndedAt == null)).ToListAsync(stoppingToken);
                foreach (var instance in instances)
                {
                    await remote.SyncAsync(instance, DateTime.UtcNow, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Federation sync failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
