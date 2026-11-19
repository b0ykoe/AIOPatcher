using System.Security.Claims;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Einstellungen → KI: Anbieter, Modell, Schlüssel, Sprache der Zusammenfassungen (nur Admins).</summary>
public static class AiEndpoints
{
    /// <param name="HasApiKey">Ein Schlüssel ist gespeichert (der Schlüssel selbst verlässt den Server nie).</param>
    /// <param name="Usage">Verbrauch der letzten Tage laut Anbieter.</param>
    public sealed record AiSettingsDto(bool Enabled, string Provider, string Model, string DefaultModel, string? BaseUrl, bool HasApiKey, string Language,
        string? UpdatedBy, DateTime? UpdatedAt, AiUsageDto Usage);

    public sealed record AiUsageDto(int Days, int Requests, long InputTokens, long OutputTokens);

    private const int UsageDays = 30;

    /// <param name="ApiKey">null = unverändert lassen, "" = löschen.</param>
    public sealed record AiSettingsRequest(bool Enabled, string Provider, string? Model, string? BaseUrl, string? ApiKey, string Language);

    public sealed record AiTestResult(bool Ok, string? Text, string? Error);

    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/settings/ai").RequireAuthorization(UserAccounts.AdminPolicy);
        admin.MapGet("", (UpdateSummaries summaries, AppDbContext db, CancellationToken ct) => DtoAsync(summaries.Settings, summaries, db, ct));
        admin.MapPut("", SaveAsync);
        admin.MapPost("/test", TestAsync);
    }

    private static async Task<AiSettingsDto> DtoAsync(AiSettingsRecord s, UpdateSummaries summaries, AppDbContext db, CancellationToken ct)
    {
        var (requests, input, output) = await UpdateSummaries.UsageAsync(db, DateTime.UtcNow, UsageDays, ct);
        return new(s.Enabled, s.Provider, s.Model, AiClient.DefaultModel(s.Provider), s.BaseUrl,
            !string.IsNullOrEmpty(s.ProtectedApiKey) && summaries.ApiKeyOf(s) is not null, s.Language, s.UpdatedBy, s.UpdatedAt,
            new AiUsageDto(UsageDays, requests, input, output));
    }

    private static Dictionary<string, string[]> Validate(AiSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AiClient.Providers.Contains(request.Provider))
        {
            errors["provider"] = [L.T("Unbekannter Anbieter.", "Unknown provider.")];
        }

        if (request.Language is not ("de" or "en"))
        {
            errors["language"] = [L.T("Sprache: de oder en.", "Language: de or en.")];
        }

        if (request.Model is { Length: > 100 })
        {
            errors["model"] = [L.T("Modellname zu lang.", "Model name too long.")];
        }

        if (request.Provider == AiClient.Compatible
            && !(Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && request.BaseUrl!.Length <= 300))
        {
            errors["baseUrl"] = [L.T("Adresse der Schnittstelle, z. B. http://ollama:11434/v1", "Address of the API, e.g. http://ollama:11434/v1")];
        }

        if (request.Provider == AiClient.Compatible && string.IsNullOrWhiteSpace(request.Model))
        {
            errors["model"] = [L.T("Bei lokalen Modellen den Modellnamen eintragen, z. B. llama3.1.", "For local models, enter the model name, e.g. llama3.1.")];
        }

        if (request.ApiKey is { Length: > 500 })
        {
            errors["apiKey"] = [L.T("Schlüssel zu lang.", "Key too long.")];
        }

        return errors;
    }

    private static AiSettingsRecord Apply(AiSettingsRecord row, AiSettingsRequest request, UpdateSummaries summaries)
    {
        row.Enabled = request.Enabled;
        row.Provider = request.Provider;
        row.Model = request.Model?.Trim() ?? "";
        row.BaseUrl = request.Provider == AiClient.Compatible ? request.BaseUrl?.Trim() : null;
        row.Language = request.Language;
        if (request.ApiKey is { } key)
        {
            row.ProtectedApiKey = key.Trim().Length == 0 ? null : summaries.Protect(key.Trim());
        }

        return row;
    }

    private static async Task<IResult> SaveAsync(AiSettingsRequest request, ClaimsPrincipal user, AppDbContext db, AuditLog audit, UpdateSummaries summaries,
        CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors);
        }

        var row = await db.AiSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            db.AiSettings.Add(row = new AiSettingsRecord());
        }

        var wasEnabled = row.Enabled;
        Apply(row, request, summaries);
        row.UpdatedBy = user.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;
        audit.Add("ai.settings", $"{(row.Enabled ? "on" : "off")}: {AiClient.Label(row)}{(row.BaseUrl is null ? "" : $" ({row.BaseUrl})")}, {row.Language}" +
            (request.ApiKey is null ? "" : request.ApiKey.Trim().Length == 0 ? ", key removed" : ", key changed"));
        await db.SaveChangesAsync(ct);
        summaries.Use(row);

        // Eingeschaltet: alle Hosts mit anstehenden Updates einmal zusammenfassen
        if (row.Enabled && !wasEnabled)
        {
            foreach (var hostId in await db.PendingUpdates.Select(u => u.HostId).Distinct().ToListAsync(ct))
            {
                summaries.Enqueue(hostId);
            }
        }

        return Results.Ok(await DtoAsync(row, summaries, db, ct));
    }

    /// <summary>Probeanfrage mit den Werten aus dem Formular (noch nicht gespeichert); ohne neuen Schlüssel gilt der gespeicherte.</summary>
    private static async Task<IResult> TestAsync(AiSettingsRequest request, UpdateSummaries summaries, AppDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors);
        }

        var probe = Apply(new AiSettingsRecord { ProtectedApiKey = summaries.Settings.ProtectedApiKey }, request, summaries);
        try
        {
            var text = await summaries.TestAsync(db, probe, summaries.ApiKeyOf(probe), DateTime.UtcNow, ct);
            return Results.Ok(new AiTestResult(true, text, null));
        }
        catch (AiException ex)
        {
            return Results.Ok(new AiTestResult(false, null, ex.Message));
        }
    }
}
