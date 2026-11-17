using System.Security.Claims;
using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// SSO per OpenID Connect. Ablauf: /api/auth/sso/login → Anbieter → /signin-oidc (Handler) → /api/auth/sso/complete
/// (Benutzer zuordnen, normale Sitzung anlegen) → Dashboard.
/// </summary>
public static class SsoEndpoints
{
    public sealed record SsoInfoDto(bool Enabled, string DisplayName);

    public sealed record SsoSettingsDto(
        bool Enabled,
        string DisplayName,
        string Authority,
        string ClientId,
        bool HasClientSecret,
        string Scopes,
        string UsernameClaim,
        string GroupsClaim,
        IReadOnlyList<SsoRoleMapping> RoleMappings,
        UserRole? DefaultRole,
        bool AutoCreateUsers,
        bool LinkByUsername,
        string? CertificateFingerprint,
        string CallbackPath,
        DateTime UpdatedAt,
        string? UpdatedBy);

    /// <param name="ClientSecret">null = unverändert lassen, "" = löschen.</param>
    public sealed record SaveSsoSettingsRequest(
        bool Enabled,
        string? DisplayName,
        string? Authority,
        string? ClientId,
        string? ClientSecret,
        string? Scopes,
        string? UsernameClaim,
        string? GroupsClaim,
        IReadOnlyList<SsoRoleMapping>? RoleMappings,
        UserRole? DefaultRole,
        bool AutoCreateUsers,
        bool LinkByUsername,
        string? CertificateFingerprint);

    public static void MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/sso/info", (SsoService sso) => new SsoInfoDto(sso.IsConfigured, sso.Current.DisplayName));
        app.MapGet("/api/auth/sso/login", Login).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        app.MapGet("/api/auth/sso/complete", Complete);

        var admin = app.MapGroup("/api").RequireAuthorization(UserAccounts.AdminPolicy);
        admin.MapGet("/settings/sso", (SsoService sso) => ToDto(sso.Current));
        admin.MapPut("/settings/sso", Save);
        admin.MapDelete("/users/{id:guid}/sso", Unlink);
    }

    private static async Task Login(HttpContext http, SsoService sso, ILoggerFactory loggerFactory)
    {
        if (!sso.IsConfigured)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            await http.ChallengeAsync(SsoService.Scheme, new AuthenticationProperties { RedirectUri = "/api/auth/sso/complete" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            // Anbieter nicht erreichbar oder Metadaten ungültig: zurück zur Anmeldung statt Fehlerseite.
            loggerFactory.CreateLogger("AIOPatcher.Sso").LogWarning("SSO provider not reachable: {Message}", ex.Message);
            http.Response.Redirect("/login?sso=error");
        }
    }

    /// <summary>
    /// Nach dem Anbieter: Benutzer zuordnen und anmelden. Antwortet mit einer kleinen Seite, die per Meta-Refresh
    /// weiterleitet: Das Sitzungs-Cookie ist SameSite=Strict und würde bei einer Weiterleitung, die beim Anbieter
    /// begann, sonst nicht mitgeschickt.
    /// </summary>
    private static async Task<IResult> Complete(HttpContext http, SsoService sso, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var external = await http.AuthenticateAsync(SsoService.ExternalCookieScheme);
        await http.SignOutAsync(SsoService.ExternalCookieScheme);
        if (!external.Succeeded || external.Principal is null || !sso.IsConfigured)
        {
            return Redirect("/login?sso=error");
        }

        var result = await SsoService.ResolveUserAsync(db, sso.Current, external.Principal, ct);
        var who = external.Principal.FindFirstValue(sso.Current.UsernameClaim) ?? external.Principal.FindFirstValue("sub") ?? "?";
        if (result.User is null)
        {
            audit.Add("auth.sso.denied", result.Error, actor: $"sso:{who}");
            await db.SaveChangesAsync(ct);
            return Redirect("/login?sso=denied");
        }

        var user = result.User;
        audit.Add(result.Created ? "auth.sso.created" : result.Linked ? "auth.sso.linked" : "auth.sso.login",
            $"role {user.Role}", actor: $"user:{user.Username}");
        await db.SaveChangesAsync(ct);
        await AuthEndpoints.SignInAsync(http, user);
        return Redirect("/");
    }

    private static IResult Redirect(string target) =>
        Results.Content($"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={target}\"><title>AIOPatcher</title><a href=\"{target}\">AIOPatcher</a>",
            "text/html");

    private static SsoSettingsDto ToDto(SsoSettingsRecord s) => new(
        s.Enabled, s.DisplayName, s.Authority, s.ClientId, s.ProtectedClientSecret is not null, s.Scopes, s.UsernameClaim, s.GroupsClaim,
        SsoService.Mappings(s), s.DefaultRole, s.AutoCreateUsers, s.LinkByUsername, s.CertificateFingerprint, SsoService.CallbackPath, s.UpdatedAt, s.UpdatedBy);

    private static async Task<IResult> Save(SaveSsoSettingsRequest request, ClaimsPrincipal user, SsoService sso, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var authority = request.Authority?.Trim().TrimEnd('/') ?? "";
        if (request.Enabled && (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["authority"] = [L.T("Issuer-URL mit https:// angeben, z. B. https://sso.example.com/realms/homelab.", "Issuer URL with https://, e.g. https://sso.example.com/realms/homelab.")];
        }

        if (request.Enabled && string.IsNullOrWhiteSpace(request.ClientId))
        {
            errors["clientId"] = [L.T("Client-ID fehlt.", "Client ID missing.")];
        }

        var mappings = (request.RoleMappings ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Group))
            .Select(m => m with { Group = SsoService.Normalize(m.Group) }).DistinctBy(m => m.Group.ToLowerInvariant()).ToList();
        if (request.Enabled && mappings.Count == 0 && request.DefaultRole is null)
        {
            errors["roleMappings"] = [L.T("Mindestens eine Gruppe einer Rolle zuordnen (oder eine Standardrolle wählen).", "Map at least one group to a role (or choose a default role).")];
        }

        var fingerprint = request.CertificateFingerprint?.Replace(":", "").Replace(" ", "").ToUpperInvariant();
        if (!string.IsNullOrEmpty(fingerprint) && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
        {
            errors["certificateFingerprint"] = [L.T("SHA-256-Fingerprint (64 Hex-Zeichen).", "SHA-256 fingerprint (64 hex characters).")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var row = await db.SsoSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            db.SsoSettings.Add(row = new SsoSettingsRecord());
        }

        row.Enabled = request.Enabled;
        row.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "SSO" : request.DisplayName.Trim()[..Math.Min(100, request.DisplayName.Trim().Length)];
        row.Authority = authority;
        row.ClientId = request.ClientId?.Trim() ?? "";
        if (request.ClientSecret is { } secret)
        {
            row.ProtectedClientSecret = secret.Length == 0 ? null : sso.Protect(secret);
        }

        row.Scopes = string.IsNullOrWhiteSpace(request.Scopes) ? "openid profile email" : request.Scopes.Trim();
        row.UsernameClaim = string.IsNullOrWhiteSpace(request.UsernameClaim) ? "preferred_username" : request.UsernameClaim.Trim();
        row.GroupsClaim = string.IsNullOrWhiteSpace(request.GroupsClaim) ? "groups" : request.GroupsClaim.Trim();
        row.RoleMappingsJson = JsonSerializer.Serialize(mappings, SsoService.JsonOptions);
        row.DefaultRole = request.DefaultRole;
        row.AutoCreateUsers = request.AutoCreateUsers;
        row.LinkByUsername = request.LinkByUsername;
        row.CertificateFingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = user.Identity?.Name;
        audit.Add("settings.sso", $"{(row.Enabled ? "enabled" : "disabled")}, {row.Authority}, {mappings.Count} group mappings");
        await db.SaveChangesAsync(ct);
        await sso.LoadAsync(db, ct);
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> Unlink(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            return Results.Problem(L.T("Dieser Benutzer hat kein Passwort und kann sich nur per SSO anmelden. Stattdessen deaktivieren oder löschen.",
                "This user has no password and can only sign in via SSO. Disable or delete instead."), statusCode: StatusCodes.Status409Conflict);
        }

        user.SsoSubject = null;
        audit.Add("user.sso.unlinked", user.Username);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
