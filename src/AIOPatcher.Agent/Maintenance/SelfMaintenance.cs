using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Maintenance;

/// <summary>
/// Pflege des Agents selbst: Client-Zertifikat erneuern und signierte Updates installieren.
/// Updates werden nur angenommen, wenn die Signatur zum im Binary eingebauten Release-Schlüssel passt;
/// ein kompromittierter Server kann so keinen fremden Code verteilen.
/// </summary>
public sealed class SelfMaintenance(AgentState state, ILogger<SelfMaintenance> logger)
{
    private DateTime _nextRenewAttempt = DateTime.MinValue;
    private DateTime _nextUpdateAttempt = DateTime.MinValue;
    private bool _warnedNoKey;

    /// <summary>Öffentlicher Release-Schlüssel (SPKI, Base64), beim Build über -p:AgentReleaseKey=… eingebaut.</summary>
    public static string? ReleaseKey { get; } = typeof(SelfMaintenance).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "AIOPatcher.ReleaseKey")?.Value is { Length: > 0 } key ? key : null;

    public static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>true = Agent wird neu gestartet (Aufrufer soll nichts mehr tun).</summary>
    public async Task<bool> RenewCertificateAsync(ServerClient client, CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextRenewAttempt)
        {
            return false;
        }

        _nextRenewAttempt = DateTime.UtcNow.AddHours(12);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                $"CN={System.Net.Dns.GetHostName()}", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
            var response = await client.RenewAsync(new RenewRequest(csr), ct);

            AgentState.Save(state.Directory, state.Config, state.CaCertificate.ExportCertificatePem(), response.ClientCertificatePem, key);
            logger.LogInformation("Client certificate renewed. Restarting agent to use it.");
            Restart();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Certificate renewal failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>true = Update installiert, Agent startet neu.</summary>
    public async Task<bool> TryUpdateAsync(ServerClient client, AgentUpdateOffer offer, CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextUpdateAttempt)
        {
            return false;
        }

        if (ReleaseKey is null)
        {
            if (!_warnedNoKey)
            {
                logger.LogInformation("Agent {Version} available, but this build has no release key: update manually by re-running the install command.", offer.Version);
                _warnedNoKey = true;
            }

            return false;
        }

        if (!AgentPolicy.Load(state.Directory).AllowSelfUpdate)
        {
            return false;
        }

        _nextUpdateAttempt = DateTime.UtcNow.AddHours(6);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var temp = exe + ".new";
        try
        {
            logger.LogInformation("Downloading agent {Version} …", offer.Version);
            await client.DownloadAsync(offer.DownloadPath, temp, ct);

            await using (var stream = File.OpenRead(temp))
            {
                var hash = await SHA256.HashDataAsync(stream, ct);
                if (!string.Equals(Convert.ToHexString(hash), offer.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SHA-256 of the download does not match.");
                }

                if (!VerifySignature(ReleaseKey, hash, offer.Signature))
                {
                    throw new InvalidDataException("Signature invalid: update rejected.");
                }
            }

            Replace(exe, temp);
            logger.LogInformation("Agent updated to {Version}. Restarting.", offer.Version);
            Restart();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Agent update failed: {Message}", ex.Message);
            TryDelete(temp);
            return false;
        }
    }

    public static bool VerifySignature(string publicKeySpkiBase64, byte[] sha256, string signatureBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return ecdsa.VerifyHash(sha256, Convert.FromBase64String(signatureBase64), DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Replace(string exe, string temp)
    {
        if (OperatingSystem.IsWindows())
        {
            // Laufende .exe lässt sich nicht überschreiben, aber umbenennen.
            var old = exe + ".old";
            TryDelete(old);
            File.Move(exe, old);
            File.Move(temp, exe);
        }
        else
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temp, exe, overwrite: true); // atomar im selben Verzeichnis
        }
    }

    /// <summary>Reste eines früheren Updates entfernen (Windows: alte .exe).</summary>
    public static void CleanupAfterUpdate()
    {
        if (Environment.ProcessPath is { } exe)
        {
            TryDelete(exe + ".old");
            TryDelete(exe + ".new");
        }
    }

    /// <summary>
    /// Linux: Prozess beenden, systemd startet neu (Restart=always).
    /// Windows: Neustart des Dienstes über einen abgekoppelten PowerShell-Prozess.
    /// </summary>
    private static void Restart()
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 3; Restart-Service -Name AIOPatcherAgent -Force" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return;
        }

        Environment.Exit(75);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
