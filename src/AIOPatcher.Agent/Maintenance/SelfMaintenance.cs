using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Maintenance;

/// <summary>
/// Pflege des Agents selbst: Client-Zertifikat erneuern und signierte Updates installieren.
/// <para>
/// Vertrauen: Agents mit eingebautem Release-Schlüssel nehmen nur Release-Signaturen an; ein kompromittierter Server
/// kann so keinen fremden Code verteilen. Agents ohne Release-Schlüssel nehmen Updates mit dem Update-Schlüssel des
/// Servers an (den sie sich beim ersten Check-in merken), wenn policy.json das nicht verbietet.
/// </para>
/// <para>
/// Ablauf eines Updates: herunterladen, Prüfsumme und Signatur prüfen, die neue Version einen Selbsttest machen lassen
/// (startet sie, liest sie die Registrierung, erreicht sie den Server?), alte Version sichern, tauschen, neu starten.
/// Ein Wächter außerhalb des Agents (systemd-run bzw. PowerShell) stellt die alte Version wieder her, wenn sich die
/// neue nicht innerhalb von <see cref="UpdateState.ConfirmTimeout"/> beim Server meldet.
/// </para>
/// </summary>
public sealed class SelfMaintenance(AgentState state, ILogger<SelfMaintenance> logger)
{
    public const string LinuxServiceName = "aiopatcher-agent";
    public const string WindowsServiceName = "AIOPatcherAgent";

    private static readonly TimeSpan SelfTestTimeout = TimeSpan.FromSeconds(90);

    private bool _warnedKeyChange;
    private PendingSelfUpdate? _awaitingConfirmation;

    /// <summary>Öffentlicher Release-Schlüssel (SPKI, Base64), beim Build über -p:AgentReleaseKey=… eingebaut.</summary>
    public static string? ReleaseKey { get; } = typeof(SelfMaintenance).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "AIOPatcher.ReleaseKey")?.Value is { Length: > 0 } key ? key : null;

    public static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    private static string? Exe => Environment.ProcessPath;

    /// <summary>Sicherung der vorigen Version während eines Updates.</summary>
    public static string? BackupPath => Exe is { } exe ? exe + ".bak" : null;

    /// <summary>Download-Ziel. Unter Windows mit .exe am Ende, damit der Selbsttest es starten kann.</summary>
    public static string NewPath(string exe) =>
        OperatingSystem.IsWindows() ? Path.ChangeExtension(exe, ".new.exe") : exe + ".new";

    /// <summary>Beim Start: Reste aufräumen und ein laufendes Update fortsetzen (bestätigen oder Rollback melden).</summary>
    public void Startup()
    {
        if (Exe is { } exe)
        {
            UpdateState.Delete(NewPath(exe));
            UpdateState.Delete(exe + ".old"); // ältere Agent-Versionen
        }

        _awaitingConfirmation = UpdateState.Resume(state.Directory, AgentState.AgentVersion, BackupPath);
        if (_awaitingConfirmation is { } pending)
        {
            logger.LogInformation("Updated from {From} to {To}. Waiting for the first successful poll to confirm the update.", pending.FromVersion, pending.ToVersion);
        }
        else if (UpdateState.LoadResult(state.Directory) is { Outcome: AgentUpdateOutcome.RolledBack } rolledBack)
        {
            logger.LogWarning("Update to {To} was rolled back: {Message}", rolledBack.ToVersion, rolledBack.Message);
        }
    }

    /// <summary>Ergebnis des letzten Self-Updates für die Antwort auf system.describe (null = nichts zu melden).</summary>
    public AgentUpdateReport? ReportForDescribe() => UpdateState.LoadResult(state.Directory);

    /// <summary>Der Server hat die Antwort mit dem Bericht angenommen.</summary>
    public void ReportDelivered() => UpdateState.DeleteResult(state.Directory);

    /// <summary>
    /// Nach jeder erfolgreichen Abfrage: Update bestätigen (die neue Version erreicht den Server), Server-Schlüssel merken.
    /// Der Bericht darüber geht erst raus, wenn der Server danach fragt (system.describe).
    /// </summary>
    public void PollSucceeded(string? serverUpdateKey)
    {
        if (_awaitingConfirmation is { } pending)
        {
            UpdateState.Confirm(state.Directory, BackupPath);
            UpdateState.SaveResult(state.Directory, new AgentUpdateReport(pending.FromVersion, pending.ToVersion, AgentUpdateOutcome.Installed, null, DateTime.UtcNow));
            _awaitingConfirmation = null;
            logger.LogInformation("Update to {Version} confirmed.", pending.ToVersion);
        }

        RememberServerKey(serverUpdateKey);
        try
        {
            UpdateState.TouchLastCheckin(state.Directory);
        }
        catch (IOException)
        {
        }
    }

    private void RememberServerKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var known = UpdateState.LoadServerKey(state.Directory);
        if (known is null)
        {
            UpdateState.SaveServerKey(state.Directory, key.Trim());
        }
        else if (known != key.Trim() && !_warnedKeyChange)
        {
            _warnedKeyChange = true;
            logger.LogWarning("The server presents a different update key than before. Server-signed updates stay blocked until the agent is enrolled again.");
        }
    }

    /// <summary>agent.renew, Schritt 1: neuer Schlüssel und CSR für die Antwort. Der Schlüssel bleibt bis Schritt 2 im Speicher.</summary>
    public static (ECDsa Key, string CsrPem) CreateRenewal()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={System.Net.Dns.GetHostName()}", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        return (key, csr);
    }

    /// <summary>agent.renew, Schritt 2: Zertifikat aus der Rückgabe des Servers speichern, danach neu starten.</summary>
    public void CompleteRenewal(string clientCertificatePem, ECDsa key)
    {
        AgentState.Save(state.Directory, state.Config, state.CaCertificate.ExportCertificatePem(), clientCertificatePem, key);
        logger.LogInformation("Client certificate renewed. Restarting agent to use it.");
        Restart(watchdog: false);
    }

    /// <summary>Nach der Antwort auf agent.update: neu starten, mit Wächter für das Rollback.</summary>
    public void RestartAfterUpdate() => Restart(watchdog: true);

    /// <summary>
    /// Welcher Schlüssel prüft dieses Angebot? Null = Update nicht annehmen (mit Grund).
    /// Agents mit Release-Schlüssel bleiben streng, auch wenn der Server etwas anderes anbietet.
    /// </summary>
    public static (string? Key, string? Reason) ChooseKey(string? releaseKey, string signedBy, bool trustServerUpdates, string? serverKey)
    {
        if (releaseKey is not null)
        {
            return signedBy == UpdateSigners.Release
                ? (releaseKey, null)
                : (null, "this agent only accepts updates signed with the release key");
        }

        if (signedBy == UpdateSigners.Release)
        {
            return (null, "this agent build has no release key: update by re-running the install command");
        }

        if (signedBy != UpdateSigners.Server)
        {
            return (null, $"unknown signer '{signedBy}'");
        }

        if (!trustServerUpdates)
        {
            return (null, "policy.json: trustServerUpdates is false");
        }

        return serverKey is null ? (null, "the server's update key is not known yet") : (serverKey, null);
    }

    /// <summary>
    /// agent.update: prüfen, testen, tauschen. Startet NICHT neu (erst antworten, dann <see cref="RestartAfterUpdate"/>).
    /// Rückgabe: (installiert?, Begründung für die Antwort).
    /// </summary>
    public async Task<(bool Installed, string Message)> TryUpdateAsync(ServerClient client, AgentUpdateOffer offer, CancellationToken ct)
    {
        if (_awaitingConfirmation is not null)
        {
            return (false, "Previous update not confirmed yet.");
        }

        var policy = AgentPolicy.Load(state.Directory);
        if (!policy.AllowSelfUpdate)
        {
            return (false, "policy.json: allowSelfUpdate is false.");
        }

        var (key, reason) = ChooseKey(ReleaseKey, offer.SignedBy, policy.TrustServerUpdates, UpdateState.LoadServerKey(state.Directory));
        if (key is null)
        {
            logger.LogInformation("Agent {Version} available, not installing automatically: {Reason}.", offer.Version, reason);
            return (false, $"Not installed: {reason}.");
        }

        var exe = Exe ?? throw new InvalidOperationException("Unknown executable path.");
        var temp = NewPath(exe);
        try
        {
            logger.LogInformation("Downloading agent {Version} ({Signer}-signed) …", offer.Version, offer.SignedBy);
            await client.DownloadAsync(offer.DownloadPath, temp, ct);

            await using (var stream = File.OpenRead(temp))
            {
                var hash = await SHA256.HashDataAsync(stream, ct);
                if (!string.Equals(Convert.ToHexString(hash), offer.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SHA-256 of the download does not match.");
                }

                if (!VerifySignature(key, hash, offer.Signature))
                {
                    throw new InvalidDataException("Signature invalid: update rejected.");
                }
            }

            MakeExecutable(temp);
            var (ok, output) = await SelfTestAsync(temp, offer.Version, ct);
            if (!ok)
            {
                logger.LogWarning("New agent {Version} failed its self-test, keeping {Current}: {Output}", offer.Version, AgentState.AgentVersion, output);
                UpdateState.SaveResult(state.Directory, new AgentUpdateReport(AgentState.AgentVersion, offer.Version, AgentUpdateOutcome.Rejected,
                    $"Self-test failed: {output}", DateTime.UtcNow));
                UpdateState.Delete(temp);
                return (false, $"Self-test of {offer.Version} failed, keeping {AgentState.AgentVersion}.");
            }

            UpdateState.SavePending(state.Directory, new PendingSelfUpdate(AgentState.AgentVersion, offer.Version, DateTime.UtcNow));
            Replace(exe, temp, BackupPath!);
            logger.LogInformation("Agent updated to {Version}. Restarting after the answer; the previous version is restored if the new one does not reach the server within {Minutes} minutes.",
                offer.Version, UpdateState.ConfirmTimeout.TotalMinutes);
            return (true, $"Installed {offer.Version}, restarting. Rollback if it does not reach the server within {UpdateState.ConfirmTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Agent update failed: {Message}", ex.Message);
            UpdateState.Delete(temp);
            // Tausch halb geschafft (Windows: alte .exe schon umbenannt)? Dann zurück, sonst startet der Dienst nicht mehr.
            if (!File.Exists(exe) && BackupPath is { } backup && File.Exists(backup))
            {
                File.Move(backup, exe);
            }

            UpdateState.Delete(Path.Combine(state.Directory, UpdateState.PendingFile));
            return (false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Startet die heruntergeladene Version mit "selftest". Fängt ab: Binary startet nicht (falsche Architektur, fehlende
    /// Bibliotheken), stürzt beim Laden der Registrierung ab, erreicht den Server nicht, oder meldet eine andere Version
    /// als angeboten (sonst gäbe es eine Update-Schleife).
    /// </summary>
    private async Task<(bool Ok, string Output)> SelfTestAsync(string binary, string expectedVersion, CancellationToken ct)
    {
        var start = new ProcessStartInfo(binary)
        {
            ArgumentList = { "selftest", "--state-dir", state.Directory },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Self-test could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SelfTestTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (false, $"no answer within {SelfTestTimeout.TotalSeconds:0} s");
        }

        var output = ((await stdout) + (await stderr)).Trim();
        var shortOutput = output.Length > 600 ? output[^600..] : output;
        if (process.ExitCode != 0)
        {
            return (false, $"exit code {process.ExitCode}. {shortOutput}");
        }

        var reported = output.Split('\n').FirstOrDefault(l => l.StartsWith("version:", StringComparison.Ordinal))?["version:".Length..].Trim();
        return reported == expectedVersion
            ? (true, shortOutput)
            : (false, $"download reports version '{reported}', expected {expectedVersion}");
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

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Alte Version nach .bak, neue an ihre Stelle.</summary>
    private static void Replace(string exe, string temp, string backup)
    {
        UpdateState.Delete(backup);
        if (OperatingSystem.IsWindows())
        {
            // Laufende .exe lässt sich nicht überschreiben, aber umbenennen.
            File.Move(exe, backup);
            File.Move(temp, exe);
        }
        else
        {
            File.Copy(exe, backup);
            File.Move(temp, exe, overwrite: true); // atomar im selben Verzeichnis
        }
    }

    /// <summary>
    /// Linux: Prozess beenden, systemd startet neu (Restart=always).
    /// Windows: Neustart des Dienstes über einen abgekoppelten PowerShell-Prozess.
    /// watchdog: zusätzlich nach <see cref="UpdateState.ConfirmTimeout"/> prüfen, ob die neue Version bestätigt ist,
    /// sonst die Sicherung zurückspielen. Der Wächter läuft außerhalb des Agents, damit er auch hilft, wenn die neue
    /// Version gar nicht erst startet.
    /// </summary>
    private void Restart(bool watchdog)
    {
        var pending = Path.Combine(state.Directory, UpdateState.PendingFile);
        if (OperatingSystem.IsWindows())
        {
            var script = $"Start-Sleep -Seconds 3; Restart-Service -Name {WindowsServiceName} -Force";
            if (watchdog && Exe is { } exe && BackupPath is { } backup)
            {
                script = "$ErrorActionPreference = 'SilentlyContinue'; " + script + "; " + WindowsWatchdog(exe, backup, pending);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return;
        }

        if (watchdog && Exe is { } linuxExe && BackupPath is { } linuxBackup)
        {
            StartLinuxWatchdog(linuxExe, linuxBackup, pending);
        }

        Environment.Exit(75);
    }

    public static string WindowsWatchdog(string exe, string backup, string pending)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $"Start-Sleep -Seconds {(int)UpdateState.ConfirmTimeout.TotalSeconds}; " +
               $"if (Test-Path -LiteralPath {Q(pending)}) {{ " +
               "& schtasks.exe /Change /TN AIOPatcherAgentWatchdog /DISABLE | Out-Null; " +
               $"Stop-Service -Name {WindowsServiceName} -Force; " +
               $"Get-Process -Name aiopatcher-agent | Where-Object {{ $_.Path -eq {Q(exe)} }} | Stop-Process -Force; " +
               "Start-Sleep -Seconds 2; " +
               $"Copy-Item -LiteralPath {Q(backup)} -Destination {Q(exe)} -Force; " +
               $"Start-Service -Name {WindowsServiceName} }}";
    }

    public static string LinuxWatchdog(string exe, string backup, string pending)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        return $"test -f {Q(pending)} || exit 0; " +
               $"systemctl stop {LinuxServiceName}; " +
               $"cp -f {Q(backup)} {Q(exe + ".rollback")} && mv -f {Q(exe + ".rollback")} {Q(exe)}; " +
               $"systemctl start {LinuxServiceName}";
    }

    /// <summary>Transienter systemd-Timer, unabhängig vom Agent-Prozess. Nur, wenn der Agent als Dienst läuft.</summary>
    private void StartLinuxWatchdog(string exe, string backup, string pending)
    {
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is null)
        {
            logger.LogWarning("Not running under systemd: no automatic rollback if the new version fails.");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("systemd-run")
            {
                ArgumentList =
                {
                    $"--unit=aiopatcher-agent-update-guard-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    "--collect",
                    $"--on-active={(int)UpdateState.ConfirmTimeout.TotalSeconds}",
                    "--timer-property=AccuracySec=5s",
                    "/bin/sh", "-c", LinuxWatchdog(exe, backup, pending),
                },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            process?.WaitForExit(10_000);
            if (process is { HasExited: true, ExitCode: not 0 })
            {
                logger.LogWarning("systemd-run failed ({Code}): {Error}. No automatic rollback.", process.ExitCode, process.StandardError.ReadToEnd().Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("systemd-run not available ({Message}). No automatic rollback.", ex.Message);
        }
    }
}
