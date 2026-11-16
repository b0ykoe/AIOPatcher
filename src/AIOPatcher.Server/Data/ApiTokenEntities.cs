namespace AIOPatcher.Server.Data;

/// <summary>
/// API-Zugang für Skripte und andere Tools (z. B. Home Assistant, Grafana, eigene Automatisierung).
/// Header: <c>Authorization: Bearer aiop_…</c>. Höchstens Operator-Rechte, nie Admin.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>SHA-256 des Tokens (Base64). Der Klartext wird nur beim Anlegen einmal angezeigt.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Die ersten Zeichen zum Wiedererkennen, z. B. "aiop_3f9a".</summary>
    public required string Prefix { get; set; }

    public UserRole Role { get; set; } = UserRole.Viewer;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
