using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AIOPatcher.Contracts;

/// <summary>
/// Öffentlicher Weg zwischen Agent und Server über einen Reverse Proxy, der TLS selbst beendet (nginx, Caddy,
/// Nginx Proxy Manager, Traefik, Cloudflare). Ein Client-Zertifikat kommt dort nicht durch, darum:
/// <list type="bullet">
/// <item>Der Agent signiert jede Anfrage mit dem Schlüssel seines Client-Zertifikats (Methode, Pfad, Zeit, Nonce,
/// SHA-256 des Inhalts) und schickt das Zertifikat mit. Der Server prüft, dass es von seiner CA stammt.</item>
/// <item>Der Server signiert jede Antwort mit dem Schlüssel seines Server-Zertifikats, gebunden an die Nonce der
/// Anfrage. Der Agent prüft gegen die gepinnte CA: Wer den Proxy kontrolliert, kann mitlesen, aber keine Aufträge
/// einschleusen, verändern oder alte Antworten wiederholen.</item>
/// <item>Geheimnisse (z. B. ein neues Benutzer-Passwort) verschlüsselt der Server zusätzlich für den Schlüssel des
/// Agents (<see cref="Seal"/>), damit sie auch der Proxy nicht im Klartext sieht.</item>
/// </list>
/// </summary>
public static class PublicChannel
{
    public const string CertHeader = "X-AIOP-Cert";
    public const string TimeHeader = "X-AIOP-Time";
    public const string NonceHeader = "X-AIOP-Nonce";
    public const string SignatureHeader = "X-AIOP-Signature";
    public const string ServerCertHeader = "X-AIOP-Server-Cert";
    public const string ResponseSignatureHeader = "X-AIOP-Response-Signature";

    /// <summary>Prüfadresse für den Wizard: erreichbar ohne Anmeldung, Antwort vom Server signiert.</summary>
    public const string CheckPath = "/agent/public-check";

    /// <summary>Erlaubte Abweichung der Uhren zwischen Agent und Server.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Kennzeichnet ein für den Agent verschlüsseltes Geheimnis.</summary>
    public const string SealedPrefix = "aiop-box1:";

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static byte[] RequestToSign(string method, string pathAndQuery, long unixTime, string nonce, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "AIOP1-REQ", method.ToUpperInvariant(), pathAndQuery,
            unixTime.ToString(CultureInfo.InvariantCulture), nonce, Convert.ToHexString(SHA256.HashData(body))));

    public static byte[] ResponseToSign(string nonce, int status, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "AIOP1-RES", nonce, status.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(body))));

    public static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public static bool Verify(ECDsa key, byte[] data, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        try
        {
            return key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Liegt der Zeitstempel nah genug an der eigenen Uhr?</summary>
    public static bool IsFresh(long unixTime, DateTimeOffset now) =>
        Math.Abs(now.ToUnixTimeSeconds() - unixTime) <= MaxClockSkew.TotalSeconds;

    /// <summary>
    /// Geheimnis für den Inhaber eines EC-Schlüssels verschlüsseln: ECDH mit Einmal-Schlüssel, HKDF-SHA256, AES-256-GCM.
    /// Ergebnis: <see cref="SealedPrefix"/> + Base64(Länge des Einmal-Schlüssels, Einmal-Schlüssel, Nonce, Tag, Daten).
    /// </summary>
    public static string Seal(string plaintext, ECDsa recipient)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create(recipient.ExportParameters(includePrivateParameters: false));
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(peer.PublicKey), ephemeralPublic);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        var box = new byte[2 + ephemeralPublic.Length + nonce.Length + tag.Length + cipher.Length];
        box[0] = (byte)(ephemeralPublic.Length >> 8);
        box[1] = (byte)ephemeralPublic.Length;
        var at = 2;
        foreach (var part in new[] { ephemeralPublic, nonce, tag, cipher })
        {
            part.CopyTo(box, at);
            at += part.Length;
        }

        return SealedPrefix + Convert.ToBase64String(box);
    }

    /// <summary>Gegenstück zu <see cref="Seal"/>; Werte ohne Präfix kommen unverändert zurück (interner Weg).</summary>
    public static string? Open(string? value, ECDsa privateKey)
    {
        if (value is null || !value.StartsWith(SealedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var box = Convert.FromBase64String(value[SealedPrefix.Length..]);
        var length = (box[0] << 8) | box[1];
        if (box.Length < 2 + length + 12 + 16)
        {
            throw new CryptographicException("Sealed secret is truncated.");
        }

        var ephemeralPublic = box.AsSpan(2, length).ToArray();
        var nonce = box.AsSpan(2 + length, 12);
        var tag = box.AsSpan(2 + length + 12, 16);
        var cipher = box.AsSpan(2 + length + 28);

        using var own = ECDiffieHellman.Create(privateKey.ExportParameters(includePrivateParameters: true));
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
        var key = DeriveKey(own.DeriveRawSecretAgreement(ephemeral.PublicKey), ephemeralPublic);

        var data = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Decrypt(nonce, cipher, tag, data);
        }

        return Encoding.UTF8.GetString(data);
    }

    private static byte[] DeriveKey(byte[] shared, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.UTF8.GetBytes("AIOP1-SECRET"));
}
