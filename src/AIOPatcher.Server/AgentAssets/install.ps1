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
    [string]$DownloadOnly,
    # Firewall-, RDP- und Benutzer-Aenderungen aus dem Dashboard erlauben (policy.json allowSystemChanges; ab Werk aus)
    [switch]$AllowSystemChanges,
    # Lite-Agent (PowerShell) erzwingen. Auf Server 2008 R2 / Windows 7 automatisch.
    [switch]$Lite
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

# --- Lite-Agent fuer alte Windows-Versionen ------------------------------------------------------
# Der normale Agent (.NET 10) laeuft erst ab Server 2012. Davor: PowerShell-Agent, nur Updates und Neustarts,
# gestartet als Aufgabe (SYSTEM, beim Systemstart, Neustart nach Absturz).

function Enable-Tls12 {
    $changed = $false
    foreach ($side in @('Client', 'Server')) {
        $p = "HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\$side"
        if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null; $changed = $true }
        $cur = Get-ItemProperty -Path $p
        if ($cur.Enabled -ne 1 -or $cur.DisabledByDefault -ne 0) {
            Set-ItemProperty -Path $p -Name Enabled -Value 1 -Type DWord
            Set-ItemProperty -Path $p -Name DisabledByDefault -Value 0 -Type DWord
            $changed = $true
        }
    }
    foreach ($net in @('HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319', 'HKLM:\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319')) {
        if (Test-Path $net) {
            Set-ItemProperty -Path $net -Name SchUseStrongCrypto -Value 1 -Type DWord
            Set-ItemProperty -Path $net -Name SystemDefaultTlsVersions -Value 1 -Type DWord
        }
    }
    return $changed
}

function Get-LiteTask {
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    try { return $scheduler.GetFolder('\').GetTask($ServiceName) } catch { return $null }
}

function Install-LiteAgent {
    Info 'Altes Windows: installiere den Lite-Agent (Updates scannen und installieren, Neustarts).'
    $file = Join-Path $tmp 'aiopatcher-agent-lite.ps1'
    try {
        Invoke-Fetch -Url "$Server/agent/download/lite-windows" -OutFile $file -Validator $pinnedValidator -Group 'aiopatcher-pinned' | Out-Null
        $expected = (Invoke-Fetch -Url "$Server/agent/download/lite-windows/sha256" -Validator $pinnedValidator -Group 'aiopatcher-pinned').Trim().ToUpperInvariant()
    } catch {
        Fail "Download fehlgeschlagen: $($_.Exception.Message)"
    }
    $got = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not $expected -or $got -ne $expected) { Fail "Pruefsumme stimmt nicht (erwartet $expected, erhalten $got)." }

    $liteFile = Join-Path $InstallDir 'aiopatcher-agent-lite.ps1'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $task = Get-LiteTask
    if ($task -and $task.State -eq 4) { Info 'Halte laufenden Agent fuer das Update an ...'; $task.Stop(0) }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path $file -Destination $liteFile -Force

    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) {
        Info 'Agent ist bereits registriert, nur Programm aktualisiert.'
    } else {
        if (-not $Token) { Fail '-Token fehlt (noetig fuer die Erstregistrierung).' }
        $enrollArgs = @('enroll', '-Server', $Server, '-Token', $Token, '-Fingerprint', $Fingerprint, '-StateDir', $StateDir)
        if ($Force) { $enrollArgs += '-Force' }
        & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $liteFile @enrollArgs
        if ($LASTEXITCODE -ne 0) { Fail 'Registrierung fehlgeschlagen.' }
    }

    Info 'Selbsttest ...'
    & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $liteFile selftest -StateDir $StateDir
    if ($LASTEXITCODE -ne 0) { Fail 'Selbsttest fehlgeschlagen (siehe oben). Wurde TLS 1.2 gerade erst eingeschaltet: Server neu starten und den Befehl erneut ausfuehren.' }

    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>AIOPatcher Lite-Agent: meldet Updates an den AIOPatcher-Server und fuehrt freigegebene Patch-Jobs aus.</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled><Delay>PT1M</Delay></BootTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author"><Exec><Command>$ps</Command><Arguments>-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$liteFile" run -StateDir "$StateDir"</Arguments></Exec></Actions>
</Task>
"@
    $xmlPath = Join-Path $tmp 'task.xml'
    [IO.File]::WriteAllText($xmlPath, $xml, [Text.Encoding]::Unicode)
    & schtasks.exe /Create /TN $ServiceName /XML $xmlPath /F | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'Aufgabe konnte nicht angelegt werden (schtasks).' }

    # Crash-Check wie beim normalen Agent: laeuft die Aufgabe stabil und meldet sie sich beim Server?
    $started = Get-Date
    $lastCheckin = Join-Path $StateDir 'last-checkin'
    & schtasks.exe /Run /TN $ServiceName | Out-Null
    Info 'Pruefe, ob der Agent stabil laeuft und sich beim Server meldet (bis 90 s) ...'
    $result = 'timeout'
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        if ((Test-Path $lastCheckin) -and (Get-Item $lastCheckin).LastWriteTime -ge $started) { $result = 'ok'; break }
        $t = Get-LiteTask
        if ($i -gt 2 -and ($null -eq $t -or $t.State -ne 4)) { $result = 'crashed'; break }
    }
    $logFile = Join-Path $StateDir 'agent-lite.log'
    switch ($result) {
        'ok' { Info 'Agent laeuft und hat sich beim Server gemeldet.' }
        'crashed' {
            if (Test-Path $logFile) { Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host $_ } }
            Fail 'Der Agent ist nach dem Start beendet worden. Die Aufgabe startet ihn nach 1 Minute neu; Ursache im Log oben.'
        }
        default {
            if (Test-Path $logFile) { Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host $_ } }
            Fail 'Der Agent laeuft, hat sich aber nach 90 s noch nicht beim Server gemeldet (Firewall, TLS 1.2, Netzfreigabe der Site?).'
        }
    }
    Info "Autostart: Aufgabe '$ServiceName' (SYSTEM, beim Systemstart, Neustart nach Absturz nach 1 Minute)."
    Info "Fertig. Log: $logFile"
}

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
    if (Get-LiteTask) { & schtasks.exe /End /TN $ServiceName 2>$null | Out-Null; & schtasks.exe /Delete /TN $ServiceName /F | Out-Null }
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
$UseLite = $Lite -or ([Environment]::OSVersion.Version -lt [Version]'6.2')
if ($UseLite) {
    if ($PSVersionTable.PSVersion.Major -lt 5) {
        Fail 'Windows PowerShell 5.1 fehlt: WMF 5.1 installieren (https://aka.ms/wmf51download), neu starten und den Befehl erneut ausfuehren.'
    }
    $netRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
    if (-not $netRelease -or $netRelease -lt 528040) {
        Fail '.NET Framework 4.8 fehlt: installieren (https://dotnet.microsoft.com/download/dotnet-framework/net48), neu starten und den Befehl erneut ausfuehren.'
    }
    if (Enable-Tls12) { Info 'TLS 1.2 in SChannel eingeschaltet. Scheitert gleich die Verbindung: Server neu starten und Befehl erneut ausfuehren.' }
}
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

    if ($UseLite) {
        Install-LiteAgent
        exit 0
    }

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
    # Reste eines automatischen Updates: sonst wuerde dessen Waechter die gerade installierte Version ueberschreiben.
    foreach ($leftover in @((Join-Path $StateDir 'update-pending.json'), "$Exe.bak", (Join-Path $InstallDir 'aiopatcher-agent.new.exe'))) {
        Remove-Item -LiteralPath $leftover -Force -ErrorAction SilentlyContinue
    }

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

    if ($AllowSystemChanges) {
        & $Exe policy set allowSystemChanges true --state-dir $StateDir | Out-Null
        Info 'Aenderungen an Firewall, RDP und Benutzern aus dem Dashboard erlaubt (policy.json).'
    }

    # --- 5. Selbsttest vor dem Dienststart --------------------------------------------------------
    # Startet der Agent, liest er seine Registrierung, erreicht er den Server mit seinem Zertifikat?
    Info 'Selbsttest ...'
    & $Exe selftest --state-dir $StateDir
    if ($LASTEXITCODE -ne 0) { Fail 'Selbsttest fehlgeschlagen (siehe oben). Der Dienst wurde nicht gestartet.' }

    # --- 6. Windows-Dienst ------------------------------------------------------------------------
    if (-not $svc) {
        New-Service -Name $ServiceName -DisplayName 'AIOPatcher Agent' `
            -Description 'Meldet Updates an den AIOPatcher-Server und fuehrt freigegebene Patch-Jobs aus.' `
            -BinaryPathName "`"$Exe`" run --state-dir `"$StateDir`"" -StartupType Automatic | Out-Null
    }
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    # Nach Absturz neu starten, immer wieder (Zaehler nach 1 Tag zurueck). failureflag 1: auch bei Beenden mit Fehlercode.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/30000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    # --- 7. Crash-Check: laeuft der Dienst stabil und meldet er sich beim Server? -------------------
    $started = Get-Date
    $lastCheckin = Join-Path $StateDir 'last-checkin'
    function Show-AgentLog {
        Write-Host '--- Letzte Eintraege (Ereignisanzeige, Anwendung) ---' -ForegroundColor Yellow
        $events = @()
        $events += Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AIOPatcherAgent'; StartTime = $started } -MaxEvents 20 -ErrorAction SilentlyContinue
        $events += Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error'; StartTime = $started } -MaxEvents 20 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'aiopatcher' }
        $events | Sort-Object TimeCreated | ForEach-Object { Write-Host ("{0:HH:mm:ss} {1}" -f $_.TimeCreated, ($_.Message -split "`n")[0]) }
    }

    try {
        Start-Service -Name $ServiceName
    } catch {
        Show-AgentLog
        Fail "Dienst startet nicht: $($_.Exception.Message)"
    }

    Info 'Pruefe, ob der Agent stabil laeuft und sich beim Server meldet (bis 90 s) ...'
    $firstPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
    $result = 'timeout'
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        if ((Test-Path $lastCheckin) -and (Get-Item $lastCheckin).LastWriteTime -ge $started) { $result = 'ok'; break }
        $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if ($service.State -ne 'Running' -or $service.ProcessId -ne $firstPid) { $result = 'crashed'; break }
    }

    switch ($result) {
        'ok' { Info 'Agent laeuft und hat sich beim Server gemeldet.' }
        'crashed' {
            Show-AgentLog
            Fail 'Der Agent ist nach dem Start abgestuerzt. Windows startet ihn nach 30 s neu; Ursache in der Ereignisanzeige (siehe oben).'
        }
        default {
            Show-AgentLog
            Fail 'Der Dienst laeuft, hat sich aber nach 90 s noch nicht beim Server gemeldet (Firewall, Netzfreigabe der Site?). Er versucht es weiter.'
        }
    }

    if ((Get-Service -Name $ServiceName).StartType.ToString() -like 'Automatic*') {
        Info 'Autostart: aktiv (verzoegerter Start mit Windows, Neustart nach Absturz nach 30 s).'
    } else {
        Fail "Autostart ist nicht aktiv: Set-Service -Name $ServiceName -StartupType Automatic"
    }

    Info "Fertig. Status: Get-Service $ServiceName"
    Info 'Logs: Ereignisanzeige > Windows-Protokolle > Anwendung, Quelle AIOPatcherAgent'
    Info "Lokale Policy (Patchen/Neustarts einschraenken): $StateDir\policy.json"
} finally {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = $null
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
