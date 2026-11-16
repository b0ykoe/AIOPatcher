using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Benutzerverwaltung (nur Admins).</summary>
public static class UserEndpoints
{
    public sealed record UserDto(Guid Id, string Username, UserRole Role, string Status, DateTime CreatedAt, DateTime? LastLoginAt, DateTime? InviteExpiresAt, string? CreatedBy);
    public sealed record CreateUserRequest(string Username, UserRole Role);
    public sealed record UpdateUserRequest(UserRole Role, bool Disabled);
    public sealed record InviteResponse(UserDto User, string InviteUrl, DateTime ExpiresAt);

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").RequireAuthorization(UserAccounts.AdminPolicy);
        users.MapGet("/", List);
        users.MapPost("/", Create);
        users.MapPost("/{id:guid}/invite", Reinvite);
        users.MapPut("/{id:guid}", Update);
        users.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<List<UserDto>> List(AppDbContext db, CancellationToken ct) =>
        (await db.Users.OrderBy(u => u.Username).ToListAsync(ct)).Select(ToDto).ToList();

    private static async Task<IResult> Create(CreateUserRequest request, ClaimsPrincipal principal, AppDbContext db, AIOPatcherOptions options, AuditLog audit, CancellationToken ct)
    {
        if (UserAccounts.ValidateUsername(request.Username) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [error] });
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = [L.T("Unbekannte Rolle.", "Unknown role.")] });
        }

        var username = request.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            return Results.Problem(L.T($"Benutzer '{username}' existiert bereits.", $"User '{username}' already exists."),statusCode: StatusCodes.Status409Conflict);
        }

        var user = new UserAccount { Username = username, PasswordHash = "", ProtectedTotpSecret = "", Role = request.Role, CreatedBy = principal.Identity?.Name };
        var token = UserAccounts.StartInvite(user, DateTime.UtcNow);
        db.Users.Add(user);
        audit.Add("user.created", $"{username} ({request.Role})");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new InviteResponse(ToDto(user), UserAccounts.InviteUrl(options, token), user.InviteExpiresAt!.Value));
    }

    /// <summary>Neue Einladung: setzt Passwort und TOTP zurück (z. B. Smartphone verloren).</summary>
    private static async Task<IResult> Reinvite(Guid id, ClaimsPrincipal principal, AppDbContext db, AIOPatcherOptions options, AuditLog audit, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (IsSelf(principal, user))
        {
            return Results.Problem(L.T("Eigene Zugangsdaten unter 'Mein Konto' ändern.", "Change your own credentials under 'My account'."),statusCode: StatusCodes.Status409Conflict);
        }

        if (await UserAccounts.CheckLastAdminAsync(db, user, user.Role, user.IsDisabled, removesLogin: true, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
        }

        var token = UserAccounts.StartInvite(user, DateTime.UtcNow);
        audit.Add("user.reinvited", user.Username);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new InviteResponse(ToDto(user), UserAccounts.InviteUrl(options, token), user.InviteExpiresAt!.Value));
    }

    private static async Task<IResult> Update(Guid id, UpdateUserRequest request, ClaimsPrincipal principal, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = [L.T("Unbekannte Rolle.", "Unknown role.")] });
        }

        if (IsSelf(principal, user) && (request.Disabled || request.Role != user.Role))
        {
            return Results.Problem(L.T("Die eigene Rolle und den eigenen Status kann man nicht ändern.", "You cannot change your own role or status."),statusCode: StatusCodes.Status409Conflict);
        }

        if (await UserAccounts.CheckLastAdminAsync(db, user, request.Role, request.Disabled, removesLogin: false, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
        }

        if (user.Role != request.Role || user.IsDisabled != request.Disabled)
        {
            audit.Add("user.updated", $"{user.Username}: {user.Role}→{request.Role}, disabled {user.IsDisabled}→{request.Disabled}");
            user.Role = request.Role;
            user.IsDisabled = request.Disabled;
            UserAccounts.RotateStamp(user); // bestehende Sitzungen mit alter Rolle beenden
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal principal, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (IsSelf(principal, user))
        {
            return Results.Problem(L.T("Das eigene Konto kann man nicht löschen.", "You cannot delete your own account."),statusCode: StatusCodes.Status409Conflict);
        }

        if (await UserAccounts.CheckLastAdminAsync(db, user, user.Role, user.IsDisabled, removesLogin: true, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
        }

        db.Users.Remove(user);
        audit.Add("user.deleted", user.Username);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static bool IsSelf(ClaimsPrincipal principal, UserAccount user) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString();

    public static UserDto ToDto(UserAccount u) =>
        new(u.Id, u.Username, u.Role, UserAccounts.Status(u), u.CreatedAt, u.LastLoginAt, u.IsSetupComplete ? null : u.InviteExpiresAt, u.CreatedBy);
}
