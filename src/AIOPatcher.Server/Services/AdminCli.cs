using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Notfallzugang über die Kommandozeile, z. B. wenn der einzige Admin sein Smartphone verloren hat:
/// <c>docker exec aiopatcher dotnet AIOPatcher.Server.dll admin invite &lt;name&gt;</c>.
/// Wer die Shell des Servers hat, hat ohnehin Zugriff auf alle Daten.
/// </summary>
public static class AdminCli
{
    public static bool IsCliInvocation(string[] args) => args.Length > 0 && args[0] == "admin";

    public static async Task<int> RunAsync(string[] args, AppDbContext db, AIOPatcherOptions options, CancellationToken ct)
    {
        var command = args.Length > 1 ? args[1] : "help";
        var username = args.Length > 2 ? args[2].Trim() : null;

        switch (command)
        {
            case "list":
                foreach (var u in await db.Users.OrderBy(u => u.Username).ToListAsync(ct))
                {
                    Console.WriteLine($"{u.Username,-24} {u.Role,-9} {UserAccounts.Status(u),-14} last login: {u.LastLoginAt?.ToString("u") ?? "-"}");
                }

                return 0;

            case "invite" when username is not null:
            {
                var role = UserRole.Admin;
                var roleIndex = Array.IndexOf(args, "--role");
                if (roleIndex > 0 && (roleIndex + 1 >= args.Length || !Enum.TryParse(args[roleIndex + 1], ignoreCase: true, out role)))
                {
                    Console.Error.WriteLine("--role must be Admin, Operator or Viewer.");
                    return 2;
                }

                if (UserAccounts.ValidateUsername(username) is { } error)
                {
                    Console.Error.WriteLine(error);
                    return 2;
                }

                var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
                if (user is null)
                {
                    user = new UserAccount { Username = username, PasswordHash = "", ProtectedTotpSecret = "", Role = role, CreatedBy = "cli" };
                    db.Users.Add(user);
                }
                else if (roleIndex > 0)
                {
                    user.Role = role;
                }

                user.IsDisabled = false;
                var token = UserAccounts.StartInvite(user, DateTime.UtcNow);
                db.AuditEntries.Add(new AuditEntry { Actor = "cli", Action = "user.reinvited", Details = $"{user.Username} ({user.Role}) via CLI" });
                await db.SaveChangesAsync(ct);

                Console.WriteLine($"Invite for '{user.Username}' ({user.Role}), valid until {user.InviteExpiresAt:u}:");
                Console.WriteLine(UserAccounts.InviteUrl(options, token));
                Console.WriteLine("Password and TOTP of this account have been reset. Existing sessions are signed out.");
                return 0;
            }

            case "disable" or "enable" when username is not null:
            {
                var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
                if (user is null)
                {
                    Console.Error.WriteLine($"User '{username}' not found.");
                    return 1;
                }

                var disable = command == "disable";
                if (disable && await UserAccounts.CheckLastAdminAsync(db, user, user.Role, newDisabled: true, removesLogin: false, ct) is { } problem)
                {
                    Console.Error.WriteLine(problem);
                    return 1;
                }

                user.IsDisabled = disable;
                UserAccounts.RotateStamp(user);
                db.AuditEntries.Add(new AuditEntry { Actor = "cli", Action = "user.updated", Details = $"{user.Username}: disabled={disable} via CLI" });
                await db.SaveChangesAsync(ct);
                Console.WriteLine($"'{user.Username}' {(disable ? "disabled" : "enabled")}.");
                return 0;
            }

            default:
                Console.WriteLine("""
                    Usage:
                      admin list
                      admin invite <username> [--role Admin|Operator|Viewer]   create user or reset password+TOTP, prints invite link
                      admin disable <username>
                      admin enable <username>
                    """);
                return command == "help" ? 0 : 2;
        }
    }
}
