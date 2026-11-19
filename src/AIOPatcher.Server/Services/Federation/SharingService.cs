using System.Collections.Concurrent;
using System.Security.Cryptography;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Federation;

/// <summary>Ergebnis einer Föderations-Anfrage: entweder ein Wert oder Status + Meldung.</summary>
public sealed record FederationResult<T>(T? Value, int Status = StatusCodes.Status200OK, string? Error = null)
{
    public bool Ok => Value is not null;
    public static FederationResult<T> Fail(int status, string error) => new(default, status, error);
}

/// <summary>
/// Site-Sharing, Seite A (wir teilen): Einladungen, Annahme, Freigaben prüfen, Daten liefern, Aktionen ausführen.
/// Jede Freigabe gilt für genau eine Site; was außerhalb liegt, ist für die Gegenstelle "nicht gefunden".
/// </summary>
public sealed class SharingService(
    AppDbContext db,
    CertificateAuthority ca,
    FeatureFlags features,
    AuditLog audit,
    Notifier notifier,
    ShareActionLimiter limiter)
{
    public static string PeerActor(FederationPeer peer, string? user = null) =>
        user is null ? $"peer:{peer.Name}" : $"peer:{peer.Name}/{FederationProtocol.CleanName(user)}";

    // --- Einladungen -------------------------------------------------------------------------------

    public sealed record CreatedInvite(ShareInvite Invite, string Code);

    public CreatedInvite CreateInvite(Site site, ShareLevel level, ShareData data, int inviteDays, int? shareDays, string createdBy, DateTime now)
    {
        var code = FederationProtocol.NewCode();
        var invite = new ShareInvite
        {
            SiteId = site.Id,
            CodeHash = FederationProtocol.HashCode(code),
            Level = level,
            Data = data,
            ShareDays = shareDays,
            ExpiresAt = now.AddDays(inviteDays),
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        db.ShareInvites.Add(invite);
        audit.Add("federation.invite.created", $"{site.Name}: {level}, data {data}, invite {inviteDays} d, share {(shareDays is { } d ? $"{d} d" : "unlimited")}", siteId: site.Id);
        return new CreatedInvite(invite, code);
    }

    public static bool IsOpen(ShareInvite invite, DateTime now) =>
        invite.UsedAt is null && invite.BurnedAt is null && invite.RevokedAt is null && invite.ExpiresAt > now;

    /// <summary>
    /// Einladung annehmen. <paramref name="signedBy"/>: B hat mit einem schon ausgestellten Zertifikat signiert (zweite
    /// Site derselben Instanz), dann kein neues Zertifikat. Sonst ist eine CSR Pflicht.
    /// </summary>
    public async Task<FederationResult<JoinResponse>> JoinAsync(JoinRequest request, FederationPeer? signedBy, DateTime now, CancellationToken ct)
    {
        var invite = await db.ShareInvites.Include(i => i.Site).SingleOrDefaultAsync(i => i.Id == request.InviteId, ct);
        if (invite?.Site is null || !IsOpen(invite, now))
        {
            return FederationResult<JoinResponse>.Fail(StatusCodes.Status404NotFound, "Invite is invalid, used or expired.");
        }

        if (!FederationProtocol.CodeMatches(request.Code, invite.CodeHash))
        {
            invite.FailedAttempts++;
            var left = FederationProtocol.MaxFailedCodes - invite.FailedAttempts;
            if (left <= 0)
            {
                invite.BurnedAt = now;
                audit.Add("federation.invite.burned", $"{invite.Site.Name}: {FederationProtocol.MaxFailedCodes} wrong codes", siteId: invite.SiteId, actor: "federation");
                notifier.Publish(new Notification(NotificationEvents.SharingActivity,
                    L.T($"Einladung für {invite.Site.Name} ungültig", $"Invite for {invite.Site.Name} burned"),
                    L.T($"{FederationProtocol.MaxFailedCodes} falsche Codes. Die Einladung ist ungültig; bei Bedarf eine neue erstellen.",
                        $"{FederationProtocol.MaxFailedCodes} wrong codes. The invite is no longer valid; create a new one if needed."),
                    NotificationSeverity.Warning, SiteId: invite.SiteId, LinkPath: "/settings?tab=sharing"));
                await db.SaveChangesAsync(ct);
                return FederationResult<JoinResponse>.Fail(StatusCodes.Status403Forbidden, "Wrong code. The invite is no longer valid.");
            }

            await db.SaveChangesAsync(ct);
            return FederationResult<JoinResponse>.Fail(StatusCodes.Status403Forbidden, $"Wrong code ({left} attempts left).");
        }

        var peer = signedBy;
        string? certificatePem = null;
        if (peer is null)
        {
            if (string.IsNullOrWhiteSpace(request.CsrPem) || request.CsrPem.Length > 8000)
            {
                return FederationResult<JoinResponse>.Fail(StatusCodes.Status400BadRequest, "Certificate request missing.");
            }

            peer = new FederationPeer
            {
                InstanceId = request.InstanceId,
                Name = FederationProtocol.CleanName(request.InstanceName, 100),
                Version = FederationProtocol.CleanName(request.Version, 32),
                CertificateThumbprint = "",
                CreatedAt = now,
            };

            try
            {
                using var certificate = ca.IssuePeerCertificate(request.CsrPem, peer.Id, FederationProtocol.PeerCertificateLifetime);
                peer.CertificateThumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
                peer.CertificateExpiresAt = certificate.NotAfter.ToUniversalTime();
                certificatePem = certificate.ExportCertificatePem();
            }
            catch (CryptographicException ex)
            {
                return FederationResult<JoinResponse>.Fail(StatusCodes.Status400BadRequest, ex.Message);
            }

            db.FederationPeers.Add(peer);
        }

        // Dieselbe Site ein zweites Mal für dieselbe Instanz: die alte Freigabe endet, die neue gilt.
        foreach (var old in await db.SiteShares.Where(s => s.PeerId == peer.Id && s.SiteId == invite.SiteId && s.EndedAt == null).ToListAsync(ct))
        {
            old.EndedAt = now;
            old.EndedBy = "replaced";
        }

        var share = new SiteShare
        {
            PeerId = peer.Id,
            SiteId = invite.SiteId,
            Level = invite.Level,
            Data = invite.Data,
            ExpiresAt = invite.ShareDays is { } days ? now.AddDays(days) : null,
            InviteId = invite.Id,
            CreatedBy = invite.CreatedBy,
            CreatedAt = now,
        };
        db.SiteShares.Add(share);
        invite.UsedAt = now;
        invite.UsedByPeerId = peer.Id;

        audit.Add("federation.joined", $"{peer.Name} ({peer.InstanceId}) accepted {invite.Site.Name}: {share.Level}", siteId: invite.SiteId, actor: PeerActor(peer));
        notifier.Publish(new Notification(NotificationEvents.SharingActivity,
            L.T($"{peer.Name} hat {invite.Site.Name} angenommen", $"{peer.Name} accepted {invite.Site.Name}"),
            L.T($"Die Site ist jetzt mit der Instanz {peer.Name} geteilt ({share.Level}).", $"The site is now shared with the instance {peer.Name} ({share.Level})."),
            NotificationSeverity.Info, SiteId: invite.SiteId, LinkPath: "/settings?tab=sharing"));
        await db.SaveChangesAsync(ct);

        return new FederationResult<JoinResponse>(new JoinResponse(peer.Id, certificatePem, ToInfo(share, invite.Site)));
    }

    public static ShareInfo ToInfo(SiteShare share, Site site) =>
        new(share.Id, share.SiteId, site.Name, share.Level, share.Data, share.ExpiresAt, share.CreatedAt, share.CreatedBy);

    public async Task<FederationResult<RenewResponse>> RenewAsync(FederationPeer peer, RenewRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CsrPem) || request.CsrPem.Length > 8000)
        {
            return FederationResult<RenewResponse>.Fail(StatusCodes.Status400BadRequest, "Certificate request missing.");
        }

        try
        {
            using var certificate = ca.IssuePeerCertificate(request.CsrPem, peer.Id, FederationProtocol.PeerCertificateLifetime);
            peer.PendingCertificateThumbprint = CertificateAuthority.GetIdentityThumbprint(certificate);
            peer.PendingCertificateExpiresAt = certificate.NotAfter.ToUniversalTime();
            audit.Add("federation.renewed", $"{peer.Name}: valid until {peer.PendingCertificateExpiresAt:yyyy-MM-dd}", actor: PeerActor(peer));
            await db.SaveChangesAsync(ct);
            return new FederationResult<RenewResponse>(new RenewResponse(certificate.ExportCertificatePem()));
        }
        catch (CryptographicException ex)
        {
            return FederationResult<RenewResponse>.Fail(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    public async Task LeaveAsync(FederationPeer peer, Guid shareId, DateTime now, CancellationToken ct)
    {
        var share = await db.SiteShares.Include(s => s.Site).SingleOrDefaultAsync(s => s.Id == shareId && s.PeerId == peer.Id && s.EndedAt == null, ct);
        if (share is null)
        {
            return;
        }

        share.EndedAt = now;
        share.EndedBy = PeerActor(peer);
        audit.Add("federation.left", $"{peer.Name} removed {share.Site?.Name}", siteId: share.SiteId, actor: PeerActor(peer));
        notifier.Publish(new Notification(NotificationEvents.SharingActivity,
            L.T($"{peer.Name} hat {share.Site?.Name} entfernt", $"{peer.Name} removed {share.Site?.Name}"),
            L.T("Die Freigabe ist beendet.", "The share has ended."),
            NotificationSeverity.Info, SiteId: share.SiteId, LinkPath: "/settings?tab=sharing"));
        await db.SaveChangesAsync(ct);
    }

    public async Task<SharesResponse> SharesAsync(FederationPeer peer, string instanceName, DateTime now, CancellationToken ct)
    {
        var shares = await db.SiteShares.Include(s => s.Site)
            .Where(s => s.PeerId == peer.Id && s.EndedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
            .ToListAsync(ct);
        return new SharesResponse(instanceName, AgentDownloads.AgentVersion, FederationProtocol.Capabilities,
            shares.OrderBy(s => s.Site!.Name).Select(s => ToInfo(s, s.Site!)).ToList());
    }

    // --- Freigabe prüfen und Daten liefern -----------------------------------------------------------

    /// <summary>Aktive Freigabe dieser Gegenstelle für genau diese Site, sonst null (Aufrufer antwortet 404).</summary>
    public async Task<SiteShare?> FindShareAsync(FederationPeer peer, Guid siteId, DateTime now, CancellationToken ct)
    {
        if (peer.RevokedAt is not null)
        {
            return null;
        }

        var share = await db.SiteShares.Include(s => s.Site)
            .Where(s => s.PeerId == peer.Id && s.SiteId == siteId && s.EndedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (share is not null && (share.LastAccessAt is null || now - share.LastAccessAt > TimeSpan.FromMinutes(1)))
        {
            share.LastAccessAt = now;
            await db.SaveChangesAsync(ct);
        }

        return share;
    }

    public async Task<SiteSnapshot> SnapshotAsync(SiteShare share, DateTime now, CancellationToken ct)
    {
        var hosts = await HostsAsync(db.Hosts.Where(h => h.SiteId == share.SiteId), ct);
        var jobs = await JobsAsync(db.Jobs.Where(j => j.Host!.SiteId == share.SiteId), 50, ct);
        return new SiteSnapshot(share.SiteId, share.Site!.Name, now, hosts, jobs);
    }

    public async Task<RemoteHostDetail?> HostDetailAsync(SiteShare share, Guid hostId, CancellationToken ct)
    {
        var host = (await HostsAsync(db.Hosts.Where(h => h.Id == hostId && h.SiteId == share.SiteId), ct)).SingleOrDefault();
        if (host is null)
        {
            return null;
        }

        List<RemoteUpdateDto>? updates = null;
        if (share.Data.HasFlag(ShareData.UpdateDetails))
        {
            updates = await db.PendingUpdates.Where(u => u.HostId == hostId)
                .OrderByDescending(u => u.IsSecurity).ThenBy(u => u.Name)
                .Select(u => new RemoteUpdateDto(u.Name, u.CurrentVersion, u.AvailableVersion, u.IsSecurity, u.IsKev, u.Cves))
                .ToListAsync(ct);
        }

        var jobs = await JobsAsync(db.Jobs.Where(j => j.HostId == hostId), 20, ct);
        return new RemoteHostDetail(host, updates, jobs);
    }

    public async Task<RemoteJobDetail?> JobDetailAsync(SiteShare share, Guid jobId, long afterId, CancellationToken ct)
    {
        var job = (await JobsAsync(db.Jobs.Where(j => j.Id == jobId && j.Host!.SiteId == share.SiteId), 1, ct)).SingleOrDefault();
        if (job is null)
        {
            return null;
        }

        List<RemoteLogLineDto>? lines = null;
        if (share.Data.HasFlag(ShareData.JobLogs))
        {
            lines = await db.JobLogEntries.Where(l => l.JobId == jobId && l.Id > afterId).OrderBy(l => l.Id).Take(5000)
                .Select(l => new RemoteLogLineDto(l.Id, l.Timestamp, l.Text))
                .ToListAsync(ct);
        }

        return new RemoteJobDetail(job, lines);
    }

    private static async Task<List<RemoteHostDto>> HostsAsync(IQueryable<ManagedHost> query, CancellationToken ct)
    {
        var rows = await query.OrderBy(h => h.Name).Select(h => new
        {
            h.Id, h.Name, h.Hostname, h.OsFamily, h.OsName, h.OsVersion, h.AgentVersion, h.LastSeenAt, h.LastScanAt, h.RebootRequired,
            Pending = h.PendingUpdates.Count, Security = h.PendingUpdates.Count(u => u.IsSecurity), Kev = h.PendingUpdates.Count(u => u.IsKev),
            h.IsRevoked, h.Tags, Offline = h.OfflineNotifiedAt != null,
        }).ToListAsync(ct);
        return rows.Select(h => new RemoteHostDto(h.Id, h.Name, h.Hostname, h.OsFamily.ToString(), h.OsName, h.OsVersion, h.AgentVersion,
            h.LastSeenAt, h.LastScanAt, h.RebootRequired, h.Pending, h.Security, h.Kev, h.IsRevoked, h.Tags, h.Offline)).ToList();
    }

    private static async Task<List<RemoteJobDto>> JobsAsync(IQueryable<PatchJob> query, int limit, CancellationToken ct)
    {
        var rows = await query.OrderByDescending(j => j.CreatedAt).Take(limit).Select(j => new
        {
            j.Id, j.HostId, HostName = j.Host!.Name, j.Type, j.Scope, j.AllowReboot, j.Status, j.RequestedBy,
            j.CreatedAt, j.StartedAt, j.FinishedAt, j.Summary, j.RebootPerformed,
        }).ToListAsync(ct);
        return rows.Select(j => new RemoteJobDto(j.Id, j.HostId, j.HostName, j.Type.ToString(), j.Scope.ToString(), j.AllowReboot, j.Status.ToString(),
            j.RequestedBy, j.CreatedAt, j.StartedAt, j.FinishedAt, j.Summary, j.RebootPerformed)).ToList();
    }

    // --- Aktionen ------------------------------------------------------------------------------------

    public async Task<FederationResult<ActionResponse>> ActAsync(FederationPeer peer, SiteShare share, Guid hostId, ActionRequest request, DateTime now, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Scope))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status400BadRequest, "Unknown action.");
        }

        if (!FederationProtocol.Allows(share.Level, request.Action))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status403Forbidden, L.T("Diese Aktion erlaubt die Freigabe nicht.", "The share does not allow this action."));
        }

        if (!await db.Hosts.AnyAsync(h => h.Id == hostId && h.SiteId == share.SiteId, ct))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status404NotFound, "Host not found.");
        }

        var (type, allowReboot) = request.Action switch
        {
            ShareAction.Scan => (JobType.Scan, false),
            ShareAction.Patch => (JobType.Patch, false),
            ShareAction.PatchReboot => (JobType.Patch, true),
            _ => (JobType.Reboot, true),
        };

        // Notaus von A gilt auch für andere Instanzen; Patchen mit Neustart braucht beide Schalter.
        if (type == JobType.Patch && !features.IsOn(Feature.PatchJobs))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status403Forbidden, FeatureFlags.DisabledMessage(Feature.PatchJobs));
        }

        if (allowReboot && !features.IsOn(Feature.Reboots))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status403Forbidden, FeatureFlags.DisabledMessage(Feature.Reboots));
        }

        if (!limiter.TryAcquire(peer.Id, now))
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status429TooManyRequests,
                L.T($"Höchstens {FederationProtocol.MaxActionsPerHour} Aktionen pro Stunde.", $"At most {FederationProtocol.MaxActionsPerHour} actions per hour."));
        }

        var actor = PeerActor(peer, request.User);
        var plan = await JobPlanner.PlanAsync(db, [hostId], type, request.Scope, allowReboot, actor, ct);
        if (plan.Skipped.Count > 0)
        {
            return FederationResult<ActionResponse>.Fail(StatusCodes.Status409Conflict, plan.Skipped[0].Reason);
        }

        var job = plan.Created[0];
        audit.Add("federation.job.requested", $"{job.Type} ({job.Scope}, reboot {(job.AllowReboot ? "allowed" : "no")}) for {job.Host!.Name}, job {job.Id}",
            siteId: share.SiteId, hostId: job.HostId, actor: actor);
        notifier.Publish(new Notification(NotificationEvents.SharingActivity,
            L.T($"{job.Type} auf {job.Host.Name} von {peer.Name}", $"{job.Type} on {job.Host.Name} by {peer.Name}"),
            L.T($"Ausgelöst von {FederationProtocol.CleanName(request.User)} auf der Instanz {peer.Name} (geteilte Site {share.Site?.Name}).",
                $"Started by {FederationProtocol.CleanName(request.User)} on the instance {peer.Name} (shared site {share.Site?.Name})."),
            NotificationSeverity.Info, SiteId: share.SiteId, HostId: job.HostId, LinkPath: $"/jobs/{job.Id}"));
        await db.SaveChangesAsync(ct);

        var dto = (await JobsAsync(db.Jobs.Where(j => j.Id == job.Id), 1, ct)).Single();
        return new FederationResult<ActionResponse>(new ActionResponse(dto));
    }
}

/// <summary>Aktionen pro Gegenstelle und Stunde (im Speicher; ein Neustart des Servers setzt zurück).</summary>
public sealed class ShareActionLimiter
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _actions = new();

    public bool TryAcquire(Guid peerId, DateTime now)
    {
        var queue = _actions.GetOrAdd(peerId, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= FederationProtocol.MaxActionsPerHour)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
