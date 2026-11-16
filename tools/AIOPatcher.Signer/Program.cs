// Signiert Agent-Binaries für das Self-Update. Der private Schlüssel bleibt beim Release-Bau (CI-Secret oder
// offline), der öffentliche wird beim Build in den Agent eingebaut (-p:AgentReleaseKey=…).
//
//   aiopatcher-signer keygen <verzeichnis>          → release-signing.key (privat!) + release-signing.pub
//   aiopatcher-signer sign <binary> <schlüsseldatei> → <binary>.sig
//   aiopatcher-signer verify <binary> <öffentlicher Schlüssel als Base64>
using System.Security.Cryptography;

switch (args)
{
    case ["keygen", var directory]:
    {
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePath = Path.Combine(directory, "release-signing.key");
        File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem() + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(directory, "release-signing.pub"), publicKey + "\n");
        Console.WriteLine($"Private key: {privatePath} (keep secret, e.g. as CI secret AGENT_SIGNING_KEY)");
        Console.WriteLine($"Public key (build with -p:AgentReleaseKey=…):\n{publicKey}");
        return 0;
    }

    case ["sign", var binary, var keyFile]:
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyFile));
        var hash = SHA256.HashData(File.ReadAllBytes(binary));
        var signature = Convert.ToBase64String(key.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence));
        File.WriteAllText(binary + ".sig", signature + "\n");
        Console.WriteLine($"{binary}.sig");
        return 0;
    }

    case ["verify", var binary, var publicKeyBase64]:
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var hash = SHA256.HashData(File.ReadAllBytes(binary));
        var signature = Convert.FromBase64String(File.ReadAllText(binary + ".sig").Trim());
        var ok = key.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
        Console.WriteLine(ok ? "OK" : "INVALID");
        return ok ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("Usage: aiopatcher-signer keygen <dir> | sign <binary> <key.pem> | verify <binary> <publicKeyBase64>");
        return 2;
}
