namespace AIOPatcher.Server.Data;

/// <summary>
/// Single Sign-on per OpenID Connect (Keycloak, Authentik, Entra ID, Google, Authelia …). Genau eine Zeile (Id 1).
/// Das Client-Secret liegt nur verschlüsselt (Data Protection) in der Datenbank.
/// </summary>
public sealed class SsoSettingsRecord
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }

    /// <summary>Text auf dem Anmelde-Knopf, z. B. "Keycloak".</summary>
    public string DisplayName { get; set; } = "SSO";

    /// <summary>Issuer-URL, z. B. https://sso.example.com/realms/homelab.</summary>
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ProtectedClientSecret { get; set; }
    public string Scopes { get; set; } = "openid profile email";
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>Claim mit den Gruppen, z. B. "groups" (Keycloak: Mapper "Group Membership") oder "realm_access.roles".</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>JSON-Liste von <see cref="AIOPatcher.Server.Services.Sso.SsoRoleMapping"/>.</summary>
    public string RoleMappingsJson { get; set; } = "[]";

    /// <summary>Rolle für angemeldete Benutzer ohne passende Gruppe. null = Anmeldung ablehnen.</summary>
    public UserRole? DefaultRole { get; set; }

    /// <summary>Unbekannte Benutzer beim ersten SSO-Login anlegen.</summary>
    public bool AutoCreateUsers { get; set; } = true;

    /// <summary>Bestehende lokale Benutzer mit gleichem Benutzernamen beim ersten SSO-Login verknüpfen.</summary>
    public bool LinkByUsername { get; set; } = true;

    /// <summary>SHA-256 des Server-Zertifikats des Anbieters (selbstsigniert, z. B. Keycloak im LAN). Leer = normale Prüfung.</summary>
    public string? CertificateFingerprint { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
