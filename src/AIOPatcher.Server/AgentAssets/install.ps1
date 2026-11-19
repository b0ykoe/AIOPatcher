<#
AIOPatcher agent: installation, update and uninstall for Windows
(Windows Server 2019/2022/2025, Windows 10/11). Runs on Windows PowerShell 5.1.

First installation (the dashboard provides the complete command under "Add host"):
  install.ps1 -Server https://patch.example.com:8443 -Token <token> -Fingerprint <sha256>
Host outside (cloud), server only reachable internally: additionally the public address and checksum
  install.ps1 -Server https://10.0.0.5:8443 -PublicUrl https://agents.example.com -Sha256 win-x64=<sha256> -Token <token> -Fingerprint <sha256>
Update (agent stays enrolled):
  install.ps1 -Server https://patch.example.com:8443 -Fingerprint <sha256>
Uninstall:
  install.ps1 -Uninstall [-Purge]

Note: this file is deliberately pure ASCII (PowerShell 5.1 reads scripts without BOM as ANSI).
#>
[CmdletBinding()]
param(
    [string]$Server,
    [string]$Fingerprint,
    [string]$Token,
    # Public address via the reverse proxy (fallback when -Server is not reachable)
    [string]$PublicUrl,
    # Checksums of the agent builds from the dashboard ("win-x64=<sha256>"), required for the download via -PublicUrl
    [string]$Sha256,
    [switch]$Force,
    [switch]$Uninstall,
    [switch]$Purge,
    [string]$DownloadOnly,
    # Allow firewall, RDP and user changes from the dashboard (policy.json allowSystemChanges; off by default)
    [switch]$AllowSystemChanges,
    # Allow installing, reconfiguring and uninstalling Wazuh/Checkmk clients (policy.json allowMonitoringChanges; off by default)
    [switch]$AllowMonitoringChanges,
    # Further values for policy.json, e.g. 'allowPatch=Security,allowReboot=false' (keys as in policy.json)
    [string]$Policy,
    # Force the lite agent (PowerShell). Automatic on Server 2008 R2 / Windows 7.
    [switch]$Lite
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ServiceName = 'AIOPatcherAgent'
$InstallDir = Join-Path $env:ProgramFiles 'AIOPatcher'
$Exe = Join-Path $InstallDir 'aiopatcher-agent.exe'
$StateDir = Join-Path $env:ProgramData 'AIOPatcher\Agent'

function Info([string]$Message) { Write-Host "==> $Message" }

# HTTPS fetch with its own certificate check ONLY for this request (not global),
# without keep-alive and in its own connection group, so no connection is reused.
# Without a validator: normal validation by Windows (public address with the reverse proxy's certificate).
function Invoke-Fetch([string]$Url, [scriptblock]$Validator, [string]$Group, [string]$OutFile, [int]$TimeoutMs = 600000) {
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.KeepAlive = $false
    $request.ConnectionGroupName = $Group
    $request.Timeout = $TimeoutMs
    $request.ReadWriteTimeout = 600000
    if ($Validator) { $request.ServerCertificateValidationCallback = $Validator }
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
function Fail([string]$Message) { Write-Host "Error: $Message" -ForegroundColor Red; exit 1 }

# --- Lite agent for old Windows versions ---------------------------------------------------------
# The regular agent (.NET 10) needs Server 2012 or newer. Before that: PowerShell agent, updates and reboots only,
# started as a scheduled task (SYSTEM, at system startup, restart after a crash).

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

# Lite agent: no "policy set" command, so set the key in policy.json directly (other keys stay).
function Set-LitePolicy([string]$Key, $Value = $true) {
    $path = Join-Path $StateDir 'policy.json'
    $data = if (Test-Path $path) { (Get-Content -Path $path -Raw -Encoding UTF8) | ConvertFrom-Json } else { New-Object PSObject }
    if ($null -eq $data) { $data = New-Object PSObject }
    if ($Value -eq 'true') { $Value = $true } elseif ($Value -eq 'false') { $Value = $false }
    $data | Add-Member -NotePropertyName $Key -NotePropertyValue $Value -Force
    Set-Content -Path "$path.tmp" -Value (ConvertTo-Json -InputObject $data -Depth 10) -Encoding UTF8
    Move-Item -Path "$path.tmp" -Destination $path -Force
}

function Install-LiteAgent {
    Info 'Old Windows: installing the lite agent (scans and installs updates, reboots).'
    $file = Join-Path $tmp 'aiopatcher-agent-lite.ps1'
    try {
        Invoke-Fetch -Url "$Server/agent/download/lite-windows" -OutFile $file -Validator $pinnedValidator -Group 'aiopatcher-pinned' | Out-Null
        $expected = (Invoke-Fetch -Url "$Server/agent/download/lite-windows/sha256" -Validator $pinnedValidator -Group 'aiopatcher-pinned').Trim().ToUpperInvariant()
    } catch {
        Fail "Download failed: $($_.Exception.Message)"
    }
    $got = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not $expected -or $got -ne $expected) { Fail "Checksum mismatch (expected $expected, got $got)." }

    $liteFile = Join-Path $InstallDir 'aiopatcher-agent-lite.ps1'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $task = Get-LiteTask
    if ($task -and $task.State -eq 4) { Info 'Stopping the running agent for the update ...'; $task.Stop(0) }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path $file -Destination $liteFile -Force

    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) {
        Info 'Agent is already enrolled with this server, only the program was updated (to re-enroll: -Force).'
    } else {
        if (-not $Token) { Fail '-Token is missing (required for the first enrollment).' }
        $enrollArgs = @('enroll', '-Server', $Server, '-Token', $Token, '-Fingerprint', $Fingerprint, '-StateDir', $StateDir)
        if ($Force) { $enrollArgs += '-Force' }
        if ($Force) { Remove-Item (Join-Path $StateDir 'pending-result.json') -Force -ErrorAction SilentlyContinue }
        & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $liteFile @enrollArgs
        if ($LASTEXITCODE -ne 0) { Fail 'Enrollment failed.' }
    }

    if ($AllowMonitoringChanges) {
        Set-LitePolicy 'allowMonitoringChanges'
        Info 'Installing and reconfiguring Wazuh/Checkmk clients from the dashboard allowed (policy.json).'
    }
    foreach ($p in $PolicyPairs) { Set-LitePolicy $p[0] $p[1] }
    if ($PolicyPairs.Count -gt 0) { Info "Local policy set: $Policy (policy.json)." }

    Info 'Self-test ...'
    & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $liteFile selftest -StateDir $StateDir
    if ($LASTEXITCODE -ne 0) { Fail 'Self-test failed (see above). If TLS 1.2 was only just enabled: reboot the server and run the command again.' }

    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>AIOPatcher lite agent: reports updates to the AIOPatcher server and runs approved patch jobs.</Description></RegistrationInfo>
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
    if ($LASTEXITCODE -ne 0) { Fail 'Could not create the scheduled task (schtasks).' }

    # Crash check as for the regular agent: does the task run stably and check in with the server?
    $started = Get-Date
    $lastCheckin = Join-Path $StateDir 'last-checkin'
    & schtasks.exe /Run /TN $ServiceName | Out-Null
    Info 'Checking that the agent runs stably and checks in with the server (up to 90 s) ...'
    $result = 'timeout'
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        if ((Test-Path $lastCheckin) -and (Get-Item $lastCheckin).LastWriteTime -ge $started) { $result = 'ok'; break }
        $t = Get-LiteTask
        if ($i -gt 2 -and ($null -eq $t -or $t.State -ne 4)) { $result = 'crashed'; break }
    }
    $logFile = Join-Path $StateDir 'agent-lite.log'
    switch ($result) {
        'ok' { Info 'Agent is running and has checked in with the server.' }
        'crashed' {
            if (Test-Path $logFile) { Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host $_ } }
            Fail 'The agent exited after starting. The task restarts it after 1 minute; see the log above for the cause.'
        }
        default {
            if (Test-Path $logFile) { Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host $_ } }
            Fail 'The agent is running but has not checked in with the server after 90 s (firewall, TLS 1.2, network access for the site?).'
        }
    }
    Info "Autostart: scheduled task '$ServiceName' (SYSTEM, at system startup, restart 1 minute after a crash)."
    Info "Done. Log: $logFile"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $DownloadOnly -and -not $isAdmin) { Fail 'Please run PowerShell as Administrator.' }

# --- Uninstall -----------------------------------------------------------------------------------
if ($Uninstall) {
    Info 'Removing the AIOPatcher agent ...'
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
        & sc.exe delete $ServiceName | Out-Null
    }
    if (Get-LiteTask) { & schtasks.exe /End /TN $ServiceName 2>$null | Out-Null; & schtasks.exe /Delete /TN $ServiceName /F | Out-Null }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if ($Purge) {
        if (Test-Path $StateDir) { Remove-Item $StateDir -Recurse -Force }
        Info 'Enrollment deleted. Delete the host in the dashboard as well.'
    } else {
        Info "Enrollment kept in $StateDir (remove with -Purge)."
    }
    exit 0
}

# --- Checks --------------------------------------------------------------------------------------
$UseLite = $Lite -or ([Environment]::OSVersion.Version -lt [Version]'6.2')
if ($UseLite) {
    if ($PSVersionTable.PSVersion.Major -lt 5) {
        Fail 'Windows PowerShell 5.1 is missing: install WMF 5.1 (https://aka.ms/wmf51download), reboot and run the command again.'
    }
    $netRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
    if (-not $netRelease -or $netRelease -lt 528040) {
        Fail '.NET Framework 4.8 is missing: install it (https://dotnet.microsoft.com/download/dotnet-framework/net48), reboot and run the command again.'
    }
    if (Enable-Tls12) { Info 'TLS 1.2 enabled in SChannel. If the connection fails next: reboot the server and run the command again.' }
}
if (-not $Server) { Fail '-Server is missing.' }
if (-not $Fingerprint) { Fail '-Fingerprint is missing.' }
$Server = $Server.TrimEnd('/')
if (-not $Server.StartsWith('https://')) { Fail '-Server must start with https://.' }
if ($PublicUrl) {
    $PublicUrl = $PublicUrl.TrimEnd('/')
    if (-not $PublicUrl.StartsWith('https://')) { Fail '-PublicUrl must start with https://.' }
}
$Fingerprint = ($Fingerprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
# -Policy 'allowPatch=Security,allowReboot=false': check everything before anything is installed
$PolicyPairs = @()
foreach ($pair in ("$Policy" -split '[,;]')) {
    $pair = $pair.Trim()
    if (-not $pair) { continue }
    $key, $value = $pair -split '=', 2
    switch -CaseSensitive ($key) {
        'allowPatch' { if (@('All', 'Security', 'None') -notcontains $value) { Fail '-Policy allowPatch: All, Security or None.' } }
        { @('allowReboot', 'allowSelfUpdate', 'allowAppUpdates', 'allowSourceFixes', 'allowAutoUpdateChanges', 'allowSystemChanges', 'allowMonitoringChanges', 'allowServerUpdate', 'allowUpdateRecipes', 'trustServerUpdates') -ccontains $_ } {
            if (@('true', 'false') -notcontains $value) { Fail "-Policy ${key}: true or false." }
        }
        default { Fail "-Policy: unknown key in '$pair'." }
    }
    $PolicyPairs += , @($key, $value)
}
if (-not [Environment]::Is64BitOperatingSystem) { Fail 'Only 64-bit Windows is supported.' }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('aiopatcher-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    # --- 1. Fetch the CA and verify it against the fingerprint (pinning) --------------------------
    # Only this one fetch runs without certificate validation; the response is immediately checked
    # against the fingerprint from the dashboard. Everything after that only against exactly this CA.
    #
    # Important: .NET Framework keeps TLS connections open and reuses them. Without a countermeasure
    # the agent download would run over the unverified connection of the CA fetch (validation would never
    # run). Therefore: validation per request, no keep-alive, separate connection groups.
    Info 'Verifying the server CA ...'
    # Via the public address, TLS belongs to the reverse proxy: there, too, only the CA fingerprint counts,
    # and the agent is verified against the checksum from the install command.
    $viaPublic = $false
    try {
        $caPem = Invoke-Fetch -Url "$Server/agent/ca" -Validator { $true } -Group 'aiopatcher-unverified' -TimeoutMs 15000
    } catch {
        if (-not $PublicUrl) { Fail "Server not reachable: $Server ($($_.Exception.Message))" }
        Info "$Server not reachable, using the public address $PublicUrl."
        try {
            $caPem = Invoke-Fetch -Url "$PublicUrl/agent/ca" -Group 'aiopatcher-public' -TimeoutMs 30000
        } catch {
            Fail "$PublicUrl is not reachable either ($($_.Exception.Message)). Check the reverse proxy and DNS."
        }
        $viaPublic = $true
    }

    $caBase64 = ($caPem -replace '-----[A-Z ]+-----', '') -replace '\s', ''
    $pinnedCa = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, [Convert]::FromBase64String($caBase64))
    $hasher = [Security.Cryptography.SHA256]::Create()
    $actual = [BitConverter]::ToString($hasher.ComputeHash($pinnedCa.RawData)) -replace '-', ''
    if ($actual -ne $Fingerprint) { Fail "CA fingerprint mismatch! Expected $Fingerprint, server delivers $actual. Aborting." }
    $caPath = Join-Path $tmp 'ca.crt'
    Set-Content -Path $caPath -Value $caPem -Encoding Ascii

    # Already enrolled, but with a different server (different CA, e.g. a freshly set up instance)? Then re-enroll
    # with the token from the new dashboard: new key, new certificate, new CA. policy.json is kept.
    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) {
        $oldFingerprint = 'none'
        $oldCaPath = Join-Path $StateDir 'ca.crt'
        if (Test-Path $oldCaPath) {
            try {
                $oldBase64 = ((Get-Content -Path $oldCaPath -Raw) -replace '-----[A-Z ]+-----', '') -replace '\s', ''
                $oldCa = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, [Convert]::FromBase64String($oldBase64))
                $oldFingerprint = [BitConverter]::ToString($hasher.ComputeHash($oldCa.RawData)) -replace '-', ''
            } catch { $oldFingerprint = 'unreadable' }
        }
        if ($oldFingerprint -ne $Fingerprint) {
            if (-not $Token) { Fail 'This host is enrolled with a different AIOPatcher server. To switch, use the install command (with token) from the new dashboard.' }
            Info "Previously enrolled with a different server: re-enrolling with $Server (policy.json is kept)."
            $Force = $true
        }
    }

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

    if ($UseLite -and $viaPublic) { Fail 'The lite agent (old systems) only connects to the server directly or via VPN, not via the public address.' }
    if ($UseLite) {
        Install-LiteAgent
        exit 0
    }

    # --- 2. Download the agent and verify the checksum --------------------------------------------
    Info 'Downloading agent (win-x64) ...'
    $exeTmp = Join-Path $tmp 'aiopatcher-agent.exe'
    try {
        if ($viaPublic) {
            $expected = ''
            foreach ($pair in ("$Sha256" -split ',')) {
                $kv = $pair.Trim() -split '=', 2
                if ($kv.Count -eq 2 -and $kv[0] -eq 'win-x64') { $expected = $kv[1].Trim().ToUpperInvariant() }
            }
            if (-not $expected) { Fail 'The checksum (-Sha256) for win-x64 is missing. Get the install command again from the dashboard.' }
            Invoke-Fetch -Url "$PublicUrl/agent/download/win-x64" -OutFile $exeTmp -Group 'aiopatcher-public' | Out-Null
        } else {
            Invoke-Fetch -Url "$Server/agent/download/win-x64" -OutFile $exeTmp -Validator $pinnedValidator -Group 'aiopatcher-pinned' | Out-Null
            $expected = (Invoke-Fetch -Url "$Server/agent/download/win-x64/sha256" -Validator $pinnedValidator -Group 'aiopatcher-pinned').Trim().ToUpperInvariant()
        }
    } catch {
        Fail "Download failed: $($_.Exception.Message)"
    }
    $got = (Get-FileHash -Path $exeTmp -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not $expected -or $got -ne $expected) { Fail "Checksum mismatch (expected $expected, got $got)." }
    $version = & $exeTmp version
    if ($LASTEXITCODE -ne 0) { Fail 'The agent does not start on this system.' }
    Info "Agent $version verified (SHA-256 $got)."

    if ($DownloadOnly) {
        New-Item -ItemType Directory -Force -Path $DownloadOnly | Out-Null
        Copy-Item -Path $exeTmp, $caPath -Destination $DownloadOnly -Force
        Info "Only downloaded to $DownloadOnly, nothing installed."
        exit 0
    }

    # --- 3. Install (on update: briefly stop the service) -----------------------------------------
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Info 'Stopping the running agent for the update ...'
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path $exeTmp -Destination $Exe -Force
    # Leftovers of an automatic update: otherwise its watchdog would overwrite the version just installed.
    foreach ($leftover in @((Join-Path $StateDir 'update-pending.json'), "$Exe.bak", (Join-Path $InstallDir 'aiopatcher-agent.new.exe'))) {
        Remove-Item -LiteralPath $leftover -Force -ErrorAction SilentlyContinue
    }

    # --- 4. Enroll (only if needed) ---------------------------------------------------------------
    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) {
        Info 'Agent is already enrolled with this server, only the program was updated (to re-enroll: -Force).'
    } else {
        if (-not $Token) { Fail '-Token is missing (required for the first enrollment).' }
        $enrollArgs = @('enroll', '--server', $Server, '--token', $Token, '--fingerprint', $Fingerprint, '--state-dir', $StateDir)
        if ($PublicUrl) { $enrollArgs += @('--public-url', $PublicUrl) }
        if ($Force) { $enrollArgs += '--force' }
        if ($Force) { Remove-Item (Join-Path $StateDir 'pending-result.json') -Force -ErrorAction SilentlyContinue }
        & $Exe @enrollArgs
        if ($LASTEXITCODE -ne 0) { Fail 'Enrollment failed.' }
    }

    if ($AllowSystemChanges) {
        & $Exe policy set allowSystemChanges true --state-dir $StateDir | Out-Null
        Info 'Changes to firewall, RDP and users from the dashboard allowed (policy.json).'
    }

    if ($AllowMonitoringChanges) {
        & $Exe policy set allowMonitoringChanges true --state-dir $StateDir | Out-Null
        Info 'Installing and reconfiguring Wazuh/Checkmk clients from the dashboard allowed (policy.json).'
    }

    foreach ($p in $PolicyPairs) {
        & $Exe policy set $p[0] $p[1] --state-dir $StateDir | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "Could not set $($p[0]) in policy.json." }
    }
    if ($PolicyPairs.Count -gt 0) { Info "Local policy set: $Policy (policy.json)." }

    # --- 5. Self-test before starting the service -------------------------------------------------
    # Does the agent start, read its enrollment, and reach the server with its certificate?
    Info 'Self-test ...'
    & $Exe selftest --state-dir $StateDir
    if ($LASTEXITCODE -ne 0) { Fail 'Self-test failed (see above). The service was not started.' }

    # --- 6. Windows service -----------------------------------------------------------------------
    if (-not $svc) {
        New-Service -Name $ServiceName -DisplayName 'AIOPatcher Agent' `
            -Description 'Reports updates to the AIOPatcher server and runs approved patch jobs.' `
            -BinaryPathName "`"$Exe`" run --state-dir `"$StateDir`"" -StartupType Automatic | Out-Null
    }
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    # Restart after a crash, again and again (counter resets after 1 day). failureflag 1: also on exit with an error code.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/30000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    # --- 7. Crash check: does the service run stably and check in with the server? ----------------
    $started = Get-Date
    $lastCheckin = Join-Path $StateDir 'last-checkin'
    function Show-AgentLog {
        Write-Host '--- Latest entries (Event Viewer, Application) ---' -ForegroundColor Yellow
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
        Fail "Service does not start: $($_.Exception.Message)"
    }

    Info 'Checking that the agent runs stably and checks in with the server (up to 90 s) ...'
    $firstPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
    $result = 'timeout'
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        if ((Test-Path $lastCheckin) -and (Get-Item $lastCheckin).LastWriteTime -ge $started) { $result = 'ok'; break }
        $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if ($service.State -ne 'Running' -or $service.ProcessId -ne $firstPid) { $result = 'crashed'; break }
    }

    switch ($result) {
        'ok' { Info 'Agent is running and has checked in with the server.' }
        'crashed' {
            Show-AgentLog
            Fail 'The agent crashed after starting. Windows restarts it after 30 s; see Event Viewer (above) for the cause.'
        }
        default {
            Show-AgentLog
            Fail 'The service is running but has not checked in with the server after 90 s (firewall, network access for the site?). It keeps trying.'
        }
    }

    if ((Get-Service -Name $ServiceName).StartType.ToString() -like 'Automatic*') {
        Info 'Autostart: active (delayed start with Windows, restart 30 s after a crash).'
    } else {
        Fail "Autostart is not active: Set-Service -Name $ServiceName -StartupType Automatic"
    }

    Info "Done. Status: Get-Service $ServiceName"
    Info 'Logs: Event Viewer > Windows Logs > Application, source AIOPatcherAgent'
    Info "Local policy (restrict patching/reboots): $StateDir\policy.json"
} finally {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = $null
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
