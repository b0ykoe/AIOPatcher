using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>API-Tokens verwalten (nur Admins, nur per Browser-Login, nicht per Token selbst).</summary>
public static class ApiTokenEndpoints
{
    public sealed record TokenDto(Guid Id, string Name, string Prefix, UserRole Role, string CreatedBy, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt);
    public sealed record CreateTokenRequest(string Name, UserRole Role, int? ExpiresInDays);
    public sealed record TokenCreatedDto(TokenDto Token, string Secret);

    public static void MapApiTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/api-tokens")
            .RequireAuthorization(UserAccounts.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                // Ein Token darf keine Tokens anlegen (Rechteausweitung ausschließen).
                context.HttpContext.User.HasClaim(c => c.Type == "aiopatcher:token") ? Results.Forbid() : await next(context));
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<List<TokenDto>> List(AppDbContext db, CancellationToken ct) =>
        await db.ApiTokens.OrderBy(t => t.Name)
            .Select(t => new TokenDto(t.Id, t.Name, t.Prefix, t.Role, t.CreatedBy, t.CreatedAt, t.ExpiresAt, t.LastUsedAt))
            .ToListAsync(ct);

    private static async Task<IResult> Create(CreateTokenRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        if (request.Role is not (UserRole.Viewer or UserRole.Operator))
        {
            errors["role"] = [L.T("Tokens können Betrachter oder Operator sein, nicht Admin.", "Tokens can be Viewer or Operator, not Admin.")];
        }

        if (request.ExpiresInDays is < 1 or > 3650)
        {
            errors["expiresInDays"] = [L.T("Gültigkeit: 1 bis 3650 Tage.", "Validity: 1 to 3650 days.")];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var secret = ApiTokenAuthenticationHandler.NewToken();
        var token = new ApiToken
        {
            Name = name,
            TokenHash = ApiTokenAuthenticationHandler.Hash(secret),
            Prefix = secret[..9],
            Role = request.Role,
            CreatedBy = user.Identity?.Name ?? "?",
            ExpiresAt = request.ExpiresInDays is { } days ? DateTime.UtcNow.AddDays(days) : null,
        };
        db.ApiTokens.Add(token);
        audit.Add("api-token.created", $"{name} ({token.Role})");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TokenCreatedDto(new TokenDto(token.Id, token.Name, token.Prefix, token.Role, token.CreatedBy, token.CreatedAt, token.ExpiresAt, null), secret));
    }

    private static async Task<IResult> Delete(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var token = await db.ApiTokens.FindAsync([id], ct);
        if (token is null)
        {
            return Results.NotFound();
        }

        db.ApiTokens.Remove(token);
        audit.Add("api-token.deleted", token.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
