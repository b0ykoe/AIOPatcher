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

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    private const string TotpProtectorPurpose = "AIOPatcher.AdminUser.Totp";
    private const string Issuer = "AIOPatcher";
    private const int MinPasswordLength = 12;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public sealed record SetupStatusResponse(bool SetupRequired);
    public sealed record SetupStartRequest(string SetupToken, string Username, string Password, string SiteName);
    public sealed record SetupStartResponse(string TotpSecret, string OtpAuthUri);
    public sealed record SetupCompleteRequest(string SetupToken, string Code);
    public sealed record LoginRequest(string Username, string Password, string Code);
    public sealed record MeResponse(string Username);

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
        auth.MapGet("/me", (ClaimsPrincipal user) => new MeResponse(user.Identity!.Name!)).RequireAuthorization();
    }

    private static async Task<IResult> SetupStart(
        SetupStartRequest request,
        SetupTokenService setupTokens,
        AppDbContext db,
        IPasswordHasher<AdminUser> hasher,
        IDataProtectionProvider dataProtection,
        CancellationToken ct)
    {
        if (!setupTokens.Validate(request.SetupToken))
        {
            return Results.Problem("Setup-Token ungültig oder Setup bereits abgeschlossen.", statusCode: StatusCodes.Status403Forbidden);
        }

        var username = request.Username.Trim();
        if (username.Length is < 3 or > 64)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Benutzername muss 3 bis 64 Zeichen lang sein."] });
        }

        if (request.Password.Length < MinPasswordLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [$"Passwort muss mindestens {MinPasswordLength} Zeichen lang sein."] });
        }

        var siteName = request.SiteName.Trim();
        if (siteName.Length is < 1 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteName"] = ["Site-Name muss 1 bis 100 Zeichen lang sein."] });
        }

        // Abgebrochene Setup-Versuche verwerfen, es gibt genau einen Admin.
        db.AdminUsers.RemoveRange(await db.AdminUsers.Where(u => !u.IsSetupComplete).ToListAsync(ct));

        var secret = Totp.GenerateSecret();
        var protector = dataProtection.CreateProtector(TotpProtectorPurpose);
        var admin = new AdminUser
        {
            Username = username,
            PasswordHash = "",
            ProtectedTotpSecret = protector.Protect(Convert.ToBase64String(secret)),
        };
        admin.PasswordHash = hasher.HashPassword(admin, request.Password);
        db.AdminUsers.Add(admin);

        if (!await db.Sites.AnyAsync(ct))
        {
            db.Sites.Add(new Site { Name = siteName });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new SetupStartResponse(Base32.Encode(secret), Totp.BuildOtpAuthUri(Issuer, username, secret)));
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
            return Results.Problem("Setup-Token ungültig oder Setup bereits abgeschlossen.", statusCode: StatusCodes.Status403Forbidden);
        }

        var admin = await db.AdminUsers.SingleOrDefaultAsync(u => !u.IsSetupComplete, ct);
        if (admin is null)
        {
            return Results.Problem("Kein offenes Setup gefunden. Bitte von vorne beginnen.", statusCode: StatusCodes.Status409Conflict);
        }

        var secret = UnprotectSecret(dataProtection, admin);
        if (!Totp.TryVerify(secret, request.Code, DateTimeOffset.UtcNow, admin.LastTotpTimeStep, out var step))
        {
            return Results.Problem("Code falsch. Uhrzeit von Server und Smartphone prüfen.", statusCode: StatusCodes.Status400BadRequest);
        }

        admin.IsSetupComplete = true;
        admin.LastTotpTimeStep = step;
        admin.LastLoginAt = DateTime.UtcNow;
        audit.Add("setup.completed", $"Admin '{admin.Username}' eingerichtet", actor: $"admin:{admin.Username}");
        await db.SaveChangesAsync(ct);
        setupTokens.MarkCompleted();

        await SignInAsync(http, admin);
        return Results.Ok(new MeResponse(admin.Username));
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<AdminUser> hasher,
        IDataProtectionProvider dataProtection,
        AuditLog audit,
        CancellationToken ct)
    {
        var invalid = Results.Problem("Anmeldung fehlgeschlagen.", statusCode: StatusCodes.Status401Unauthorized);
        var username = request.Username.Trim();
        var admin = await db.AdminUsers.SingleOrDefaultAsync(u => u.Username == username && u.IsSetupComplete, ct);

        if (admin is null)
        {
            // Gleiche Laufzeit wie bei existierendem User, damit Benutzernamen nicht erratbar sind.
            hasher.HashPassword(new AdminUser { Username = "", PasswordHash = "", ProtectedTotpSecret = "" }, request.Password);
            audit.Add("auth.login.failed", "Unbekannter Benutzer", actor: $"anonymous:{username}");
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (admin.LockoutUntil > DateTime.UtcNow)
        {
            return Results.Problem("Konto vorübergehend gesperrt. Später erneut versuchen.", statusCode: StatusCodes.Status423Locked);
        }

        var passwordResult = hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password);
        var secret = UnprotectSecret(dataProtection, admin);
        var codeOk = Totp.TryVerify(secret, request.Code, DateTimeOffset.UtcNow, admin.LastTotpTimeStep, out var step);

        if (passwordResult == PasswordVerificationResult.Failed || !codeOk)
        {
            admin.FailedLoginCount++;
            if (admin.FailedLoginCount >= MaxFailedLogins)
            {
                admin.LockoutUntil = DateTime.UtcNow.Add(LockoutDuration);
                admin.FailedLoginCount = 0;
                audit.Add("auth.lockout", $"Konto für {LockoutDuration.TotalMinutes} Minuten gesperrt", actor: $"admin:{admin.Username}");
            }

            audit.Add("auth.login.failed", passwordResult == PasswordVerificationResult.Failed ? "Passwort falsch" : "TOTP falsch", actor: $"admin:{admin.Username}");
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (passwordResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            admin.PasswordHash = hasher.HashPassword(admin, request.Password);
        }

        admin.FailedLoginCount = 0;
        admin.LockoutUntil = null;
        admin.LastTotpTimeStep = step;
        admin.LastLoginAt = DateTime.UtcNow;
        audit.Add("auth.login", actor: $"admin:{admin.Username}");
        await db.SaveChangesAsync(ct);

        await SignInAsync(http, admin);
        return Results.Ok(new MeResponse(admin.Username));
    }

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        audit.Add("auth.logout");
        await db.SaveChangesAsync(ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static byte[] UnprotectSecret(IDataProtectionProvider dataProtection, AdminUser admin) =>
        Convert.FromBase64String(dataProtection.CreateProtector(TotpProtectorPurpose).Unprotect(admin.ProtectedTotpSecret));

    private static Task SignInAsync(HttpContext http, AdminUser admin)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.Username),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
