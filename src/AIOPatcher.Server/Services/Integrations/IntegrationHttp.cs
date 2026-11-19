using System.Net.Security;
using System.Security.Cryptography;
using AIOPatcher.Server.Data;
using Microsoft.AspNetCore.DataProtection;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>Fehler einer Integration mit verständlicher Meldung (wird im Dashboard angezeigt).</summary>
public sealed class IntegrationException(string message, string? presentedFingerprint = null) : Exception(message)
{
    /// <summary>Fingerprint des Zertifikats, das der Server gezeigt hat (zum Anpinnen bei selbstsignierten Zertifikaten).</summary>
    public string? PresentedFingerprint { get; } = presentedFingerprint;
}

/// <summary>
/// HTTP-Zugriff für Integrationen: Secrets entschlüsseln, TLS prüfen (normal oder per angepinntem Fingerprint).
/// Proxmox und OPNsense laufen fast immer mit selbstsigniertem Zertifikat.
/// </summary>
public sealed class IntegrationHttp(IDataProtectionProvider dataProtection)
{
    private const string ProtectorPurpose = "AIOPatcher.Integration.Secret";

    public string Protect(string secret) => dataProtection.CreateProtector(ProtectorPurpose).Protect(secret);

    public string Unprotect(Integration integration) => dataProtection.CreateProtector(ProtectorPurpose).Unprotect(integration.ProtectedSecret);

    /// <summary>
    /// Neuer HttpClient pro Aufruf: Die Prüfung hängt am Fingerprint der jeweiligen Integration.
    /// Kurzlebig und selten genutzt, daher kein Pooling nötig.
    /// </summary>
    public HttpClient CreateClient(Integration integration, Uri baseAddress, out Func<string?> presentedFingerprint) =>
        new(CreateHandler(integration.TlsFingerprintSha256, out presentedFingerprint)) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Handler mit normaler TLS-Prüfung bzw. angepinntem Fingerprint (SHA-256 des Zertifikats).</summary>
    public static SocketsHttpHandler CreateHandler(string? fingerprint, out Func<string?> presentedFingerprint)
    {
        string? seen = null;
        var pinned = fingerprint?.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    seen = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                    return string.IsNullOrEmpty(pinned)
                        ? errors == SslPolicyErrors.None
                        : string.Equals(seen, pinned, StringComparison.Ordinal);
                },
            },
        };

        presentedFingerprint = () => seen;
        return handler;
    }

    /// <summary>Einheitliche Fehlermeldung, inklusive Hinweis auf das Zertifikat.</summary>
    public static IntegrationException Wrap(Exception ex, Func<string?> presentedFingerprint, Integration integration)
    {
        if (ex is IntegrationException ie)
        {
            return ie;
        }

        var fp = presentedFingerprint();
        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } && fp is not null)
        {
            return new IntegrationException(
                string.IsNullOrEmpty(integration.TlsFingerprintSha256)
                    ? L.T($"Zertifikat nicht vertrauenswürdig. Fingerprint zum Anpinnen: {fp}", $"Certificate not trusted. Fingerprint to pin: {fp}")
                    : L.T($"Zertifikat hat sich geändert. Neuer Fingerprint: {fp}", $"Certificate changed. New fingerprint: {fp}"),
                fp);
        }

        return ex switch
        {
            TaskCanceledException => new IntegrationException(L.T("Zeitüberschreitung.", "Timed out.")),
            HttpRequestException http when http.StatusCode is { } code => new IntegrationException(
                code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? L.T($"Zugriff verweigert ({(int)code}). Token und Berechtigungen prüfen.", $"Access denied ({(int)code}). Check token and permissions.")
                    : $"HTTP {(int)code}"),
            HttpRequestException http => new IntegrationException(http.Message),
            _ => new IntegrationException(ex.Message),
        };
    }

    public static string? NormalizeFingerprint(string? fingerprint)
    {
        var fp = fingerprint?.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(fp) ? null : fp;
    }

    public static bool IsValidFingerprint(string? fp) => fp is null || (fp.Length == 64 && fp.All(Uri.IsHexDigit));

}
