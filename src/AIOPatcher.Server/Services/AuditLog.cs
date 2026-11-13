using System.Security.Claims;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services;

public sealed class AuditLog(AppDbContext db, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Fügt einen Audit-Eintrag hinzu. Gespeichert wird mit dem nächsten SaveChanges des Aufrufers.</summary>
    public void Add(string action, string? details = null, Guid? siteId = null, Guid? hostId = null, string? actor = null)
    {
        var http = httpContextAccessor.HttpContext;
        actor ??= http?.User.FindFirstValue(ClaimTypes.Name) is { } name ? $"admin:{name}" : "anonymous";

        db.AuditEntries.Add(new AuditEntry
        {
            Actor = actor,
            Action = action,
            Details = details,
            SiteId = siteId,
            HostId = hostId,
            RemoteIp = http?.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
