using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AIOPatcher.Agent;

public static class CertificateHelper
{
    /// <summary>Normalisiert einen SHA-256-Fingerprint ("AB:CD:..." / "abcd...") auf Großbuchstaben ohne Trenner.</summary>
    public static string NormalizeFingerprint(string fingerprint) =>
        new string(fingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    public static string Sha256Fingerprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>
    /// PEM-Schlüssel sind unter Windows "ephemeral" und funktionieren nicht mit SslStream.
    /// Ein PKCS#12-Roundtrip macht das Zertifikat überall für TLS nutzbar.
    /// </summary>
    public static X509Certificate2 ToUsableForTls(X509Certificate2 certificate) =>
        OperatingSystem.IsWindows()
            ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null)
            : X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.EphemeralKeySet);

    /// <summary>
    /// Server-Prüfung gegen die gepinnte AIOPatcher-CA statt gegen den System-Truststore.
    /// Der Hostname muss trotzdem passen.
    /// </summary>
    public static bool ValidateServer(X509Certificate2 pinnedCa, X509Certificate? presented, SslPolicyErrors errors)
    {
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pinnedCa);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        if (!chain.Build(leaf))
        {
            return false;
        }

        var root = chain.ChainElements[^1].Certificate;
        return root.RawDataMemory.Span.SequenceEqual(pinnedCa.RawDataMemory.Span);
    }
}
