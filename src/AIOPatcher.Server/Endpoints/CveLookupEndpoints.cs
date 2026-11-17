using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Security;

namespace AIOPatcher.Server.Endpoints;

/// <summary>CVE-Suche: einzelne CVE nachschlagen oder nach Stichwort suchen (eigene Daten, KEV-Katalog, NVD).</summary>
public static class CveLookupEndpoints
{
    public static void MapCveLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cves").RequireAuthorization();
        group.MapGet("/search", SearchAsync);
        group.MapGet("/{id}", LookupAsync);
    }

    private static async Task<IResult> LookupAsync(string id, AppDbContext db, CveLookupService lookup, CancellationToken ct)
    {
        var normalized = CveLookupService.NormalizeId(id);
        return normalized is null
            ? Results.BadRequest(new { error = "Keine gültige CVE-Nummer (Format CVE-2024-3094)." })
            : Results.Ok(await lookup.LookupAsync(normalized, db, ct));
    }

    private static async Task<IResult> SearchAsync(string? q, AppDbContext db, CveLookupService lookup, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q)
            ? Results.BadRequest(new { error = "Suchbegriff fehlt." })
            : Results.Ok(await lookup.SearchAsync(q, db, ct));
}
