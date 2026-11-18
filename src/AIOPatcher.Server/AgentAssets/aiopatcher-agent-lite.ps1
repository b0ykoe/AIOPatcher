<#
AIOPatcher lite agent for old Windows systems (Server 2008 R2 SP1, Windows 7 SP1) that the regular
agent (.NET 10) does not run on. Pull agent like the full one: fetches tasks and only answers them.

Modules: system (describe), updates (scan, install, reboot), agent (renew). Nothing else.
Requirements: Windows PowerShell 5.1 (WMF 5.1) and .NET Framework 4.8, TLS 1.2 enabled in SChannel.

  aiopatcher-agent-lite.ps1 enroll -Server https://... -Token ... -Fingerprint <sha256> [-StateDir DIR] [-Force]
  aiopatcher-agent-lite.ps1 run [-StateDir DIR]
  aiopatcher-agent-lite.ps1 selftest [-StateDir DIR]
  aiopatcher-agent-lite.ps1 version

Note: this file is deliberately pure ASCII (PowerShell 5.1 reads scripts without a BOM as ANSI).
#>
param(
    [Parameter(Position = 0)][string]$Command = 'run',
    [string]$StateDir = (Join-Path $env:ProgramData 'AIOPatcher\Agent'),
    [string]$Server,
    [string]$Token,
    [string]$Fingerprint,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$AgentVersion = '1.0-lite'
$Modules = @(
    @{ id = 'system'; available = $true; disabledLocally = $false; actions = @('describe') },
    @{ id = 'updates'; available = $true; disabledLocally = $false; actions = @('scan', 'install', 'reboot') },
    @{ id = 'agent'; available = $true; disabledLocally = $false; actions = @('renew') }
)
$SecurityCategory = '0fa1201d-4330-4fa8-8ae9-b877473b6441'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-AgentLog([string]$Message) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    $file = Join-Path $StateDir 'agent-lite.log'
    try {
        if ((Test-Path $file) -and (Get-Item $file).Length -gt 5MB) { Move-Item $file "$file.1" -Force }
        Add-Content -Path $file -Value $line -Encoding UTF8
    } catch { }
}

function Read-Json([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    return (Get-Content -Path $Path -Raw -Encoding UTF8) | ConvertFrom-Json
}

function Write-Json([string]$Path, $Value) {
    $tmp = "$Path.tmp"
    Set-Content -Path $tmp -Value (ConvertTo-Json -InputObject $Value -Depth 10) -Encoding UTF8
    Move-Item -Path $tmp -Destination $Path -Force
}

function UtcNow { return [DateTime]::UtcNow.ToString('o') }

# --- Key, certificate, connection --------------------------------------------------------------------

function New-AgentKey {
    # Persistent CNG machine key: SChannel needs a stored key for client certificates.
    $params = New-Object Security.Cryptography.CngKeyCreationParameters
    $params.KeyCreationOptions = [Security.Cryptography.CngKeyCreationOptions]::MachineKey
    $params.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $name = 'AIOPatcherAgent-' + [Guid]::NewGuid().ToString('N')
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $name, $params)
    return New-Object Security.Cryptography.ECDsaCng($key)
}

function New-Csr($Ecdsa) {
    $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest(("CN=" + $env:COMPUTERNAME), $Ecdsa, [Security.Cryptography.HashAlgorithmName]::SHA256)
    $der = $request.CreateSigningRequest()
    return "-----BEGIN CERTIFICATE REQUEST-----`n" + [Convert]::ToBase64String($der, 'InsertLineBreaks') + "`n-----END CERTIFICATE REQUEST-----"
}

function Install-ClientCertificate([string]$Pem, $Ecdsa) {
    $b64 = ($Pem -replace '-----[A-Z ]+-----', '') -replace '\s', ''
    $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [Convert]::FromBase64String($b64))
    $withKey = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::CopyWithPrivateKey($cert, $Ecdsa)
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
    $store.Open('ReadWrite')
    try { $store.Add($withKey) } finally { $store.Close() }
    return $withKey.Thumbprint
}

function Get-PinnedCa {
    $pem = Get-Content -Path (Join-Path $StateDir 'ca.crt') -Raw
    $b64 = ($pem -replace '-----[A-Z ]+-----', '') -replace '\s', ''
    return New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [Convert]::FromBase64String($b64))
}

function New-Validator($PinnedCa) {
    return {
        param($sender, $certificate, $chain, $errors)
        if ($null -eq $certificate) { return $false }
        $c = New-Object Security.Cryptography.X509Certificates.X509Chain
        $c.ChainPolicy.RevocationMode = 'NoCheck'
        $c.ChainPolicy.VerificationFlags = 'AllowUnknownCertificateAuthority'
        [void]$c.ChainPolicy.ExtraStore.Add($PinnedCa)
        $leaf = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, $certificate.GetRawCertData())
        if (-not $c.Build($leaf)) { return $false }
        return ($c.ChainElements[$c.ChainElements.Count - 1].Certificate.Thumbprint -eq $PinnedCa.Thumbprint)
    }.GetNewClosure()
}

function Get-ClientCertificate($Config) {
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
    $store.Open('ReadOnly')
    try {
        $found = $store.Certificates.Find('FindByThumbprint', $Config.certificateThumbprint, $false)
        if ($found.Count -eq 0) { throw "Client certificate $($Config.certificateThumbprint) not found in LocalMachine\My." }
        return $found[0]
    } finally { $store.Close() }
}

class ServerError : Exception {
    [int]$Status
    ServerError([int]$status, [string]$text) : base("Server responded with ${status}: $text") { $this.Status = $status }
}

function Invoke-Agent([string]$Method, [string]$Path, $Body, [int]$TimeoutSeconds = 60) {
    $config = Read-Json (Join-Path $StateDir 'agent.json')
    $request = [Net.HttpWebRequest]::Create($config.serverUrl + $Path)
    $request.Method = $Method
    $request.KeepAlive = $false
    $request.Timeout = $TimeoutSeconds * 1000
    $request.ReadWriteTimeout = $TimeoutSeconds * 1000
    $request.UserAgent = "aiopatcher-agent/$AgentVersion"
    $request.Headers.Add('X-AIOPatcher-Agent-Version', $AgentVersion)
    $request.ServerCertificateValidationCallback = (New-Validator (Get-PinnedCa))
    [void]$request.ClientCertificates.Add((Get-ClientCertificate $config))
    if ($null -ne $Body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Depth 10 -Compress))
        $request.ContentType = 'application/json'
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    try {
        $response = $request.GetResponse()
    } catch [Net.WebException] {
        if ($_.Exception.Response) {
            $r = $_.Exception.Response
            $text = (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd()
            throw [ServerError]::new([int]$r.StatusCode, $text.Substring(0, [Math]::Min(500, $text.Length)))
        }
        throw
    }
    try {
        $text = (New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd()
    } finally { $response.Dispose() }
    if ($text.Trim().Length -eq 0) { return $null }
    return $text | ConvertFrom-Json
}

# --- System ------------------------------------------------------------------------------------------

function Get-PrefixLength([string]$Mask) {
    if ($Mask -notmatch '\.') { return [int]$Mask }
    $bits = 0
    foreach ($octet in $Mask.Split('.')) { $bits += ([Convert]::ToString([int]$octet, 2) -replace '0', '').Length }
    return $bits
}

function Get-Description($Policy) {
    $os = Get-WmiObject Win32_OperatingSystem
    $boot = $os.ConvertToDateTime($os.LastBootUpTime)
    $addresses = @()
    foreach ($nic in (Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = True')) {
        for ($i = 0; $i -lt $nic.IPAddress.Count; $i++) {
            $ip = $nic.IPAddress[$i]
            if ($ip -like 'fe80*') { continue }
            $addresses += @{ interfaceName = $nic.Description; address = $ip; prefixLength = (Get-PrefixLength $nic.IPSubnet[$i]) }
        }
    }
    $rebootRequired = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
                      (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
    $au = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction SilentlyContinue
    $disabled = @($Policy.disabledModules)
    $mods = @()
    foreach ($m in $Modules) {
        $copy = $m.Clone()
        $copy.disabledLocally = ($disabled -contains $m.id) -and ($m.id -notin @('system', 'agent'))
        $mods += $copy
    }
    return @{
        agentVersion = $AgentVersion
        hostname = $env:COMPUTERNAME
        osFamily = 'Windows'
        osName = $os.Caption.Trim()
        osVersion = "$($os.Version) SP$($os.ServicePackMajorVersion)"
        kernelVersion = $os.Version
        uptime = ([DateTime]::Now - $boot).ToString('c')
        rebootRequired = [bool]$rebootRequired
        nativeAutoUpdatesEnabled = ($null -ne $au -and $au.AUOptions -eq 4 -and $au.NoAutoUpdate -ne 1)
        addresses = $addresses
        architecture = 'x64'
        modules = $mods
    }
}

# --- Updates (Windows Update Agent) -------------------------------------------------------------------

function Get-UpdateInfo($Update) {
    $categories = @($Update.Categories | ForEach-Object { $_ })
    $kb = $null
    if ($Update.KBArticleIDs.Count -gt 0) { $kb = 'KB' + $Update.KBArticleIDs.Item(0) }
    $severity = $Update.MsrcSeverity
    $names = @($categories | ForEach-Object { $_.Name } | Where-Object { $_ } | Select-Object -Unique -First 3) -join ', '
    $isSecurity = (@($categories | Where-Object { $_.CategoryID -eq $SecurityCategory }).Count -gt 0) -or -not [string]::IsNullOrEmpty($severity)
    $classification = $names
    if ($severity) { $classification = "$names ($severity)" }
    return @{ name = $Update.Title; currentVersion = $null; availableVersion = $kb; isSecurity = $isSecurity; classification = $classification }
}

function Search-Updates {
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'AIOPatcher'
    return @{ Session = $session; Result = $session.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'") }
}

function Get-PendingUpdates {
    $search = Search-Updates
    $list = @()
    foreach ($u in $search.Result.Updates) { $list += (Get-UpdateInfo $u) }
    return , $list
}

function Test-Held($Holds, [string]$Title, [string]$Kb) {
    foreach ($p in @($Holds)) {
        if (-not $p) { continue }
        if ($Title -like $p -or ($Kb -and $Kb -like $p)) { return $true }
    }
    return $false
}

function Install-Updates($Payload, $Policy, [scriptblock]$Log) {
    $scope = if ($Payload.scope) { $Payload.scope } else { 'Security' }
    $allow = if ($Policy.allowPatch) { $Policy.allowPatch } else { 'All' }
    if ($allow -eq 'None') { return @{ outcome = 'Rejected'; summary = 'Local policy forbids patching.' } }
    if ($allow -eq 'Security' -and $scope -eq 'All') { return @{ outcome = 'Rejected'; summary = 'Local policy only allows security updates.' } }

    & $Log '[agent] Searching for updates via Windows Update / WSUS ...'
    $search = Search-Updates
    $collection = New-Object -ComObject Microsoft.Update.UpdateColl
    foreach ($u in $search.Result.Updates) {
        $info = Get-UpdateInfo $u
        if ($scope -ne 'All' -and -not $info.isSecurity) { continue }
        if (Test-Held $Payload.holds $info.name $info.availableVersion) { & $Log "[agent] Held back: $($info.name)"; continue }
        if (-not $u.EulaAccepted) { $u.AcceptEula() }
        & $Log "[agent] Selected: $($info.name)"
        [void]$collection.Add($u)
    }
    if ($collection.Count -eq 0) {
        return @{ outcome = 'Succeeded'; summary = $(if ($scope -eq 'Security') { 'No pending security updates.' } else { 'No pending updates.' }); updates = (Get-PendingUpdates) }
    }

    & $Log "[agent] Downloading $($collection.Count) update(s) ..."
    $downloader = $search.Session.CreateUpdateDownloader()
    $downloader.Updates = $collection
    [void]$downloader.Download()
    $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
    foreach ($u in $collection) { if ($u.IsDownloaded) { [void]$toInstall.Add($u) } }
    if ($toInstall.Count -eq 0) { return @{ outcome = 'Failed'; summary = 'No update could be downloaded.' } }

    & $Log "[agent] Installing $($toInstall.Count) update(s) ..."
    $installer = $search.Session.CreateUpdateInstaller()
    $installer.AllowSourcePrompts = $false
    $installer.Updates = $toInstall
    $result = $installer.Install()
    $ok = $result.ResultCode -in @(2, 3) # 2 = succeeded, 3 = succeeded with errors
    $after = Get-PendingUpdates
    $summary = "$($toInstall.Count) update(s) installed (result code $($result.ResultCode)). Still pending afterwards: $($after.Count)."
    $reboot = [bool]$result.RebootRequired
    if ($reboot -and $Payload.allowReboot -and $Policy.allowReboot -ne $false) {
        return @{ outcome = $(if ($ok) { 'Succeeded' } else { 'Failed' }); summary = $summary; updates = $after; reboot = $true }
    }
    if ($reboot) { $summary += ' Reboot required.' }
    return @{ outcome = $(if ($ok) { 'Succeeded' } else { 'Failed' }); summary = $summary; updates = $after }
}

# --- Tasks -------------------------------------------------------------------------------------------

function Send-Log([string]$TaskId, $Lines) {
    if ($Lines.Count -gt 0) { [void](Invoke-Agent 'POST' "/agent/v2/tasks/$TaskId/log" @{ lines = @($Lines) }) }
}

function Invoke-Task($Task) {
    $policy = Read-Json (Join-Path $StateDir 'policy.json')
    if ($null -eq $policy) { $policy = [pscustomobject]@{ allowPatch = 'All'; allowReboot = $true; disabledModules = @() } }
    $lines = New-Object Collections.ArrayList
    $log = { param($text) [void]$lines.Add(@{ timestampUtc = (UtcNow); text = $text }) }.GetNewClosure()
    $id = $Task.taskId
    Write-AgentLog "Task ${id}: $($Task.module).$($Task.action)"

    $known = @{}
    foreach ($m in $Modules) { $known[$m.id] = $m.actions }
    if (-not $known.ContainsKey($Task.module) -or $known[$Task.module] -notcontains $Task.action) {
        return Invoke-Agent 'POST' "/agent/v2/tasks/$id/result" @{ outcome = 'Rejected'; summary = "Unknown task $($Task.module).$($Task.action) (lite agent $AgentVersion)." }
    }
    if (@($policy.disabledModules) -contains $Task.module -and $Task.module -notin @('system', 'agent')) {
        return Invoke-Agent 'POST' "/agent/v2/tasks/$id/result" @{ outcome = 'Rejected'; summary = "Module '$($Task.module)' is disabled on this host (policy.json)." }
    }

    $reboot = $false
    $renewKey = $null
    try {
        switch ("$($Task.module).$($Task.action)") {
            'system.describe' { $r = @{ outcome = 'Succeeded'; summary = 'lite agent'; payload = (Get-Description $policy) } }
            'updates.scan' {
                $updates = Get-PendingUpdates
                $sec = @($updates | Where-Object { $_.isSecurity }).Count
                $r = @{ outcome = 'Succeeded'; summary = "$($updates.Count) updates available, $sec of them security."; payload = @{ updates = $updates } }
            }
            'updates.install' {
                $i = Install-Updates $Task.payload $policy $log
                $r = @{ outcome = $i.outcome; summary = $i.summary }
                if ($i.updates) { $r.payload = @{ updates = $i.updates } }
                $reboot = [bool]$i.reboot
            }
            'updates.reboot' {
                if ($policy.allowReboot -eq $false) { $r = @{ outcome = 'Rejected'; summary = 'Local policy forbids reboots.' } }
                else { $r = @{ outcome = 'Succeeded'; summary = 'Reboot requested.' }; $reboot = $true }
            }
            'agent.renew' {
                $renewKey = New-AgentKey
                $r = @{ outcome = 'Succeeded'; summary = 'Certificate signing request created.'; payload = @{ certificateSigningRequestPem = (New-Csr $renewKey) } }
            }
        }
    } catch {
        $r = @{ outcome = 'Failed'; summary = "Error in lite agent: $($_.Exception.Message)" }
    }

    if ($reboot) {
        & $log '[agent] Triggering reboot. The result will be reported after the reboot.'
        Send-Log $id $lines
        Write-Json (Join-Path $StateDir 'pending-result.json') @{ jobId = $id; outcome = $r.outcome; summary = $r.summary; rebootRequestedUtc = (UtcNow); awaitReboot = $true }
        & shutdown.exe /r /t 60 /c 'AIOPatcher: reboot after patch job' | Out-Null
        if ($LASTEXITCODE -eq 0) { return $null }
        Remove-Item (Join-Path $StateDir 'pending-result.json') -Force
        $r.outcome = 'Failed'
        $r.summary += ' Reboot could not be triggered.'
    }

    Send-Log $id $lines
    $body = @{ outcome = $r.outcome; summary = $r.summary }
    if ($r.payload) { $body.payload = $r.payload }
    $response = Invoke-Agent 'POST' "/agent/v2/tasks/$id/result" $body
    Write-AgentLog "Task ${id}: $($r.outcome). $($r.summary)"

    if ($renewKey -and $response -and $response.payload -and $response.payload.clientCertificatePem) {
        $config = Read-Json (Join-Path $StateDir 'agent.json')
        $config.certificateThumbprint = Install-ClientCertificate $response.payload.clientCertificatePem $renewKey
        Write-Json (Join-Path $StateDir 'agent.json') $config
        Write-AgentLog 'Client certificate renewed. Restarting.'
        exit 75
    }
    return $response
}

function Send-PendingResult {
    $file = Join-Path $StateDir 'pending-result.json'
    $pending = Read-Json $file
    if ($null -eq $pending) { return $null }
    $requested = [DateTime]::Parse($pending.rebootRequestedUtc).ToUniversalTime()
    $os = Get-WmiObject Win32_OperatingSystem
    $boot = $os.ConvertToDateTime($os.LastBootUpTime).ToUniversalTime()
    $body = @{ outcome = $pending.outcome; summary = $pending.summary; rebootPerformed = $false }
    if ($pending.awaitReboot) {
        if ($boot -gt $requested.AddSeconds(-5)) { $body.summary += ' Rebooted.'; $body.rebootPerformed = $true }
        elseif (([DateTime]::UtcNow - $requested).TotalMinutes -lt 10) { return $pending.jobId }
        else { $body.outcome = 'Failed'; $body.summary += ' Reboot was triggered but did not happen.' }
    }
    try { [void](Invoke-Agent 'POST' "/agent/v2/tasks/$($pending.jobId)/result" $body) }
    catch [ServerError] { if ($_.Exception.Status -notin @(404, 409)) { throw } }
    Remove-Item $file -Force
    return $null
}

# --- Commands ----------------------------------------------------------------------------------------

function Invoke-Enroll {
    if (-not $Server -or -not $Token -or -not $Fingerprint) { throw 'enroll -Server https://... -Token ... -Fingerprint <sha256>' }
    $Server = $Server.TrimEnd('/')
    $expected = ($Fingerprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ((Test-Path (Join-Path $StateDir 'agent.json')) -and -not $Force) { throw "Already enrolled ($StateDir). Use -Force." }
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null

    $insecure = [Net.HttpWebRequest]::Create("$Server/agent/ca")
    $insecure.KeepAlive = $false
    $insecure.ConnectionGroupName = 'aiopatcher-unverified'
    $insecure.ServerCertificateValidationCallback = { $true }
    $resp = $insecure.GetResponse()
    $caPem = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
    $resp.Dispose()
    $b64 = ($caPem -replace '-----[A-Z ]+-----', '') -replace '\s', ''
    $ca = New-Object Security.Cryptography.X509Certificates.X509Certificate2(, [Convert]::FromBase64String($b64))
    $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ca.RawData)) -replace '-', ''
    if ($sha -ne $expected) { throw "CA fingerprint mismatch: expected $expected, server sent $sha." }

    $ecdsa = New-AgentKey
    $body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Compress -InputObject @{
        enrollmentToken = $Token; certificateSigningRequestPem = (New-Csr $ecdsa); hostname = $env:COMPUTERNAME; osFamily = 'Windows'; agentVersion = $AgentVersion }))
    $request = [Net.HttpWebRequest]::Create("$Server/agent/enroll")
    $request.Method = 'POST'
    $request.KeepAlive = $false
    $request.ConnectionGroupName = 'aiopatcher-pinned'
    $request.ContentType = 'application/json'
    $request.ServerCertificateValidationCallback = (New-Validator $ca)
    $stream = $request.GetRequestStream(); $stream.Write($body, 0, $body.Length); $stream.Dispose()
    $response = $request.GetResponse()
    $data = (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd() | ConvertFrom-Json
    $response.Dispose()

    Set-Content -Path (Join-Path $StateDir 'ca.crt') -Value $caPem.Trim() -Encoding Ascii
    $thumb = Install-ClientCertificate $data.clientCertificatePem $ecdsa
    $uri = [Uri]$Server
    Write-Json (Join-Path $StateDir 'agent.json') @{ serverUrl = "$($uri.Scheme)://$($uri.Authority)"; hostId = $data.hostId; siteId = $data.siteId; certificateThumbprint = $thumb }
    if (-not (Test-Path (Join-Path $StateDir 'policy.json'))) {
        Write-Json (Join-Path $StateDir 'policy.json') @{ allowPatch = 'All'; allowReboot = $true; disabledModules = @() }
    }
    # A pending result for a previous server is worthless after re-enrolling
    Remove-Item (Join-Path $StateDir 'pending-result.json') -Force -ErrorAction SilentlyContinue
    Write-Host "Enrolled. Host ID $($data.hostId), data in $StateDir"
}

function Invoke-SelfTest {
    Write-Host "version: $AgentVersion"
    try {
        $hello = Invoke-Agent 'GET' '/agent/hello' $null 45
        Write-Host "ok   server: knows this host as '$($hello.hostName)' (server $($hello.serverVersion))"
        Write-Host "ok   system: $((Get-WmiObject Win32_OperatingSystem).Caption)"
        Write-Host 'selftest: ok'
        exit 0
    } catch {
        Write-Host "FAIL $($_.Exception.Message)"
        Write-Host 'selftest: FAILED'
        exit 1
    }
}

function Invoke-Loop {
    $config = Read-Json (Join-Path $StateDir 'agent.json')
    if ($null -eq $config) { throw "Agent is not enrolled ($StateDir)." }
    Write-AgentLog "AIOPatcher lite agent $AgentVersion started. Server $($config.serverUrl), host $($config.hostId)"
    $interval = 30
    while ($true) {
        $wait = $interval
        try {
            $waiting = Send-PendingResult
            $path = '/agent/v2/poll'
            if ($waiting) { $path += "?running=$waiting" }
            $response = Invoke-Agent 'GET' $path $null
            Set-Content -Path (Join-Path $StateDir 'last-checkin') -Value "$(UtcNow) $AgentVersion" -Encoding Ascii
            $interval = [Math]::Max([int]$response.pollIntervalSeconds, 10)
            $wait = $interval
            if ($response.task -and -not $waiting) {
                [void](Invoke-Task $response.task)
                $wait = 1
            }
        } catch [ServerError] {
            Write-AgentLog "Poll failed: $($_.Exception.Message)"
            $wait = $(if ($_.Exception.Status -in @(401, 403)) { 900 } else { 60 })
        } catch {
            Write-AgentLog "Poll failed: $($_.Exception.Message)"
            $wait = [Math]::Min(60, $interval)
        }
        Start-Sleep -Seconds $wait
    }
}

switch ($Command) {
    'version' { Write-Host $AgentVersion }
    'enroll' { Invoke-Enroll }
    'selftest' { Invoke-SelfTest }
    'run' { Invoke-Loop }
    default { Write-Host 'Commands: run (default), enroll, selftest, version'; exit 2 }
}
