using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Passkeys (WebAuthn): Anmelden ohne Passwort und Code. Ein Passkey gilt für den Namen, unter dem das Dashboard beim
/// Anlegen aufgerufen wurde (RP-ID = Hostname). Über eine IP-Adresse oder ohne https (außer localhost) gibt es keine
/// Passkeys; dann bleiben Passwort und Code. Reine SSO-Konten bekommen keine Passkeys (Rollen kommen aus dem IdP).
/// </summary>
public static class PasskeyEndpoints
{
    public sealed record PasskeyDto(Guid Id, string Name, string RpId, DateTime CreatedAt, DateTime? LastUsedAt, bool BackedUp);

    public sealed record RegisterRequest(string? RequestId, string? Name, JsonElement Response);

    public sealed record LoginRequest(string? RequestId, JsonElement Response);

    /// <summary>Herkunft der Anfrage: Origin (https://name[:port]) und RP-ID (name).</summary>
    public sealed record PasskeySite(string Origin, string RpId);

    /// <summary>Laufende Zeremonie (Challenge in den Optionen), einmal verwendbar.</summary>
    private sealed record Ceremony(string OptionsJson, Guid? UserId, PasskeySite Site);

    /// <summary>Fehler, die dem Benutzer gezeigt werden (z. B. abgelaufen, falscher Name).</summary>
    public sealed class PasskeyException(string message) : Exception(message);

    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    public static void MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me/passkeys").RequireAuthorization();
        me.MapGet("", ListAsync);
        me.MapPost("/register/options", RegisterOptionsAsync);
        me.MapPost("/register", RegisterAsync);
        me.MapDelete("/{id:guid}", DeleteAsync);

        var auth = app.MapGroup("/api/auth/passkey").RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        auth.MapPost("/options", LoginOptions);
        auth.MapPost("/login", LoginAsync);
    }

    // --- Herkunft ---------------------------------------------------------------------------------

    /// <summary>
    /// Origin und RP-ID aus der Anfrage. Maßgeblich ist der Origin-Header des Browsers (auch hinter einem Proxy, der den
    /// Host umschreibt); die Bindung an den Namen sichern Browser und Authenticator (RP-ID-Hash in der Signatur).
    /// </summary>
    public static PasskeySite? SiteOf(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            origin = $"{request.Scheme}://{request.Host}";
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? SiteOf(uri) : null;
    }

    /// <summary>null = hier keine Passkeys (IP-Adresse, kein https außer localhost).</summary>
    public static PasskeySite? SiteOf(Uri uri)
    {
        if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic) || string.IsNullOrEmpty(uri.IdnHost))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.Scheme != Uri.UriSchemeHttps && host != "localhost")
        {
            return null;
        }

        return new PasskeySite(uri.GetLeftPart(UriPartial.Authority), host);
    }

    private static IFido2 Fido(PasskeySite site) => new Fido2(new Fido2Configuration
    {
        RPID = site.RpId,
        RPName = "AIOPatcher",
        Origins = new HashSet<string>([site.Origin]),
    });

    /// <summary>Darf dieses Konto Passkeys nutzen? Eingerichtet, aktiv und lokal anmeldbar (nicht nur SSO).</summary>
    public static bool CanUse(UserAccount user) => user.IsSetupComplete && !user.IsDisabled && !string.IsNullOrEmpty(user.PasswordHash);

    private static string NoPasskeyHere() => L.T(
        "Passkeys gehen nur, wenn das Dashboard über einen Namen (nicht über eine IP) mit vertrauenswürdigem https-Zertifikat aufgerufen wird.",
        "Passkeys only work when the dashboard is opened by name (not by IP address) with a trusted https certificate.");

    private static string Expired() => L.T("Der Vorgang ist abgelaufen. Bitte erneut versuchen.", "The request has expired. Please try again.");

    private static string NewRequestId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static Ceremony? Take(IMemoryCache cache, string? requestId)
    {
        if (string.IsNullOrEmpty(requestId) || !cache.TryGetValue("passkey:" + requestId, out Ceremony? ceremony))
        {
            return null;
        }

        cache.Remove("passkey:" + requestId); // nur einmal verwendbar
        return ceremony;
    }

    private static PasskeyDto ToDto(UserPasskey p) => new(p.Id, p.Name, p.RpId, p.CreatedAt, p.LastUsedAt, p.BackedUp);

    // --- Registrieren -----------------------------------------------------------------------------

    /// <summary>Optionen für navigator.credentials.create (JSON wie von PublicKeyCredential.parseCreationOptionsFromJSON erwartet).</summary>
    public static async Task<(string RequestId, string OptionsJson)> BeginRegistrationAsync(
        AppDbContext db, IMemoryCache cache, UserAccount user, PasskeySite site, CancellationToken ct)
    {
        if (!CanUse(user))
        {
            throw new PasskeyException(L.T("Für dieses Konto gibt es keine Passkeys (nur SSO oder nicht eingerichtet).",
                "Passkeys are not available for this account (SSO only or not set up)."));
        }

        var existing = await db.Passkeys.Where(p => p.UserId == user.Id && p.RpId == site.RpId).Select(p => p.CredentialId).ToListAsync(ct);
        var options = Fido(site).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Username, DisplayName = user.Username },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var requestId = NewRequestId();
        var json = options.ToJson();
        cache.Set("passkey:" + requestId, new Ceremony(json, user.Id, site), CeremonyLifetime);
        return (requestId, json);
    }

    public static async Task<UserPasskey> CompleteRegistrationAsync(
        AppDbContext db, IMemoryCache cache, UserAccount user, PasskeySite site, string? requestId, string? name, JsonElement response, CancellationToken ct)
    {
        var ceremony = Take(cache, requestId);
        if (ceremony is null || ceremony.UserId != user.Id || ceremony.Site != site)
        {
            throw new PasskeyException(Expired());
        }

        if (!CanUse(user))
        {
            throw new PasskeyException(L.T("Für dieses Konto gibt es keine Passkeys.", "Passkeys are not available for this account."));
        }

        var raw = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(response.GetRawText())
                  ?? throw new PasskeyException(Expired());
        var credential = await Fido(site).MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = raw,
            OriginalOptions = CredentialCreateOptions.FromJson(ceremony.OptionsJson),
            IsCredentialIdUniqueToUserCallback = async (args, token) => !await db.Passkeys.AnyAsync(p => p.CredentialId == args.CredentialId, token),
        }, ct);

        var passkey = new UserPasskey
        {
            UserId = user.Id,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            RpId = site.RpId,
            Name = CleanName(name, site),
            Transports = credential.Transports is { Length: > 0 } t ? string.Join(",", t.Select(x => x.ToString().ToLowerInvariant())) : null,
            BackedUp = credential.IsBackedUp,
        };
        db.Passkeys.Add(passkey);
        return passkey;
    }

    private static string CleanName(string? name, PasskeySite site)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            clean = $"Passkey {site.RpId}";
        }

        return clean.Length > 64 ? clean[..64] : clean;
    }

    // --- Anmelden ---------------------------------------------------------------------------------

    /// <summary>Optionen für navigator.credentials.get: ohne Benutzername (Passkey kennt sein Konto).</summary>
    public static (string RequestId, string OptionsJson) BeginLogin(IMemoryCache cache, PasskeySite site)
    {
        var options = Fido(site).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        var requestId = NewRequestId();
        var json = options.ToJson();
        cache.Set("passkey:" + requestId, new Ceremony(json, null, site), CeremonyLifetime);
        return (requestId, json);
    }

    /// <summary>Prüft die Antwort des Authenticators; Ergebnis: angemeldeter Benutzer (Änderungen noch nicht gespeichert).</summary>
    public static async Task<(UserAccount User, UserPasskey Passkey)> CompleteLoginAsync(
        AppDbContext db, IMemoryCache cache, PasskeySite site, string? requestId, JsonElement response, DateTime now, CancellationToken ct)
    {
        var ceremony = Take(cache, requestId);
        if (ceremony is null || ceremony.UserId is not null || ceremony.Site != site)
        {
            throw new PasskeyException(Expired());
        }

        var raw = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(response.GetRawText())
                  ?? throw new PasskeyException(Expired());
        var unknown = L.T("Dieser Passkey ist hier nicht bekannt.", "This passkey is not known here.");
        var passkey = await db.Passkeys.Include(p => p.User).SingleOrDefaultAsync(p => p.CredentialId == raw.RawId, ct)
                      ?? throw new PasskeyException(unknown);

        // Gilt nur für den Namen, unter dem er angelegt wurde
        if (passkey.RpId != site.RpId)
        {
            throw new PasskeyException(unknown);
        }

        var user = passkey.User!;
        if (!CanUse(user))
        {
            throw new PasskeyException(L.T("Dieses Konto ist deaktiviert oder nicht eingerichtet.", "This account is disabled or not set up."));
        }

        var result = await Fido(site).MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = raw,
            OriginalOptions = AssertionOptions.FromJson(ceremony.OptionsJson),
            StoredPublicKey = passkey.PublicKey,
            StoredSignatureCounter = (uint)passkey.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(passkey.UserId.ToByteArray())),
        }, ct);

        passkey.SignCount = result.SignCount;
        passkey.BackedUp = result.IsBackedUp;
        passkey.LastUsedAt = now;
        user.LastLoginAt = now;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        return (user, passkey);
    }

    // --- Endpunkte --------------------------------------------------------------------------------

    private static async Task<UserAccount?> CurrentUserAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? await db.Users.FindAsync([id], ct) : null;

    private static IResult Problem(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (await CurrentUserAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        var list = await db.Passkeys.AsNoTracking().Where(p => p.UserId == user.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        return Results.Ok(new { available = CanUse(user), passkeys = list.Select(ToDto) });
    }

    private static async Task<IResult> RegisterOptionsAsync(HttpContext http, ClaimsPrincipal principal, AppDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        if (await CurrentUserAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        if (SiteOf(http.Request) is not { } site)
        {
            return Problem(NoPasskeyHere());
        }

        try
        {
            var (requestId, json) = await BeginRegistrationAsync(db, cache, user, site, ct);
            return Envelope(requestId, json);
        }
        catch (PasskeyException ex)
        {
            return Problem(ex.Message);
        }
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, IMemoryCache cache,
        AuditLog audit, CancellationToken ct)
    {
        if (await CurrentUserAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        if (SiteOf(http.Request) is not { } site)
        {
            return Problem(NoPasskeyHere());
        }

        try
        {
            var passkey = await CompleteRegistrationAsync(db, cache, user, site, request.RequestId, request.Name, request.Response, ct);
            audit.Add("auth.passkey.added", $"{user.Username}: {passkey.Name} ({passkey.RpId})");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(passkey));
        }
        catch (Exception ex) when (ex is PasskeyException or Fido2VerificationException or JsonException)
        {
            return Problem(ex is PasskeyException ? ex.Message : L.T($"Passkey wurde nicht angenommen: {ex.Message}", $"Passkey was not accepted: {ex.Message}"));
        }
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal principal, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (await CurrentUserAsync(principal, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }

        var passkey = await db.Passkeys.SingleOrDefaultAsync(p => p.Id == id && p.UserId == user.Id, ct);
        if (passkey is null)
        {
            return Results.NotFound();
        }

        db.Passkeys.Remove(passkey);
        audit.Add("auth.passkey.removed", $"{user.Username}: {passkey.Name} ({passkey.RpId})");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult LoginOptions(HttpContext http, IMemoryCache cache)
    {
        if (SiteOf(http.Request) is not { } site)
        {
            return Problem(NoPasskeyHere());
        }

        var (requestId, json) = BeginLogin(cache, site);
        return Envelope(requestId, json);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, AppDbContext db, IMemoryCache cache, AuditLog audit, CancellationToken ct)
    {
        if (SiteOf(http.Request) is not { } site)
        {
            return Problem(NoPasskeyHere());
        }

        try
        {
            var (user, passkey) = await CompleteLoginAsync(db, cache, site, request.RequestId, request.Response, DateTime.UtcNow, ct);
            audit.Add("auth.login", $"passkey {passkey.Name} ({passkey.RpId})", actor: $"user:{user.Username}");
            await db.SaveChangesAsync(ct);
            await AuthEndpoints.SignInAsync(http, user);
            return Results.Ok(AuthEndpoints.ToMe(user));
        }
        catch (Exception ex) when (ex is PasskeyException or Fido2VerificationException or JsonException)
        {
            audit.Add("auth.login.failed", $"passkey: {ex.Message}", actor: "anonymous");
            await db.SaveChangesAsync(ct);
            return Results.Problem(ex is PasskeyException ? ex.Message : L.T("Anmeldung mit Passkey fehlgeschlagen.", "Sign-in with passkey failed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>{"requestId": "...", "options": {...}}; die Optionen kommen fertig als JSON aus der Bibliothek.</summary>
    private static IResult Envelope(string requestId, string optionsJson) =>
        Results.Text($"{{\"requestId\":\"{requestId}\",\"options\":{optionsJson}}}", "application/json");
}
