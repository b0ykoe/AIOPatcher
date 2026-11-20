using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Federation;

// Protokoll zwischen zwei AIOPatcher-Servern (Site-Sharing). B ruft A über die externe Dashboard-Adresse von A auf.
//
//   GET  /federation/v1/info                                  anonym: Name, Version, CA, Fähigkeiten
//   POST /federation/v1/join                                  anonym (oder signiert): Einladung + Code + CSR → Zertifikat
//   POST /federation/v1/renew                                 neues Zertifikat (CSR)
//   POST /federation/v1/leave                                 Freigabe beenden (von B aus)
//   GET  /federation/v1/shares                                aktive Freigaben dieser Gegenstelle
//   GET  /federation/v1/sites/{siteId}/snapshot               Hosts mit Kennzahlen + letzte Jobs
//   GET  /federation/v1/sites/{siteId}/hosts/{hostId}         Detail (Updates je nach Datenumfang, Jobs)
//   GET  /federation/v1/sites/{siteId}/jobs/{jobId}           Job mit Log (je nach Datenumfang)
//   POST /federation/v1/sites/{siteId}/hosts/{hostId}/actions Scan, Patch, Neustart
//   GET  /federation/v1/sites/{siteId}/views/{view}           drei Seiten nur lesend (0.16.0, Datenumfang „Pages“)
//   GET  /federation/v1/sites/{siteId}/mirror/api/...         Dashboard-API nur lesend, auf die Site eingeschränkt („Pages“)
//
// Signiert wird wie beim öffentlichen Agent-Weg (PublicChannel): Zertifikat, Zeit, Nonce, Signatur über Methode,
// Pfad und Inhalt; jede Antwort signiert A mit seinem Server-Zertifikat. B pinnt die CA von A aus dem Einladungslink.
//
// Versionierung: innerhalb von v1 nur hinzufügen (unbekannte Felder werden ignoriert, Aufzählungen als Text).
// Was eine Gegenstelle kann, steht in "capabilities"; Bruch = neuer Pfad /federation/v2.

public static class FederationProtocol
{
    public const int Version = 1;
    public const string BasePath = "/federation/v1";
    public const string InfoPath = BasePath + "/info";
    public const string JoinPath = BasePath + "/join";
    public const string RenewPath = BasePath + "/renew";
    public const string LeavePath = BasePath + "/leave";
    public const string SharesPath = BasePath + "/shares";
    public static string SnapshotPath(Guid siteId) => $"{BasePath}/sites/{siteId}/snapshot";
    public static string HostPath(Guid siteId, Guid hostId) => $"{BasePath}/sites/{siteId}/hosts/{hostId}";
    public static string JobPath(Guid siteId, Guid jobId) => $"{BasePath}/sites/{siteId}/jobs/{jobId}";
    public static string ActionPath(Guid siteId, Guid hostId) => $"{BasePath}/sites/{siteId}/hosts/{hostId}/actions";
    public static string ViewPath(Guid siteId, string view) => $"{BasePath}/sites/{siteId}/views/{view}";
    public static string MirrorPath(Guid siteId, string apiPathAndQuery) => $"{BasePath}/sites/{siteId}/mirror{apiPathAndQuery}";

    /// <summary>Wie eine gespiegelte API-Adresse auf die geteilte Site eingeschränkt wird.</summary>
    public enum MirrorGuard
    {
        /// <summary>Liste: siteId wird fest auf die geteilte Site gesetzt.</summary>
        Site,

        /// <summary>Host-Detail: der Host muss zur geteilten Site gehören.</summary>
        Host,

        /// <summary>Job-Detail: der Host des Jobs muss zur geteilten Site gehören.</summary>
        Job,
    }

    /// <summary>
    /// Spiegelbare GET-Adressen der Dashboard-API (Datenumfang „Pages“). Nur, was sich sicher auf eine Site einschränken
    /// lässt; Patch-Pläne und -Läufe fehlen, weil sie Hosts mehrerer Sites enthalten können.
    /// </summary>
    private static readonly (Regex Pattern, MirrorGuard Guard)[] MirrorRules =
    [
        (new Regex(@"^/api/(overview|hosts|jobs|utilization|vulnerabilities|software|monitoring|backups|compliance|compliance/baselines|patch-freezes|package-holds|stacks|reports/patch)$", RegexOptions.CultureInvariant), MirrorGuard.Site),
        (new Regex(@"^/api/hosts/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(/(software|metrics|monitoring|compliance|updates/summary|updates/changelog|containers/[^/]+/vulnerabilities))?$", RegexOptions.CultureInvariant), MirrorGuard.Host),
        (new Regex(@"^/api/jobs/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(/updates)?$", RegexOptions.CultureInvariant), MirrorGuard.Job),
    ];

    /// <summary>Darf diese API-Adresse (ohne Query) gespiegelt werden, und wie wird sie eingeschränkt? Sonst null.</summary>
    public static (MirrorGuard Guard, Guid Id)? MatchMirror(string apiPath)
    {
        foreach (var (pattern, guard) in MirrorRules)
        {
            var match = pattern.Match(apiPath);
            if (match.Success)
            {
                return (guard, match.Groups["id"].Success ? Guid.Parse(match.Groups["id"].Value) : Guid.Empty);
            }
        }

        return null;
    }

    /// <summary>Seiten, die mit dem Datenumfang „Pages“ lesbar sind; Antwort im selben Format wie die API des Dashboards.</summary>
    public static readonly string[] Views = ["utilization", "vulnerabilities", "software"];

    /// <summary>Was dieser Server als A anbietet.</summary>
    public static readonly string[] Capabilities = ["snapshot", "hostDetail", "jobs", "actions", "views", "mirror"];

    /// <summary>Falsche Codes pro Einladung, danach ist sie verbrannt.</summary>
    public const int MaxFailedCodes = 5;

    public const int DefaultInviteDays = 7;
    public const int MaxInviteDays = 30;

    /// <summary>Laufzeit der Gegenstellen-Zertifikate; B erneuert nach zwei Dritteln.</summary>
    public static readonly TimeSpan PeerCertificateLifetime = TimeSpan.FromDays(90);

    /// <summary>Höchstgröße einer Antwort von A (Schutz vor einer kompromittierten oder kaputten Gegenstelle).</summary>
    public const int MaxResponseBytes = 5 * 1024 * 1024;

    /// <summary>Aktionen pro Gegenstelle und Stunde.</summary>
    public const int MaxActionsPerHour = 60;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // --- Einladungslink und Code ----------------------------------------------------------------

    /// <summary>https://patch.example.com/share/{inviteId}#{CA-Fingerabdruck}. Der Teil nach # geht nie an einen Server.</summary>
    public static string InviteLink(string dashboardUrl, Guid inviteId, string caFingerprint) =>
        $"{dashboardUrl.TrimEnd('/')}/share/{inviteId:N}#{caFingerprint}";

    public sealed record ParsedLink(string BaseUrl, Guid InviteId, string CaFingerprint);

    public static ParsedLink? ParseLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var fingerprint = uri.Fragment.TrimStart('#').Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        if (segments.Length != 2 || segments[0] != "share" || !Guid.TryParse(segments[1], out var inviteId)
            || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new ParsedLink(uri.GetLeftPart(UriPartial.Authority), inviteId, fingerprint);
    }

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // ohne 0/O, 1/I

    /// <summary>8 Zeichen, angezeigt als XXXX-XXXX (40 Bit, dazu höchstens 5 Versuche).</summary>
    public static string NewCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }

    /// <summary>Großschreibung, Leerzeichen und Bindestriche egal.</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").ToUpperInvariant().Where(c => c is not ('-' or ' ')).ToArray());

    public static string HashCode(string code) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));

    public static bool CodeMatches(string? code, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HashCode(code ?? "")), Encoding.UTF8.GetBytes(hash));

    /// <summary>Benutzername von B für Audit und Job (gekürzt, ohne Steuerzeichen).</summary>
    public static string CleanName(string? value, int max = 64)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? "?" : text.Length > max ? text[..max] : text;
    }

    public static bool Allows(ShareLevel level, ShareAction action) => action switch
    {
        ShareAction.Scan => level >= ShareLevel.Scan,
        ShareAction.Patch => level >= ShareLevel.Patch,
        ShareAction.PatchReboot or ShareAction.Reboot => level >= ShareLevel.Reboot,
        _ => false,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareAction>))]
public enum ShareAction
{
    Scan,
    Patch,
    PatchReboot,
    Reboot,
}

// --- DTOs (camelCase, Aufzählungen als Text) --------------------------------------------------------

public sealed record FederationInfo(string Server, string Version, int[] Protocols, Guid InstanceId, string InstanceName, string CaCertificatePem, string[] Capabilities);

public sealed record JoinRequest(Guid InviteId, string Code, string? CsrPem, Guid InstanceId, string InstanceName, string Version);

/// <param name="CertificatePem">Neues Zertifikat (null, wenn B mit seinem bestehenden Zertifikat signiert hat).</param>
public sealed record JoinResponse(Guid PeerId, string? CertificatePem, ShareInfo Share);

public sealed record RenewRequest(string CsrPem);

public sealed record RenewResponse(string CertificatePem);

public sealed record LeaveRequest(Guid ShareId);

/// <param name="Data">Nur die Werte, die jede v1-Gegenstelle kennt (UpdateDetails, JobLogs).</param>
/// <param name="Pages">Weitere Seiten lesbar (ShareData.Pages); eigenes Feld, ältere Gegenstellen ignorieren es.</param>
public sealed record ShareInfo(Guid ShareId, Guid SiteId, string SiteName, ShareLevel Level, ShareData Data, DateTime? ExpiresAt, DateTime CreatedAt, string CreatedBy,
    bool Pages = false)
{
    /// <summary>Werte, die schon in Version 1 bekannt waren und deshalb in Data stehen dürfen.</summary>
    public const ShareData WireData = ShareData.UpdateDetails | ShareData.JobLogs;

    /// <summary>Vollständiger Datenumfang auf Seite B.</summary>
    [JsonIgnore]
    public ShareData All => (Data & WireData) | (Pages ? ShareData.Pages : ShareData.None);
}

public sealed record SharesResponse(string InstanceName, string Version, string[] Capabilities, IReadOnlyList<ShareInfo> Shares);

public sealed record RemoteHostDto(
    Guid Id, string Name, string Hostname, string OsFamily, string? OsName, string? OsVersion, string? AgentVersion,
    DateTime? LastSeenAt, DateTime? LastScanAt, bool RebootRequired, int PendingUpdates, int SecurityUpdates, int KevUpdates,
    bool IsRevoked, string? Tags, bool Offline);

public sealed record RemoteJobDto(
    Guid Id, Guid HostId, string HostName, string Type, string Scope, bool AllowReboot, string Status, string RequestedBy,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, string? Summary, bool RebootPerformed);

public sealed record SiteSnapshot(Guid SiteId, string SiteName, DateTime GeneratedAt, IReadOnlyList<RemoteHostDto> Hosts, IReadOnlyList<RemoteJobDto> Jobs);

public sealed record RemoteUpdateDto(string Name, string? CurrentVersion, string? AvailableVersion, bool IsSecurity, bool IsKev, string? Cves);

/// <param name="Updates">null = nicht freigegeben (Datenumfang).</param>
public sealed record RemoteHostDetail(RemoteHostDto Host, IReadOnlyList<RemoteUpdateDto>? Updates, IReadOnlyList<RemoteJobDto> Jobs);

public sealed record RemoteLogLineDto(long Id, DateTime Timestamp, string Text);

/// <param name="Lines">null = Logs nicht freigegeben.</param>
public sealed record RemoteJobDetail(RemoteJobDto Job, IReadOnlyList<RemoteLogLineDto>? Lines);

/// <param name="User">Benutzer bei B (für Audit und Job auf A; A kann ihn nicht prüfen).</param>
public sealed record ActionRequest(ShareAction Action, AIOPatcher.Contracts.PatchScope Scope, string User);

public sealed record ActionResponse(RemoteJobDto Job);
