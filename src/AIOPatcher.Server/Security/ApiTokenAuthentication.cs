using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIOPatcher.Server.Security;

/// <summary>Bearer-Token für die Dashboard-API ("aiop_…"). Cookie-Login bleibt der Standard für den Browser.</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db,
    AIOPatcher.Server.Services.FeatureFlags features) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string TokenPrefix = "aiop_";

    /// <summary>Wählt das Schema: Bearer-Token mit Präfix → ApiToken, sonst Cookie.</summary>
    public const string SelectorScheme = "CookieOrToken";

    public static bool HasToken(HttpContext http) =>
        http.Request.Headers.Authorization.ToString().StartsWith("Bearer " + TokenPrefix, StringComparison.Ordinal);

    public static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    public static string NewToken() => TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasToken(Context))
        {
            return AuthenticateResult.NoResult();
        }

        if (!features.IsOn(AIOPatcher.Server.Services.Feature.ApiTokens))
        {
            return AuthenticateResult.Fail("API tokens are disabled");
        }

        var token = Context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var hash = Hash(token);
        var now = DateTime.UtcNow;
        var entity = await db.ApiTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);
        if (entity is null || entity.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Invalid or expired API token");
        }

        // Nur grob nachführen: höchstens einmal pro Minute schreiben.
        if (entity.LastUsedAt is null || now - entity.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            entity.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, entity.Id.ToString()),
                new Claim(ClaimTypes.Name, $"api:{entity.Name}"),
                new Claim(ClaimTypes.Role, entity.Role.ToString()),
                new Claim("aiopatcher:token", "1"),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
