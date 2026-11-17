using System.ComponentModel.DataAnnotations;

namespace AIOPatcher.Server.Data;

/// <summary>
/// Öffentlicher Zugang (eine Zeile): Der Server selbst läuft intern, ein Reverse Proxy des Benutzers macht ihn unter
/// einer öffentlichen Adresse erreichbar. Agents nutzen sie als Ausweichweg, wenn die interne Adresse nicht geht.
/// </summary>
public sealed class PublicAccessSettingsRecord
{
    public int Id { get; set; } = 1;

    /// <summary>Öffentliche Adresse für Agents, z. B. https://agents.example.com (ohne Port = 443). null = nicht eingerichtet.</summary>
    [MaxLength(300)]
    public string? AgentUrl { get; set; }

    /// <summary>Optional: öffentliche Adresse des Dashboards (eigener Name, Proxy zeigt auf den Dashboard-Port).</summary>
    [MaxLength(300)]
    public string? DashboardUrl { get; set; }

    /// <summary>Gewählter Proxy im Wizard: nginx, caddy, npm, traefik, cloudflared, opnsense.</summary>
    [MaxLength(32)]
    public string? Proxy { get; set; }

    [MaxLength(200)]
    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
