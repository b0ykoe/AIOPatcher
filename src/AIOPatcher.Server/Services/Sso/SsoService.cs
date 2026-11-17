using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIOPatcher.Server.Services.Sso;

/// <summary>Gruppe beim Anbieter → Rolle in AIOPatcher.</summary>
public sealed record SsoRoleMapping(string Group, UserRole Role);

/// <summary>Ergebnis der Zuordnung eines SSO-Logins zu einem lokalen Benutzer.</summary>
public sealed record SsoLoginResult(UserAccount? User, string? Error, bool Created = false, bool Linked = false);

/// <summary>
/// SSO per OpenID Connect. Die Einstellungen liegen in der Datenbank und gelten ohne Neustart: beim Speichern wird
/// die zwischengespeicherte Konfiguration des OIDC-Handlers verworfen.
/// Rollen kommen bei jedem Login aus den Gruppen; lokale Benutzer werden über den Benutzernamen verknüpft.
/// </summary>
public sealed class SsoService(IDataProtectionProvider protection, IOptionsMonitorCache<OpenIdConnectOptions> optionsCache)
{
    public const string Scheme = "oidc";
    public const string ExternalCookieScheme = "sso-external";
    public const string CallbackPath = "/signin-oidc";

    private readonly IDataProtector _protector = protection.CreateProtector("AIOPatcher.Sso.v1");
    private volatile SsoSettingsRecord _current = new();

    public SsoSettingsRecord Current => _current;

    public bool IsConfigured => _current is { Enabled: true } s && s.Authority.Length > 0 && s.ClientId.Length > 0;

    public async Task LoadAsync(AppDbContext db, CancellationToken ct)
    {
        _current = await db.SsoSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new SsoSettingsRecord();
        optionsCache.TryRemove(Scheme);
    }

    public string Protect(string secret) => _protector.Protect(secret);

    public static IReadOnlyList<SsoRoleMapping> Mappings(SsoSettingsRecord settings)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SsoRoleMapping>>(settings.RoleMappingsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Konfiguration des OIDC-Handlers aus den gespeicherten Einstellungen.</summary>
    public void Configure(OpenIdConnectOptions o)
    {
        var s = _current;
        o.SignInScheme = ExternalCookieScheme;
        o.CallbackPath = CallbackPath;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false; // Claims so, wie der Anbieter sie schickt ("groups", "preferred_username")
        o.GetClaimsFromUserInfoEndpoint = true;
        o.SaveTokens = false;
        o.Authority = s.Authority.Length > 0 ? s.Authority.TrimEnd('/') : "https://sso.invalid";
        o.ClientId = s.ClientId.Length > 0 ? s.ClientId : "unconfigured";
        o.ClientSecret = s.ProtectedClientSecret is { } secret ? Unprotect(secret) : null;
        o.Scope.Clear();
        foreach (var scope in s.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append("openid").Distinct())
        {
            o.Scope.Add(scope);
        }

        // Gruppen-Claims vom UserInfo-Endpoint übernehmen (sonst landen nur Standard-Claims im Login).
        o.ClaimActions.MapAll();

        if (s.CertificateFingerprint is { Length: > 0 } fingerprint)
        {
            var pinned = Convert.FromHexString(fingerprint.Replace(":", "").Replace(" ", ""));
            o.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || (cert is not null && SHA256.HashData(cert.RawData).AsSpan().SequenceEqual(pinned)),
            };
        }

        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/login?sso=error");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private string? Unprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Gruppen aus dem Login. "groups" (mehrfach oder JSON-Liste) oder ein Pfad wie "realm_access.roles".</summary>
    public static List<string> Groups(ClaimsPrincipal principal, string claim)
    {
        var path = claim.Split('.');
        var values = new List<string>();
        foreach (var c in principal.FindAll(path[0]))
        {
            var text = c.Value.Trim();
            if (path.Length == 1 && !text.StartsWith('[') && !text.StartsWith('{'))
            {
                values.Add(text);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var element = doc.RootElement;
                foreach (var segment in path.Skip(1))
                {
                    element = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var next) ? next : default;
                }

                if (element.ValueKind == JsonValueKind.Array)
                {
                    values.AddRange(element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    values.Add(element.GetString()!);
                }
            }
            catch (JsonException)
            {
                values.Add(text);
            }
        }

        return values.Select(Normalize).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Keycloak liefert Gruppen als Pfad ("/admins"): führenden Schrägstrich ignorieren.</summary>
    public static string Normalize(string group) => group.Trim().TrimStart('/');

    /// <summary>Höchste Rolle aus den Gruppen, sonst die Standardrolle (null = keine Anmeldung).</summary>
    public static UserRole? RoleFor(IEnumerable<string> groups, IReadOnlyList<SsoRoleMapping> mappings, UserRole? defaultRole)
    {
        var set = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = mappings.Where(m => set.Contains(Normalize(m.Group))).Select(m => m.Role).ToList();
        return matched.Count > 0 ? matched.Min() : defaultRole; // Admin = 0, Operator = 1, Viewer = 2
    }

    /// <summary>
    /// SSO-Login → lokaler Benutzer. Reihenfolge: schon verknüpft (Subject) → gleicher Benutzername (wenn erlaubt)
    /// → neu anlegen (wenn erlaubt). Die Rolle wird bei jedem Login aus den Gruppen gesetzt.
    /// </summary>
    public static async Task<SsoLoginResult> ResolveUserAsync(AppDbContext db, SsoSettingsRecord settings, ClaimsPrincipal external, CancellationToken ct)
    {
        var subject = external.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
        {
            return new SsoLoginResult(null, "The provider did not send a subject (sub).");
        }

        var issuerSubject = $"{settings.Authority.TrimEnd('/')}|{subject}";
        var username = (external.FindFirstValue(settings.UsernameClaim) ?? external.FindFirstValue("preferred_username") ?? external.FindFirstValue("email") ?? subject).Trim();
        var role = RoleFor(Groups(external, settings.GroupsClaim), Mappings(settings), settings.DefaultRole);
        if (role is null)
        {
            return new SsoLoginResult(null, $"{username} is in no group that is mapped to a role.");
        }

        var linked = false;
        var created = false;
        var user = await db.Users.SingleOrDefaultAsync(u => u.SsoSubject == issuerSubject, ct);
        if (user is null && settings.LinkByUsername)
        {
            var lower = username.ToLowerInvariant();
            user = await db.Users.SingleOrDefaultAsync(u => u.SsoSubject == null && u.Username.ToLower() == lower, ct);
            if (user is not null)
            {
                user.SsoSubject = issuerSubject;
                linked = true;
            }
        }

        if (user is null)
        {
            if (!settings.AutoCreateUsers)
            {
                return new SsoLoginResult(null, $"{username} has no account in AIOPatcher (automatic creation is off).");
            }

            if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
            {
                return new SsoLoginResult(null, $"A local user named {username} already exists and linking by username is off.");
            }

            user = new UserAccount
            {
                Username = username.Length > 64 ? username[..64] : username,
                PasswordHash = "", // nur SSO, keine lokale Anmeldung
                ProtectedTotpSecret = "",
                IsSetupComplete = true,
                SsoSubject = issuerSubject,
                CreatedBy = "sso",
            };
            db.Users.Add(user);
            created = true;
        }

        if (user.IsDisabled)
        {
            return new SsoLoginResult(null, $"{user.Username} is disabled in AIOPatcher.");
        }

        if (user.Role != role.Value)
        {
            user.Role = role.Value;
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // andere Sitzungen mit alter Rolle beenden
        }

        user.IsSetupComplete = true;
        user.LastLoginAt = DateTime.UtcNow;
        return new SsoLoginResult(user, null, created, linked);
    }
}

/// <summary>Holt die OIDC-Konfiguration bei jedem Neuaufbau aus dem <see cref="SsoService"/>.</summary>
public sealed class SsoOptionsSetup(SsoService sso) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name == SsoService.Scheme)
        {
            sso.Configure(options);
        }
    }

    public void Configure(OpenIdConnectOptions options) => Configure(SsoService.Scheme, options);
}
