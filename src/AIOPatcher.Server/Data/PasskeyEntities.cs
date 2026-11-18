using System.ComponentModel.DataAnnotations;

namespace AIOPatcher.Server.Data;

/// <summary>
/// Passkey (WebAuthn-Credential) eines lokalen Benutzers. Ersetzt beim Anmelden Passwort und Code; gilt nur für den
/// Namen (RP-ID), unter dem er angelegt wurde, z. B. patch.example.com, nicht für patch.lan oder eine IP.
/// </summary>
public sealed class UserPasskey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public UserAccount? User { get; set; }

    /// <summary>Credential-ID vom Authenticator (eindeutig).</summary>
    public required byte[] CredentialId { get; set; }

    /// <summary>Öffentlicher Schlüssel als COSE-Key.</summary>
    public required byte[] PublicKey { get; set; }

    public long SignCount { get; set; }

    /// <summary>Hostname, für den der Passkey gilt (RP-ID).</summary>
    [MaxLength(253)]
    public required string RpId { get; set; }

    [MaxLength(64)]
    public required string Name { get; set; }

    /// <summary>Transportwege laut Browser (usb, nfc, ble, internal, hybrid), kommagetrennt.</summary>
    [MaxLength(100)]
    public string? Transports { get; set; }

    /// <summary>In einem Passwort-Manager bzw. der Cloud gesichert (synchronisiert über Geräte).</summary>
    public bool BackedUp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
