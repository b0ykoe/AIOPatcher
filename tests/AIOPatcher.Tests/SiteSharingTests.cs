using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Federation;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Site-Sharing zwischen Instanzen: Einladung mit Code, Zertifikat, Freigabe nur für eine Site, Aktionen.</summary>
public sealed class SiteSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly CertificateAuthority _ca;
    private readonly Site _shared, _other;
    private readonly ManagedHost _web, _secret;
    private readonly DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    public SiteSharingTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _ca = CertificateAuthority.LoadOrCreate(_dir, "patch.example.com", NullLogger.Instance);

        _shared = new Site { Name = "Firma X" };
        _other = new Site { Name = "Privat" };
        _web = new ManagedHost { Site = _shared, Name = "web01", Hostname = "web01", CertificateThumbprint = "t1" };
        _secret = new ManagedHost { Site = _other, Name = "nas", Hostname = "nas", CertificateThumbprint = "t2" };
        _db.Hosts.AddRange(_web, _secret);
        _db.PendingUpdates.Add(new PendingUpdate { Host = _web, Name = "openssl", IsSecurity = true, AvailableVersion = "3.0.2" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SharingService Service(AIOPatcherOptions? options = null, ShareActionLimiter? limiter = null) =>
        new(_db, _ca, new FeatureFlags(options ?? new AIOPatcherOptions()), new AuditLog(_db, new HttpContextAccessor()), new Notifier(), limiter ?? new ShareActionLimiter());

    private static string Csr(ECDsa key) => new CertificateRequest("CN=b", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

    private (SharingService.CreatedInvite Invite, SharingService Service) Invite(ShareLevel level = ShareLevel.Patch, ShareData data = ShareData.None, int? shareDays = null)
    {
        var service = Service();
        var created = service.CreateInvite(_shared, level, data, 7, shareDays, "admin:alice", _now);
        _db.SaveChanges();
        return (created, service);
    }

    private async Task<(FederationPeer Peer, SiteShare Share)> JoinAsync(ShareLevel level = ShareLevel.Patch, ShareData data = ShareData.None, int? shareDays = null)
    {
        var (invite, service) = Invite(level, data, shareDays);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var result = await service.JoinAsync(new JoinRequest(invite.Invite.Id, invite.Code, Csr(key), Guid.NewGuid(), "Kollege", "0.14.0"), null, _now, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        var peer = await _db.FederationPeers.SingleAsync(p => p.Id == result.Value!.PeerId);
        var share = await _db.SiteShares.Include(s => s.Site).SingleAsync(s => s.Id == result.Value!.Share.ShareId);
        return (peer, share);
    }

    [Fact]
    public void Link_RoundTrip_AndRejectsBrokenLinks()
    {
        var id = Guid.NewGuid();
        var link = FederationProtocol.InviteLink("https://patch.example.com", id, _ca.RootFingerprintSha256);
        var parsed = FederationProtocol.ParseLink(link)!;
        Assert.Equal("https://patch.example.com", parsed.BaseUrl);
        Assert.Equal(id, parsed.InviteId);
        Assert.Equal(_ca.RootFingerprintSha256, parsed.CaFingerprint);

        Assert.Null(FederationProtocol.ParseLink("http://patch.example.com/share/" + id.ToString("N") + "#" + _ca.RootFingerprintSha256)); // kein https
        Assert.Null(FederationProtocol.ParseLink("https://patch.example.com/share/" + id.ToString("N"))); // ohne Fingerabdruck
        Assert.Null(FederationProtocol.ParseLink("https://patch.example.com/other/" + id.ToString("N") + "#" + _ca.RootFingerprintSha256));
        Assert.Null(FederationProtocol.ParseLink("nonsense"));
    }

    [Fact]
    public void Code_IgnoresCaseAndDashes()
    {
        var code = FederationProtocol.NewCode();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        var hash = FederationProtocol.HashCode(code);
        Assert.True(FederationProtocol.CodeMatches(code.ToLowerInvariant().Replace("-", " "), hash));
        Assert.False(FederationProtocol.CodeMatches("AAAA-AAAA", hash));
    }

    [Fact]
    public void PeerCertificate_IsNeverAnAgent_AndAgentNeverAPeer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var peerId = Guid.NewGuid();
        using var peer = _ca.IssuePeerCertificate(Csr(key), peerId, FederationProtocol.PeerCertificateLifetime);
        using var agent = _ca.IssueClientCertificate(Csr(key), Guid.NewGuid(), Guid.NewGuid());

        Assert.True(CertificateAuthority.IsPeerCertificate(peer));
        Assert.Equal(peerId, CertificateAuthority.GetPeerId(peer));
        Assert.True(_ca.IsIssuedByThisCa(peer));
        Assert.False(CertificateAuthority.IsPeerCertificate(agent));
        Assert.Null(CertificateAuthority.GetPeerId(agent));
        Assert.True(FederationClient.IssuedBy(peer, _ca.RootCertificate));
    }

    [Fact]
    public async Task Join_FifthWrongCodeBurnsInvite_ThenCorrectCodeIsRejected()
    {
        var (invite, service) = Invite();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JoinRequest Request(string code) => new(invite.Invite.Id, code, Csr(key), Guid.NewGuid(), "B", "0.14.0");

        for (var i = 1; i < FederationProtocol.MaxFailedCodes; i++)
        {
            var wrong = await service.JoinAsync(Request("AAAA-AAAA"), null, _now, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, wrong.Status);
            Assert.Contains($"{FederationProtocol.MaxFailedCodes - i} attempts left", wrong.Error);
        }

        var last = await service.JoinAsync(Request("AAAA-AAAA"), null, _now, CancellationToken.None);
        Assert.Contains("no longer valid", last.Error);
        Assert.NotNull((await _db.ShareInvites.SingleAsync()).BurnedAt);

        var late = await service.JoinAsync(Request(invite.Code), null, _now, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, late.Status);
        Assert.Empty(_db.FederationPeers);
        Assert.Contains(_db.AuditEntries, a => a.Action == "federation.invite.burned");
    }

    [Fact]
    public async Task Join_IssuesCertificate_CreatesFrozenShare_InviteSingleUse()
    {
        var (invite, service) = Invite(ShareLevel.Scan, ShareData.JobLogs, shareDays: 30);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new JoinRequest(invite.Invite.Id, invite.Code, Csr(key), Guid.NewGuid(), "Kollege\u0007", "0.14.0");
        var result = await service.JoinAsync(request, null, _now, CancellationToken.None);

        Assert.True(result.Ok);
        using var cert = X509Certificate2.CreateFromPem(result.Value!.CertificatePem!);
        Assert.Equal(result.Value.PeerId, CertificateAuthority.GetPeerId(cert));
        Assert.Equal(ShareLevel.Scan, result.Value.Share.Level);
        Assert.Equal(ShareData.JobLogs, result.Value.Share.Data);
        Assert.Equal(_now.AddDays(30), result.Value.Share.ExpiresAt);
        var peer = await _db.FederationPeers.SingleAsync();
        Assert.Equal("Kollege", peer.Name); // Steuerzeichen entfernt
        Assert.Equal(CertificateAuthority.GetIdentityThumbprint(cert), peer.CertificateThumbprint);
        Assert.Contains(_db.AuditEntries, a => a.Action == "federation.joined" && a.Actor == "peer:Kollege");

        var again = await service.JoinAsync(request, null, _now, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, again.Status);
    }

    [Fact]
    public async Task Join_SignedByKnownPeer_AddsSecondSiteWithoutNewCertificate()
    {
        var (peer, _) = await JoinAsync();
        var service = Service();
        var second = service.CreateInvite(_other, ShareLevel.View, ShareData.None, 7, null, "admin:alice", _now);
        await _db.SaveChangesAsync();

        var result = await service.JoinAsync(new JoinRequest(second.Invite.Id, second.Code, null, peer.InstanceId, peer.Name, "0.14.0"), peer, _now, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Value!.CertificatePem);
        Assert.Equal(peer.Id, result.Value.PeerId);
        Assert.Single(_db.FederationPeers);
        Assert.Equal(2, await _db.SiteShares.CountAsync(s => s.PeerId == peer.Id));
    }

    [Fact]
    public async Task Share_CoversOnlyItsSite_AndEndsWithRevokeEndOrExpiry()
    {
        var (peer, share) = await JoinAsync(shareDays: 30);
        var service = Service();

        Assert.NotNull(await service.FindShareAsync(peer, _shared.Id, _now, CancellationToken.None));
        Assert.Null(await service.FindShareAsync(peer, _other.Id, _now, CancellationToken.None));
        Assert.Null(await service.HostDetailAsync(share, _secret.Id, CancellationToken.None)); // Host einer anderen Site
        Assert.Null(await service.FindShareAsync(peer, _shared.Id, _now.AddDays(31), CancellationToken.None)); // abgelaufen

        var snapshot = await service.SnapshotAsync(share, _now, CancellationToken.None);
        var host = Assert.Single(snapshot.Hosts);
        Assert.Equal("web01", host.Name);
        Assert.Equal(1, host.SecurityUpdates);

        share.EndedAt = _now;
        await _db.SaveChangesAsync();
        Assert.Null(await service.FindShareAsync(peer, _shared.Id, _now, CancellationToken.None));

        share.EndedAt = null;
        peer.RevokedAt = _now;
        await _db.SaveChangesAsync();
        Assert.Null(await service.FindShareAsync(peer, _shared.Id, _now, CancellationToken.None));
    }

    [Fact]
    public async Task DataScope_HidesUpdateListAndLogsUnlessShared()
    {
        var (_, without) = await JoinAsync(data: ShareData.None);
        var service = Service();
        var detail = await service.HostDetailAsync(without, _web.Id, CancellationToken.None);
        Assert.Null(detail!.Updates);

        var job = new PatchJob { HostId = _web.Id, Type = JobType.Scan, RequestedBy = "admin:alice", Status = JobStatus.Succeeded };
        _db.Jobs.Add(job);
        _db.JobLogEntries.Add(new JobLogEntry { Job = job, Text = "apt-get update" });
        await _db.SaveChangesAsync();
        Assert.Null((await service.JobDetailAsync(without, job.Id, 0, CancellationToken.None))!.Lines);

        var (_, with) = await JoinAsync(data: ShareData.UpdateDetails | ShareData.JobLogs);
        Assert.Equal("openssl", Assert.Single((await service.HostDetailAsync(with, _web.Id, CancellationToken.None))!.Updates!).Name);
        Assert.Equal("apt-get update", Assert.Single((await service.JobDetailAsync(with, job.Id, 0, CancellationToken.None))!.Lines!).Text);
    }

    [Fact]
    public async Task Pages_TravelAsOwnField_SoOlderPeersKeepParsingData()
    {
        var (_, share) = await JoinAsync(data: ShareData.UpdateDetails | ShareData.Pages);
        var info = SharingService.ToInfo(share, _shared);
        Assert.Equal(ShareData.UpdateDetails, info.Data);
        Assert.True(info.Pages);

        var json = System.Text.Json.JsonSerializer.Serialize(info, FederationProtocol.Json);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            Assert.Equal("UpdateDetails", doc.RootElement.GetProperty("data").GetString());
            Assert.False(doc.RootElement.TryGetProperty("all", out _));
        }

        var back = System.Text.Json.JsonSerializer.Deserialize<ShareInfo>(json, FederationProtocol.Json)!;
        Assert.Equal(ShareData.UpdateDetails | ShareData.Pages, back.All);

        // Antwort einer älteren Instanz ohne das Feld
        var old = System.Text.Json.JsonSerializer.Deserialize<ShareInfo>(json.Replace(",\"pages\":true", "", StringComparison.Ordinal), FederationProtocol.Json)!;
        Assert.Equal(ShareData.UpdateDetails, old.All);
    }

    [Fact]
    public async Task Views_NeedPagesScope_AndShowOnlyTheSharedSite()
    {
        _db.InstalledApps.AddRange(
            new InstalledApp { Host = _web, Name = "nginx", Source = "dpkg", Version = "1.24" },
            new InstalledApp { Host = _secret, Name = "plex", Source = "dpkg", Version = "1.40" });
        await _db.SaveChangesAsync();
        var features = new FeatureFlags(new AIOPatcherOptions());

        var (_, without) = await JoinAsync(data: ShareData.UpdateDetails);
        var denied = await AIOPatcher.Server.Endpoints.FederationEndpoints.ViewAsync(without, "software", null, null, _db, features, null!, null!, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(denied).StatusCode);

        var (_, with) = await JoinAsync(data: ShareData.Pages);
        var result = await AIOPatcher.Server.Endpoints.FederationEndpoints.ViewAsync(with, "software", null, null, _db, features, null!, null!, CancellationToken.None);
        var apps = Assert.IsAssignableFrom<IValueHttpResult>(result).Value as IEnumerable<AIOPatcher.Server.Endpoints.SoftwareEndpoints.SoftwareSummaryDto>;
        Assert.Equal("nginx", Assert.Single(apps!).Name);

        var unknown = await AIOPatcher.Server.Endpoints.FederationEndpoints.ViewAsync(with, "users", null, null, _db, features, null!, null!, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(unknown).StatusCode);
    }

    [Fact]
    public async Task Actions_FollowLevel_FeatureSwitches_AndAreAudited()
    {
        var (peer, share) = await JoinAsync(ShareLevel.Patch);
        var service = Service();
        ActionRequest Act(ShareAction action) => new(action, PatchScope.Security, "bob");

        var reboot = await service.ActAsync(peer, share, _web.Id, Act(ShareAction.Reboot), _now, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, reboot.Status);
        var patchReboot = await service.ActAsync(peer, share, _web.Id, Act(ShareAction.PatchReboot), _now, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, patchReboot.Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await service.ActAsync(peer, share, _secret.Id, Act(ShareAction.Patch), _now, CancellationToken.None)).Status);

        var locked = Service(new AIOPatcherOptions { LockedFeatures = "PatchJobs" });
        Assert.Equal(StatusCodes.Status403Forbidden, (await locked.ActAsync(peer, share, _web.Id, Act(ShareAction.Patch), _now, CancellationToken.None)).Status);

        var patch = await service.ActAsync(peer, share, _web.Id, Act(ShareAction.Patch), _now, CancellationToken.None);
        Assert.True(patch.Ok, patch.Error);
        Assert.Equal("Patch", patch.Value!.Job.Type);
        Assert.False(patch.Value.Job.AllowReboot);
        var job = await _db.Jobs.SingleAsync();
        Assert.Equal("peer:Kollege/bob", job.RequestedBy);
        var entry = await _db.AuditEntries.SingleAsync(a => a.Action == "federation.job.requested");
        Assert.Equal("peer:Kollege/bob", entry.Actor);
        Assert.Equal(_shared.Id, entry.SiteId);
        Assert.Contains(job.Id.ToString(), entry.Details);

        var view = (await JoinAsync(ShareLevel.View)).Share;
        Assert.Equal(StatusCodes.Status403Forbidden, (await service.ActAsync(peer, view, _web.Id, Act(ShareAction.Scan), _now, CancellationToken.None)).Status);
    }

    [Fact]
    public void Levels_AreCumulative()
    {
        Assert.False(FederationProtocol.Allows(ShareLevel.View, ShareAction.Scan));
        Assert.True(FederationProtocol.Allows(ShareLevel.Scan, ShareAction.Scan));
        Assert.False(FederationProtocol.Allows(ShareLevel.Scan, ShareAction.Patch));
        Assert.True(FederationProtocol.Allows(ShareLevel.Patch, ShareAction.Patch));
        Assert.False(FederationProtocol.Allows(ShareLevel.Patch, ShareAction.PatchReboot));
        Assert.True(FederationProtocol.Allows(ShareLevel.Reboot, ShareAction.Reboot));
    }

    [Fact]
    public void Limiter_AllowsAtMostSixtyActionsPerHourAndPeer()
    {
        var limiter = new ShareActionLimiter();
        var peer = Guid.NewGuid();
        for (var i = 0; i < FederationProtocol.MaxActionsPerHour; i++)
        {
            Assert.True(limiter.TryAcquire(peer, _now));
        }

        Assert.False(limiter.TryAcquire(peer, _now.AddMinutes(30)));
        Assert.True(limiter.TryAcquire(Guid.NewGuid(), _now)); // andere Gegenstelle
        Assert.True(limiter.TryAcquire(peer, _now.AddMinutes(61)));
    }

    [Fact]
    public async Task Gate_AcceptsOnlySignedRequestsOfActivePeers()
    {
        var service = Service();
        var invite = service.CreateInvite(_shared, ShareLevel.Patch, ShareData.None, 7, null, "admin:alice", DateTime.UtcNow);
        await _db.SaveChangesAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var joined = await service.JoinAsync(new JoinRequest(invite.Invite.Id, invite.Code, Csr(key), Guid.NewGuid(), "B", "0.14.0"), null, DateTime.UtcNow, CancellationToken.None);
        using var peerCert = X509Certificate2.CreateFromPem(joined.Value!.CertificatePem!).CopyWithPrivateKey(key);
        using var agentCert = _ca.IssueClientCertificate(Csr(key), Guid.NewGuid(), _shared.Id).CopyWithPrivateKey(key);
        var access = new PublicAccess();

        HttpContext Signed(X509Certificate2 cert)
        {
            var ctx = new DefaultHttpContext { RequestServices = new SingleService(new AIOPatcherOptions()) };
            ctx.Request.Method = "GET";
            ctx.Request.Path = FederationProtocol.SharesPath;
            ctx.Request.Body = new MemoryStream();
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = PublicChannel.NewNonce();
            ctx.Request.Headers[PublicChannel.CertHeader] = Convert.ToBase64String(cert.RawData);
            ctx.Request.Headers[PublicChannel.TimeHeader] = time.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ctx.Request.Headers[PublicChannel.NonceHeader] = nonce;
            ctx.Request.Headers[PublicChannel.SignatureHeader] = PublicChannel.Sign(key, PublicChannel.RequestToSign("GET", FederationProtocol.SharesPath, time, nonce, []));
            return ctx;
        }

        var (peer, _) = await FederationGate.AuthenticateAsync(Signed(peerCert), _ca, access, _db);
        Assert.Equal(joined.Value.PeerId, peer!.Id);
        Assert.NotNull(peer.LastSeenAt);

        Assert.Contains("Not a peer", (await FederationGate.AuthenticateAsync(Signed(agentCert), _ca, access, _db)).Error);

        peer.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        Assert.Contains("Access ended", (await FederationGate.AuthenticateAsync(Signed(peerCert), _ca, access, _db)).Error);
    }

    [Fact]
    public void RemoteStatus_LiveStaleOfflineEnded()
    {
        var instance = new RemoteInstance
        {
            Name = "A", BaseUrl = "https://a", CaFingerprint = "x", CaCertificatePem = "", ProtectedKey = "", CertificatePem = "", CreatedBy = "admin:b",
            LastSuccessAt = _now.AddMinutes(-2),
        };
        var site = new RemoteSite { Name = "Firma X", CreatedBy = "admin:b", Instance = instance };

        Assert.Equal("Live", RemoteSites.Status(site, _now));
        Assert.Equal("Stale", RemoteSites.Status(site, _now.AddMinutes(20)));
        Assert.Equal("Offline", RemoteSites.Status(site, _now.AddHours(2)));
        site.ExpiresAt = _now.AddMinutes(-1);
        Assert.Equal("Ended", RemoteSites.Status(site, _now));
    }

    [Fact]
    public void JoinErrors_FromOtherInstanceAreTranslated()
    {
        L.Use("de");
        try
        {
            Assert.Equal("Falscher Code (noch 3 Versuche).", RemoteSites.JoinError(new FederationException(403, "Wrong code (3 attempts left).")));
            Assert.Contains("jetzt ungültig", RemoteSites.JoinError(new FederationException(403, "Wrong code. The invite is no longer valid.")));
            Assert.Contains("abgelaufen", RemoteSites.JoinError(new FederationException(404, "Invite is invalid, used or expired.")));
        }
        finally
        {
            L.Use("en");
        }
    }

    /// <summary>Minimaler Service-Provider für das Gate (liest nur die Optionen).</summary>
    private sealed class SingleService(AIOPatcherOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(AIOPatcherOptions) ? options : null;
    }
}
