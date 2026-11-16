using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Ersteinrichtung, Login, Einladungen annehmen und das eigene Konto.</summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    // Zweck-String nicht ändern: damit verschlüsselte TOTP-Secrets bleiben sonst unlesbar.
    private const string TotpProtectorPurpose = "AIOPatcher.AdminUser.Totp";
    private const string Issuer = "AIOPatcher";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public sealed record SetupStatusResponse(bool SetupRequired);
    public sealed record SetupStartRequest(string SetupToken, string Username, string Password, string SiteName);
    public sealed record TotpSetupResponse(string TotpSecret, string OtpAuthUri);
    public sealed record SetupCompleteRequest(string SetupToken, string Code);
    public sealed record LoginRequest(string Username, string Password, string Code);
    public sealed record MeResponse(Guid Id, string Username, UserRole Role, string? Language);
    public sealed record InviteInfoResponse(string Username, UserRole Role, DateTime ExpiresAt);
    public sealed record InviteStartRequest(string Token, string Password);
    public sealed record InviteCompleteRequest(string Token, string Code);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string Code);
    public sealed record PreferencesRequest(string? Language);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup");
        // Status wird bei jedem Seitenaufruf abgefragt und verrät nichts Schützenswertes: kein Rate-Limit.
        setup.MapGet("/status", (SetupTokenService setupTokens) => new SetupStatusResponse(setupTokens.IsSetupRequired));
        setup.MapPost("/start", SetupStart).RequireRateLimiting(RateLimitPolicy);
        setup.MapPost("/complete", SetupComplete).RequireRateLimiting(RateLimitPolicy);

        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", Login).RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/logout", Logout).RequireAuthorization();
        auth.MapGet("/me", Me).RequireAuthorization();

        var invite = app.MapGroup("/api/invite").RequireRateLimiting(RateLimitPolicy);
        invite.MapGet("/{token}", InviteInfo);
        invite.MapPost("/start", InviteStart);
        invite.MapPost("/complete", InviteComplete);

        var me = app.MapGroup("/api/me").RequireAuthorization();
        me.MapPut("/password", ChangePassword).RequireRateLimiting(RateLimitPolicy);
        me.MapPut("/preferences", UpdatePreferences);
    }

    // --- Ersteinrichtung --------------------------------------------------------------------------

    private static async Task<IResult> SetupStart(
        SetupStartRequest request,
        SetupTokenService setupTokens,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher,
        IDataProtectionProvider dataProtection,
        CancellationToken ct)
    {
        if (!setupTokens.Validate(request.SetupToken))
        {
            return Results.Problem(L.T("Setup-Token ungültig oder Setup bereits abgeschlossen.", "Setup token invalid or setup already completed."),statusCode: StatusCodes.Status403Forbidden);
        }

        if (UserAccounts.ValidateUsername(request.Username) is { } usernameError)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [usernameError] });
        }

        if (UserAccounts.ValidatePassword(request.Password) is { } passwordError)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });
        }

        var siteName = request.SiteName?.Trim() ?? "";
        if (siteName.Length is < 1 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteName"] = [L.T("Site-Name muss 1 bis 100 Zeichen lang sein.", "Site name must be 1 to 100 characters long.")] });
        }

        // Abgebrochene Setup-Versuche verwerfen: der erste Admin entsteht genau einmal.
        db.Users.RemoveRange(await db.Users.Where(u => !u.IsSetupComplete).ToListAsync(ct));

        var admin = new UserAccount { Username = request.Username.Trim(), PasswordHash = "", ProtectedTotpSecret = "", Role = UserRole.Admin, CreatedBy = "setup" };
        admin.PasswordHash = hasher.HashPassword(admin, request.Password);
        var secret = NewTotpSecret(dataProtection, admin);
        db.Users.Add(admin);

        if (!await db.Sites.AnyAsync(ct))
        {
            db.Sites.Add(new Site { Name = siteName });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new TotpSetupResponse(Base32.Encode(secret), Totp.BuildOtpAuthUri(Issuer, admin.Username, secret)));
    }

    private static async Task<IResult> SetupComplete(
        SetupCompleteRequest request,
        HttpContext http,
        SetupTokenService setupTokens,
        AppDbContext db,
        IDataProtectionProvider dataProtection,
        AuditLog audit,
        CancellationToken ct)
    {
        if (!setupTokens.Validate(request.SetupToken))
        {
            return Results.Problem(L.T("Setup-Token ungültig oder Setup bereits abgeschlossen.", "Setup token invalid or setup already completed."),statusCode: StatusCodes.Status403Forbidden);
        }

        var admin = await db.Users.SingleOrDefaultAsync(u => !u.IsSetupComplete, ct);
        if (admin is null)
        {
            return Results.Problem(L.T("Kein offenes Setup gefunden. Bitte von vorne beginnen.", "No pending setup found. Please start over."),statusCode: StatusCodes.Status409Conflict);
        }

        if (!TryConfirmTotp(dataProtection, admin, request.Code))
        {
            return Results.Problem(L.T("Code falsch. Uhrzeit von Server und Smartphone prüfen.", "Wrong code. Check the clock on the server and your phone."),statusCode: StatusCodes.Status400BadRequest);
        }

        admin.IsSetupComplete = true;
        admin.LastLoginAt = DateTime.UtcNow;
        audit.Add("setup.completed", $"Admin '{admin.Username}' set up", actor: $"admin:{admin.Username}");
        await db.SaveChangesAsync(ct);
        setupTokens.MarkCompleted();

        await SignInAsync(http, admin);
        return Results.Ok(ToMe(admin));
    }

    // --- Login --------------------------------------------------------------------------------------

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher,
        IDataProtectionProvider dataProtection,
        AuditLog audit,
        CancellationToken ct)
    {
        var invalid = Results.Problem(L.T("Anmeldung fehlgeschlagen.", "Sign-in failed."),statusCode: StatusCodes.Status401Unauthorized);
        var username = request.Username?.Trim() ?? "";
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username && u.IsSetupComplete && !u.IsDisabled, ct);

        if (user is null)
        {
            // Gleiche Laufzeit wie bei existierendem Benutzer, damit Benutzernamen nicht erratbar sind.
            hasher.HashPassword(new UserAccount { Username = "", PasswordHash = "", ProtectedTotpSecret = "" }, request.Password ?? "");
            audit.Add("auth.login.failed", "Unknown or disabled user", actor: $"anonymous:{username}");
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (user.LockoutUntil > DateTime.UtcNow)
        {
            return Results.Problem(L.T("Konto vorübergehend gesperrt. Später erneut versuchen.", "Account temporarily locked. Try again later."),statusCode: StatusCodes.Status423Locked);
        }

        var passwordResult = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "");
        var codeOk = TryVerifyTotp(dataProtection, user, request.Code, out var step);

        if (passwordResult == PasswordVerificationResult.Failed || !codeOk)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockoutUntil = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginCount = 0;
                audit.Add("auth.lockout", $"Account locked for {LockoutDuration.TotalMinutes} minutes", actor: $"user:{user.Username}");
            }

            audit.Add("auth.login.failed", passwordResult == PasswordVerificationResult.Failed ? "Wrong password" : "Wrong TOTP", actor: $"user:{user.Username}");
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (passwordResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password!);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastTotpTimeStep = step;
        user.LastLoginAt = DateTime.UtcNow;
        audit.Add("auth.login", actor: $"admin:{user.Username}");
        await db.SaveChangesAsync(ct);

        await SignInAsync(http, user);
        return Results.Ok(ToMe(user));
    }

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        audit.Add("auth.logout");
        await db.SaveChangesAsync(ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentAsync(principal, db, ct);
        return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
    }

    // --- Einladung annehmen -------------------------------------------------------------------------

    private static async Task<IResult> InviteInfo(string token, AppDbContext db, CancellationToken ct)
    {
        var user = await UserAccounts.FindByInviteAsync(db, token, DateTime.UtcNow, ct);
        return user is null
            ? Results.Problem(L.T("Einladung ungültig oder abgelaufen. Bitte eine neue anfordern.", "Invitation invalid or expired. Please request a new one."),statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new InviteInfoResponse(user.Username, user.Role, user.InviteExpiresAt!.Value));
    }

    private static async Task<IResult> InviteStart(InviteStartRequest request, AppDbContext db, IPasswordHasher<UserAccount> hasher, IDataProtectionProvider dataProtection, CancellationToken ct)
    {
        var user = await UserAccounts.FindByInviteAsync(db, request.Token, DateTime.UtcNow, ct);
        if (user is null)
        {
            return Results.Problem(L.T("Einladung ungültig oder abgelaufen. Bitte eine neue anfordern.", "Invitation invalid or expired. Please request a new one."),statusCode: StatusCodes.Status404NotFound);
        }

        if (UserAccounts.ValidatePassword(request.Password) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [error] });
        }

        user.PasswordHash = hasher.HashPassword(user, request.Password);
        var secret = NewTotpSecret(dataProtection, user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TotpSetupResponse(Base32.Encode(secret), Totp.BuildOtpAuthUri(Issuer, user.Username, secret)));
    }

    private static async Task<IResult> InviteComplete(InviteCompleteRequest request, HttpContext http, AppDbContext db, IDataProtectionProvider dataProtection, AuditLog audit, CancellationToken ct)
    {
        var user = await UserAccounts.FindByInviteAsync(db, request.Token, DateTime.UtcNow, ct);
        if (user is null || user.PasswordHash.Length == 0 || user.ProtectedTotpSecret.Length == 0)
        {
            return Results.Problem(L.T("Einladung ungültig oder abgelaufen. Bitte eine neue anfordern.", "Invitation invalid or expired. Please request a new one."),statusCode: StatusCodes.Status404NotFound);
        }

        if (!TryConfirmTotp(dataProtection, user, request.Code))
        {
            return Results.Problem(L.T("Code falsch. Uhrzeit von Server und Smartphone prüfen.", "Wrong code. Check the clock on the server and your phone."),statusCode: StatusCodes.Status400BadRequest);
        }

        user.IsSetupComplete = true;
        user.InviteTokenHash = null;
        user.InviteExpiresAt = null;
        user.LastLoginAt = DateTime.UtcNow;
        UserAccounts.RotateStamp(user);
        audit.Add("user.invite-accepted", user.Username, actor: $"admin:{user.Username}");
        await db.SaveChangesAsync(ct);

        await SignInAsync(http, user);
        return Results.Ok(ToMe(user));
    }

    // --- Eigenes Konto ------------------------------------------------------------------------------

    private static async Task<IResult> ChangePassword(ChangePasswordRequest request, ClaimsPrincipal principal, HttpContext http, AppDbContext db, IPasswordHasher<UserAccount> hasher, IDataProtectionProvider dataProtection, AuditLog audit, CancellationToken ct)
    {
        var user = await CurrentAsync(principal, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed
            || !TryVerifyTotp(dataProtection, user, request.Code, out var step))
        {
            return Results.Problem(L.T("Aktuelles Passwort oder Code falsch.", "Current password or code is wrong."),statusCode: StatusCodes.Status400BadRequest);
        }

        if (UserAccounts.ValidatePassword(request.NewPassword) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [error] });
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.LastTotpTimeStep = step;
        UserAccounts.RotateStamp(user); // andere Sitzungen abmelden
        audit.Add("user.password-changed", user.Username);
        await db.SaveChangesAsync(ct);
        await SignInAsync(http, user); // diese Sitzung mit neuem Merkmal behalten
        return Results.NoContent();
    }

    private static async Task<IResult> UpdatePreferences(PreferencesRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentAsync(principal, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (request.Language is not (null or "de" or "en"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["language"] = [L.T("Unterstützt: de, en.", "Supported: de, en.")] });
        }

        user.Language = request.Language;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToMe(user));
    }

    // --- Hilfen -------------------------------------------------------------------------------------

    public static async Task<UserAccount?> CurrentAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? await db.Users.FindAsync([id], ct) : null;

    private static MeResponse ToMe(UserAccount u) => new(u.Id, u.Username, u.Role, u.Language);

    private static byte[] NewTotpSecret(IDataProtectionProvider dataProtection, UserAccount user)
    {
        var secret = Totp.GenerateSecret();
        user.ProtectedTotpSecret = dataProtection.CreateProtector(TotpProtectorPurpose).Protect(Convert.ToBase64String(secret));
        user.LastTotpTimeStep = 0;
        return secret;
    }

    private static bool TryVerifyTotp(IDataProtectionProvider dataProtection, UserAccount user, string? code, out long step)
    {
        step = 0;
        if (string.IsNullOrEmpty(user.ProtectedTotpSecret))
        {
            return false;
        }

        var secret = Convert.FromBase64String(dataProtection.CreateProtector(TotpProtectorPurpose).Unprotect(user.ProtectedTotpSecret));
        return Totp.TryVerify(secret, code, DateTimeOffset.UtcNow, user.LastTotpTimeStep, out step);
    }

    private static bool TryConfirmTotp(IDataProtectionProvider dataProtection, UserAccount user, string? code)
    {
        if (!TryVerifyTotp(dataProtection, user, code, out var step))
        {
            return false;
        }

        user.LastTotpTimeStep = step;
        return true;
    }

    public static Task SignInAsync(HttpContext http, UserAccount user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(UserAccounts.StampClaim, user.SecurityStamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
