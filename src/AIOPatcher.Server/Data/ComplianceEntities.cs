namespace AIOPatcher.Server.Data;

/// <summary>Letzter Compliance-Stand eines Hosts (vom Agent gemeldet). Json: vollständiger <see cref="AIOPatcher.Contracts.ComplianceReport"/>.</summary>
public sealed class HostCompliance
{
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public DateTime CollectedAt { get; set; }
    public required string Json { get; set; }

    // Zusammenfassung für die Übersicht (ohne JSON zu laden)
    public string FirewallProduct { get; set; } = "none";
    public bool FirewallEnabled { get; set; }
    public bool FirewallManageable { get; set; }
    public string? AppliedFirewallHash { get; set; }
    public int RemoteAccessIssues { get; set; }
    public int Users { get; set; }
    public int Admins { get; set; }

    /// <summary>root bzw. eingebauter Administrator ist aktiv und hat ein Passwort.</summary>
    public bool BuiltInAdminActive { get; set; }
}

/// <summary>Firewall-Regelsatz. Jede Änderung ist eine neue Version; ältere bleiben ausrollbar (Rollback).</summary>
public sealed class FirewallProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<FirewallProfileVersion> Versions { get; set; } = [];
}

public sealed class FirewallProfileVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public FirewallProfile? Profile { get; set; }
    public int Number { get; set; }

    /// <summary><see cref="AIOPatcher.Contracts.FirewallConfig"/> als JSON.</summary>
    public required string ConfigJson { get; set; }

    /// <summary><see cref="AIOPatcher.Contracts.FirewallRules.Hash"/>: so erkennt der Server, welche Version ein Host aktiv hat.</summary>
    public required string Hash { get; set; }

    public string? Note { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
