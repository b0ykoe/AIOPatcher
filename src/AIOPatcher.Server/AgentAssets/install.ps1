<#
AIOPatcher-Agent: Installation, Update und Deinstallation fuer Windows
(Windows Server 2019/2022/2025, Windows 10/11). Laeuft mit Windows PowerShell 5.1.

Erstinstallation (Befehl kommt fertig aus dem Dashboard, "Host hinzufuegen"):
  install.ps1 -Server https://patch.example.com:8443 -Token <token> -Fingerprint <sha256>
Update (Agent bleibt registriert):
  install.ps1 -Server https://patch.example.com:8443 -Fingerprint <sha256>
Deinstallation:
  install.ps1 -Uninstall [-Purge]

Hinweis: Diese Datei enthaelt bewusst keine Umlaute (PowerShell 5.1 liest Skripte ohne BOM als ANSI).
#>
[CmdletBinding()]
param(
    [string]$Server,
    [string]$Fingerprint,
    [string]$Token,
    [switch]$Force,
    [switch]$Uninstall,
    [switch]$Purge,
    [string]$DownloadOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ServiceName = 'AIOPatcherAgent'
$InstallDir = Join-Path $env:ProgramFiles 'AIOPatcher'
$Exe = Join-Path $InstallDir 'aiopatcher-agent.exe'
$StateDir = Join-Path $env:ProgramData 'AIOPatcher\Agent'

function Info([string]$Message) { Write-Host "==> $Message" }

# HTTPS-Abruf mit eigener Zertifikatspruefung NUR fuer diesen Request (nicht global),
# ohne Keep-Alive und in eigener Verbindungsgruppe, damit keine Verbindung wiederverwendet wird.
function Invoke-Fetch([string]$Url, [scriptblock]$Validator, [string]$Group, [string]$OutFile) {
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.KeepAlive = $false
    $request.ConnectionGroupName = $Group
    $request.Timeout = 600000
    $request.ReadWriteTimeout = 600000
    $request.ServerCertificateValidationCallback = $Validator
    $response = $request.GetResponse()
    try {
        $stream = $response.GetResponseStream()
        if ($OutFile) {
            $file = [IO.File]::Create($OutFile)
            try { $stream.CopyTo($file) } finally { $file.Dispose() }
            return $null
        }
        return (New-Object IO.StreamReader($stream)).ReadToEnd()
    } finally {
        $response.Dispose()
    }
}
function Fail([string]$Message) { Write-Host "Fehler: $Message" -ForegroundColor Red; exit 1 }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $DownloadOnly -and -not $isAdmin) { Fail 'Bitte PowerShell als Administrator starten.' }

# --- Deinstallation ------------------------------------------------------------------------------
if ($Uninstall) {
    Info 'Entferne AIOPatcher-Agent ...'
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
        & sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if ($Purge) {
        if (Test-Path $StateDir) { Remove-Item $StateDir -Recurse -Force }
        Info 'Registrierung geloescht. Den Host im Dashboard ebenfalls loeschen.'
    } else {
        Info "Registrierung bleibt in $StateDir (mit -Purge entfernen)."
    }
    exit 0
}

# --- Pruefungen ----------------------------------------------------------------------------------
if (-not $Server) { Fail '-Server fehlt.' }
if (-not $Fingerprint) { Fail '-Fingerprint fehlt.' }
$Server = $Server.TrimEnd('/')
if (-not $Server.StartsWith('https://')) { Fail '-Server muss mit https:// beginnen.' }
$Fingerprint = ($Fingerprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if (-not [Environment]::Is64BitOperatingSystem) { Fail 'Nur 64-Bit-Windows wird unterstuetzt.' }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('aiopatcher-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    # --- 1. CA holen und gegen den Fingerprint pruefen (Pinning) ----------------------------------
    # Nur dieser eine Abruf laeuft ohne Zertifikatspruefung; die Antwort wird sofort gegen den
    # Fingerprint aus dem Dashboard geprueft. Alles danach nur noch gegen genau diese CA.
    #
    # Wichtig: .NET Framework haelt TLS-Verbindungen offen und verwendet sie wieder. Ohne Gegenmassnahme
    # liefe der Agent-Download ueber die ungepruefte Verbindung des CA-Abrufs (Pruefung wuerde nie
    # ausgefuehrt). Deshalb: Pruefung pro Request, kein Keep-Alive, getrennte Verbindungsgruppen.
    Info 'Pruefe Server-CA ...'
    try {
        $caPem = Invoke-Fetch -Url "$Server/agent/ca" -Validator { $true } -Group 'aiopatcher-unverified'
    } catch {
        Fail "Server nicht erreichbar: $Server ($($_.Exception.Message))"
    }

    $caBase64 = ($caPem -replace '-----[A-Z ]+-----', '') -replace '\s', ''
    $pinnedCa = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, [Convert]::FromBase64String($caBase64))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $actual = [BitConverter]::ToString($sha256.ComputeHash($pinnedCa.RawData)) -replace '-', ''
    if ($actual -ne $Fingerprint) { Fail "CA-Fingerprint stimmt nicht! Erwartet $Fingerprint, Server liefert $actual. Abbruch." }
    $caPath = Join-Path $tmp 'ca.crt'
    Set-Content -Path $caPath -Value $caPem -Encoding Ascii

    $pinnedValidator = {
        param($sender, $certificate, $chain, $sslErrors)
        if ($null -eq $certificate) { return $false }
        if ($sslErrors -band [Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch) { return $false }
        $pinnedChain = New-Object Security.Cryptography.X509Certificates.X509Chain
        $pinnedChain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $pinnedChain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
        [void]$pinnedChain.ChainPolicy.ExtraStore.Add($pinnedCa)
        $leaf = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, $certificate.GetRawCertData())
        if (-not $pinnedChain.Build($leaf)) { return $false }
        $root = $pinnedChain.ChainElements[$pinnedChain.ChainElements.Count - 1].Certificate
        return ($root.Thumbprint -eq $pinnedCa.Thumbprint)
    }.GetNewClosure()

    # --- 2. Agent herunterladen und Pruefsumme kontrollieren --------------------------------------
    Info 'Lade Agent (win-x64) ...'
    $exeTmp = Join-Path $tmp 'aiopatcher-agent.exe'
    try {
        Invoke-Fetch -Url "$Server/agent/download/win-x64" -OutFile $exeTmp -Validator $pinnedValidator -Group 'aiopatcher-pinned' | Out-Null
        $expected = (Invoke-Fetch -Url "$Server/agent/download/win-x64/sha256" -Validator $pinnedValidator -Group 'aiopatcher-pinned').Trim().ToUpperInvariant()
    } catch {
        Fail "Download fehlgeschlagen: $($_.Exception.Message)"
    }
    $got = (Get-FileHash -Path $exeTmp -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not $expected -or $got -ne $expected) { Fail "Pruefsumme stimmt nicht (erwartet $expected, erhalten $got)." }
    $version = & $exeTmp version
    if ($LASTEXITCODE -ne 0) { Fail 'Der Agent startet auf diesem System nicht.' }
    Info "Agent $version geprueft (SHA-256 $got)."

    if ($DownloadOnly) {
        New-Item -ItemType Directory -Force -Path $DownloadOnly | Out-Null
        Copy-Item -Path $exeTmp, $caPath -Destination $DownloadOnly -Force
        Info "Nur heruntergeladen nach $DownloadOnly, nichts installiert."
        exit 0
    }

    # --- 3. Installieren (bei Update: Dienst kurz anhalten) ---------------------------------------
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Info 'Halte laufenden Agent fuer das Update an ...'
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path $exeTmp -Destination $Exe -Force

    # --- 4. Registrieren (nur wenn noetig) --------------------------------------------------------
    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) {
        Info 'Agent ist bereits registriert, nur Programm aktualisiert.'
    } else {
        if (-not $Token) { Fail '-Token fehlt (noetig fuer die Erstregistrierung).' }
        $enrollArgs = @('enroll', '--server', $Server, '--token', $Token, '--fingerprint', $Fingerprint, '--state-dir', $StateDir)
        if ($Force) { $enrollArgs += '--force' }
        & $Exe @enrollArgs
        if ($LASTEXITCODE -ne 0) { Fail 'Registrierung fehlgeschlagen.' }
    }

    # --- 5. Windows-Dienst ------------------------------------------------------------------------
    if (-not $svc) {
        New-Service -Name $ServiceName -DisplayName 'AIOPatcher Agent' `
            -Description 'Meldet Updates an den AIOPatcher-Server und fuehrt freigegebene Patch-Jobs aus.' `
            -BinaryPathName "`"$Exe`" run --state-dir `"$StateDir`"" -StartupType Automatic | Out-Null
    }
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/30000/restart/60000 | Out-Null
    Start-Service -Name $ServiceName

    Info "Fertig. Status: Get-Service $ServiceName"
    Info 'Logs: Ereignisanzeige > Windows-Protokolle > Anwendung, Quelle AIOPatcherAgent'
    Info "Lokale Policy (Patchen/Neustarts einschraenken): $StateDir\policy.json"
} finally {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = $null
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
