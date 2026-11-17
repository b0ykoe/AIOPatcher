using System.Security.Cryptography;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Eigener Signaturschlüssel des Servers für Agent-Updates, wenn das Image keine Release-Signaturen mitbringt
/// (selbst gebautes Image ohne Release-Schlüssel). Liegt neben der CA und wird beim ersten Start erzeugt.
/// Agents lernen den öffentlichen Teil beim Check-in (über die gepinnte CA) und merken sich den ersten.
/// Achtung: Wer den Server übernimmt, hat auch diesen Schlüssel. Hosts, die das nicht wollen, setzen in
/// policy.json "trustServerUpdates": false; Agents mit eingebautem Release-Schlüssel nehmen ihn nie an.
/// </summary>
public sealed class UpdateSigningKey : IDisposable
{
    public const string KeyFile = "update-signing.key";

    private readonly ECDsa _key;

    private UpdateSigningKey(ECDsa key)
    {
        _key = key;
        PublicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public string PublicKeySpkiBase64 { get; }

    public static UpdateSigningKey LoadOrCreate(string directory, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFile);
        var key = ECDsa.Create();
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return new UpdateSigningKey(key);
        }

        key.Dispose();
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var temp = path + ".tmp";
        File.WriteAllText(temp, key.ExportPkcs8PrivateKeyPem() + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path);
        logger.LogInformation("Created update signing key for agent self-updates in {Directory}", directory);
        return new UpdateSigningKey(key);
    }

    /// <summary>Signatur über den SHA-256 (hex) eines Binaries, im selben Format wie der Release-Signer (DER, Base64).</summary>
    public string SignSha256Hex(string sha256Hex) =>
        Convert.ToBase64String(_key.SignHash(Convert.FromHexString(sha256Hex), DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => _key.Dispose();
}
