using System.Security.Cryptography;
using System.Text;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Regeln rund um Benutzerkonten: Einladungen, Rollenwechsel, Schutz des letzten Admins.</summary>
public static class UserAccounts
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);
    public const int MinPasswordLength = 12;

    public const string AdminPolicy = "Admin";
    public const string OperatorPolicy = "Operator";
    public const string StampClaim = "aiopatcher:stamp";

    public static string HashToken(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    /// <summary>Neue Einladung: setzt Zugangsdaten zurück und liefert den Klartext-Token (nur einmal sichtbar).</summary>
    public static string StartInvite(UserAccount user, DateTime now)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        user.InviteTokenHash = HashToken(token);
        user.InviteExpiresAt = now + InviteLifetime;
        user.PasswordHash = "";
        user.ProtectedTotpSecret = "";
        user.IsSetupComplete = false;
        user.LastTotpTimeStep = 0;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        RotateStamp(user);
        return token;
    }

    public static string InviteUrl(AIOPatcherOptions options, string token) =>
        $"https://{options.UrlHost}:{options.DashboardPort}/invite/{token}";

    public static async Task<UserAccount?> FindByInviteAsync(AppDbContext db, string token, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token);
        var user = await db.Users.SingleOrDefaultAsync(u => u.InviteTokenHash == hash, ct);
        return user is not null && !user.IsDisabled && user.InviteExpiresAt > now ? user : null;
    }

    public static void RotateStamp(UserAccount user) => user.SecurityStamp = Guid.NewGuid().ToString("N");

    public static bool IsActiveAdmin(UserAccount u) => u.Role == UserRole.Admin && !u.IsDisabled && u.IsSetupComplete;

    /// <summary>
    /// Prüft, ob nach der Änderung noch mindestens ein aktiver Admin übrig bleibt.
    /// Gibt eine Fehlermeldung zurück oder null, wenn die Änderung erlaubt ist.
    /// </summary>
    public static async Task<string?> CheckLastAdminAsync(AppDbContext db, UserAccount target, UserRole newRole, bool newDisabled, bool removesLogin, CancellationToken ct)
    {
        if (!IsActiveAdmin(target))
        {
            return null; // war ohnehin kein aktiver Admin
        }

        var staysActiveAdmin = newRole == UserRole.Admin && !newDisabled && !removesLogin;
        if (staysActiveAdmin)
        {
            return null;
        }

        var otherAdmins = await db.Users.CountAsync(u => u.Id != target.Id && u.Role == UserRole.Admin && !u.IsDisabled && u.IsSetupComplete, ct);
        return otherAdmins == 0 ? L.T("Es muss mindestens ein aktiver Admin bleiben.", "At least one active admin must remain.") : null;
    }

    public static string? ValidateUsername(string? username) =>
        username is null || username.Trim().Length is < 3 or > 64 || username.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? L.T("Benutzername muss 3 bis 64 Zeichen lang sein, ohne Leerzeichen.", "Username must be 3 to 64 characters long, without spaces.")
            : null;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? L.T($"Passwort muss mindestens {MinPasswordLength} Zeichen lang sein.", $"Password must be at least {MinPasswordLength} characters long.") : null;

    public static string Status(UserAccount u) =>
        u.IsDisabled ? "Disabled" : !u.IsSetupComplete ? (u.InviteExpiresAt > DateTime.UtcNow ? "Invited" : "InviteExpired") : "Active";
}
