using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AIOPatcher.Contracts;

/// <summary>Öffentliche SSH-Schlüssel lesen und wie "ssh-keygen -lf" kennzeichnen (Agent und Server rechnen gleich).</summary>
public static partial class SshPublicKeys
{
    public static readonly IReadOnlyList<string> Types =
        ["ssh-ed25519", "ssh-rsa", "ssh-dss", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com"];

    /// <summary>Eine Zeile (authorized_keys, auch mit Optionen davor, oder ein öffentlicher Schlüssel) → Typ, Fingerabdruck, Kommentar.</summary>
    public static SshKeyInfo? KeyOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (Types.Contains(tokens[i]) && Fingerprint(tokens[i + 1]) is { } fingerprint)
            {
                var comment = i + 2 < tokens.Length ? string.Join(' ', tokens[(i + 2)..]) : "";
                return new SshKeyInfo(fingerprint, tokens[i], comment.Length > 0 ? comment[..Math.Min(comment.Length, 200)] : null);
            }
        }

        return null;
    }

    /// <summary>"SHA256:" + Base64 (ohne "=") des SHA-256 über den dekodierten Schlüssel; null, wenn kein Base64.</summary>
    public static string? Fingerprint(string base64)
    {
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^SHA256:[A-Za-z0-9+/]{43}$")]
    private static partial Regex FingerprintPattern();

    public static bool IsFingerprint(string? value) => value is not null && FingerprintPattern().IsMatch(value.Trim());

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) [A-Za-z0-9+/]+={0,3}( [^\r\n]{0,200})?$")]
    private static partial Regex PublicKeyPattern();

    /// <summary>Genau eine Zeile "typ base64 [kommentar]" ohne Optionen, mit gültigem Base64.</summary>
    public static bool IsValid(string? key) => key is not null && PublicKeyPattern().IsMatch(key.Trim()) && KeyOf(key) is not null;
}
