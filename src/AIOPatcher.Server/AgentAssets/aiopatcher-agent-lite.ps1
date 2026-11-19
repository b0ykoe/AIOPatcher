<#
AIOPatcher lite agent for old Windows systems (Server 2008 R2 SP1, Windows 7 SP1) that the regular
agent (.NET 10) does not run on. Pull agent like the full one: fetches tasks and only answers them.

Modules: system (describe), updates (scan, install, reboot), monitoring (status, change), agent (renew). Nothing else.
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
    @{ id = 'monitoring'; available = $true; disabledLocally = $false; actions = @('status', 'change') },
    @{ id = 'agent'; available = $true; disabledLocally = $false; actions = @('renew') }
)
$SecurityCategory = '0fa1201d-4330-4fa8-8ae9-b877473b6441'
$MonitoringKinds = @('MonitoringInstall', 'MonitoringConfigure', 'MonitoringUninstall')
$MonitoringPolicyReject = 'Local policy forbids changing monitoring clients. To allow it on this host, set "allowMonitoringChanges": true in C:\ProgramData\AIOPatcher\Agent\policy.json (lite agent).'
$MonitoringPackagePath = '/agent/v2/monitoring-package/'
$MonVersionPattern = '^(?:\d+:)?[vV]?(?<main>\d+(?:\.\d+){0,3})(?:p(?<p>\d+))?'
$WazuhConnectTimeoutSeconds = 300
$script:MonTaskId = $null
$script:MonLines = $null
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

function New-AgentRequest([string]$Method, [string]$Path, [int]$TimeoutSeconds) {
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
    return $request
}

# GET a file over the agent channel (mTLS) and stream it to $Target.
function Save-AgentFile([string]$Path, [string]$Target, [int]$TimeoutSeconds = 1800) {
    if (-not $Path.StartsWith('/agent/', [StringComparison]::Ordinal) -or $Path.Contains('..')) { throw 'Unexpected download path.' }
    $request = New-AgentRequest 'GET' $Path $TimeoutSeconds
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
        $source = $response.GetResponseStream()
        $targetStream = [IO.File]::Create($Target)
        try { $source.CopyTo($targetStream) } finally { $targetStream.Dispose(); $source.Dispose() }
    } finally { $response.Dispose() }
}

function Invoke-Agent([string]$Method, [string]$Path, $Body, [int]$TimeoutSeconds = 60) {
    $request = New-AgentRequest $Method $Path $TimeoutSeconds
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
        # Only for display in the dashboard; the lite agent knows only these switches
        policy = @{
            allowPatch = $(if ($Policy.allowPatch) { [string]$Policy.allowPatch } else { 'All' })
            allowReboot = ($Policy.allowReboot -ne $false)
            allowMonitoringChanges = (($Policy.allowMonitoringChanges -is [bool]) -and $Policy.allowMonitoringChanges)
            disabledModules = @($disabled | Where-Object { $_ })
        }
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

# --- Monitoring clients (Wazuh agent, Checkmk agent) --------------------------------------------------
# Port of the full agent (Platforms/Monitoring). AIOPatcher only manages the client on this host, never the
# Wazuh manager or the Checkmk site. Status is always allowed; changes need policy.json allowMonitoringChanges.

function New-MonResult([string]$Outcome, [string]$Summary) { return @{ outcome = $Outcome; summary = $Summary } }

# Sends the log lines collected so far (long waits and installs), so the server shows progress.
function Send-MonitoringLog {
    if ($script:MonTaskId -and $null -ne $script:MonLines -and $script:MonLines.Count -gt 0) {
        try { Send-Log $script:MonTaskId $script:MonLines; $script:MonLines.Clear() } catch { }
    }
}

# Versions: "v4.7.2", "4.7.2-1", "2.2.0p12", "1:2.1.0-1"
function Get-MonVersionParts($Version) {
    if ($null -eq $Version -or [string]::IsNullOrWhiteSpace([string]$Version)) { return $null }
    $m = [regex]::Match(([string]$Version).Trim(), $MonVersionPattern)
    if (-not $m.Success) { return $null }
    $parts = New-Object Collections.Generic.List[int]
    foreach ($x in $m.Groups['main'].Value.Split('.')) { $parts.Add([int]$x) }
    while ($parts.Count -lt 3) { $parts.Add(0) }
    if ($m.Groups['p'].Success) { $parts.Add([int]$m.Groups['p'].Value) } else { $parts.Add(0) }
    return , $parts.ToArray()
}

# < 0: A older than B. Unreadable versions count as equal (nothing is rejected then).
function Compare-MonVersion($A, $B) {
    $x = Get-MonVersionParts $A
    $y = Get-MonVersionParts $B
    if ($null -eq $x -or $null -eq $y) { return 0 }
    $n = [Math]::Max($x.Count, $y.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $xi = 0; if ($i -lt $x.Count) { $xi = $x[$i] }
        $yi = 0; if ($i -lt $y.Count) { $yi = $y[$i] }
        if ($xi -ne $yi) { if ($xi -lt $yi) { return -1 } else { return 1 } }
    }
    return 0
}

function Compare-MonVersionMinor($A, $B) {
    $x = Get-MonVersionParts $A
    $y = Get-MonVersionParts $B
    if ($null -eq $x -or $null -eq $y) { return 0 }
    if ($x[0] -ne $y[0]) { if ($x[0] -lt $y[0]) { return -1 } else { return 1 } }
    if ($x[1] -ne $y[1]) { if ($x[1] -lt $y[1]) { return -1 } else { return 1 } }
    return 0
}

function Get-MonVersionDisplay($Version) {
    if ($null -eq $Version) { return $null }
    $v = ([string]$Version).Trim()
    $m = [regex]::Match($v, $MonVersionPattern)
    if (-not $m.Success) { return $v }
    $parts = $m.Value.TrimStart([char[]]'vV').Split(':')
    return $parts[$parts.Count - 1]
}

function Test-MonHost($Value) {
    return ($Value -is [string]) -and $Value.Length -gt 0 -and $Value.Length -le 253 -and ($Value -cmatch '^[A-Za-z0-9._\-:\[\]]+\z')
}

function Test-MonName($Value) {
    return ($Value -is [string]) -and $Value.Length -gt 0 -and $Value.Length -le 200 -and ($Value -cmatch '^[A-Za-z0-9._\-,@]+\z')
}

function Test-MonPortInvalid($Value) {
    if ($null -eq $Value) { return $false }
    if (-not ($Value -is [int] -or $Value -is [long])) { return $true }
    return ($Value -lt 1 -or $Value -gt 65535)
}

# IP address or network (CIDR) as only_from expects it.
function Test-MonSource($Value) {
    if (-not ($Value -is [string]) -or $Value.Length -eq 0) { return $false }
    $ip = $null
    if (-not $Value.Contains('/')) { return [Net.IPAddress]::TryParse($Value, [ref]$ip) }
    $parts = $Value.Split('/')
    if ($parts.Count -ne 2 -or $parts[1] -notmatch '^\d{1,3}\z' -or -not [Net.IPAddress]::TryParse($parts[0], [ref]$ip)) { return $false }
    $bytes = $ip.GetAddressBytes()
    $prefix = [int]$parts[1]
    if ($prefix -gt $bytes.Length * 8) { return $false }
    for ($i = $prefix; $i -lt $bytes.Length * 8; $i++) {
        if ($bytes[$i -shr 3] -band (0x80 -shr ($i % 8))) { return $false }
    }
    return $true
}

# Windows command line quoting (CommandLineToArgvW rules); .NET 4.8 has no ArgumentList.
function ConvertTo-MonArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq [char]92) { $slashes++; continue }
        if ($ch -eq [char]34) {
            [void]$sb.Append([char]92, 2 * $slashes + 1)
            [void]$sb.Append([char]34)
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$sb.Append([char]92, $slashes); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    if ($slashes -gt 0) { [void]$sb.Append([char]92, 2 * $slashes) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# @{ code; out; err }; $null when the program does not exist.
function Invoke-MonProcess([string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    if (-not $File -or -not (Test-Path -LiteralPath $File -PathType Leaf)) { return $null }
    try {
        $quoted = New-Object Collections.Generic.List[string]
        foreach ($a in @($Arguments)) { if ($null -ne $a) { $quoted.Add((ConvertTo-MonArgument $a)) } }
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $File
        $psi.Arguments = [string]::Join(' ', $quoted.ToArray())
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            try { $p.Kill() } catch { }
            return @{ code = -1; out = ''; err = "Timed out after $TimeoutSeconds seconds." }
        }
        $p.WaitForExit()
        return @{ code = $p.ExitCode; out = $stdout.Result; err = $stderr.Result }
    } catch {
        return @{ code = -1; out = ''; err = $_.Exception.Message }
    }
}

# msiexec; exit codes 3010/1641 = succeeded, reboot needed/initiated (the agent never reboots by itself).
function Invoke-MonMsiExec([string[]]$Arguments, [scriptblock]$Log) {
    $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    & $Log ('$ msiexec ' + ($Arguments -join ' '))
    $r = Invoke-MonProcess $msiexec $Arguments 900
    if ($null -eq $r) { return 'msiexec failed (exit code -1).' }
    if (([string]$r.err).Trim().Length -gt 0) { & $Log ([string]$r.err).Trim() }
    Send-MonitoringLog
    if ($r.code -in @(0, 3010, 1641)) { return $null }
    return "msiexec failed (exit code $($r.code))."
}

function Get-ProgramFilesX86 {
    if (${env:ProgramFiles(x86)}) { return ${env:ProgramFiles(x86)} }
    return $env:ProgramFiles
}

# Uninstall entry (64- and 32-bit view) whose display name matches: version and ProductCode.
function Get-MonUninstallEntry([scriptblock]$Match) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        foreach ($path in @('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
            $root = $base.OpenSubKey($path)
            if ($null -eq $root) { continue }
            try {
                foreach ($name in $root.GetSubKeyNames()) {
                    $key = $null
                    try { $key = $root.OpenSubKey($name) } catch { continue }
                    if ($null -eq $key) { continue }
                    try {
                        $display = $key.GetValue('DisplayName')
                        if ($display -is [string] -and (& $Match $display)) {
                            $code = $null
                            if ($name -match '^\{[0-9A-Fa-f\-]{36}\}\z') { $code = $name }
                            $version = $key.GetValue('DisplayVersion')
                            if (-not ($version -is [string])) { $version = $null }
                            return @{ version = $version; productCode = $code }
                        }
                    } finally { $key.Close() }
                }
            } finally { $root.Close() }
        }
    } finally { $base.Close() }
    return $null
}

function Test-WazuhDisplayName([string]$Name) {
    return $Name.StartsWith('Wazuh Agent', [StringComparison]::OrdinalIgnoreCase) -or $Name.StartsWith('OSSEC', [StringComparison]::OrdinalIgnoreCase)
}

# Display name per version: "Check MK Agent", "Check_MK Agent", "Checkmk Agent ..."
function Test-CheckmkDisplayName([string]$Name) {
    if ($Name.IndexOf('Checkmk', [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    return ($Name.IndexOf('Check', [StringComparison]::OrdinalIgnoreCase) -ge 0) -and ($Name.IndexOf('MK', [StringComparison]::Ordinal) -ge 0) -and
           ($Name.IndexOf('Agent', [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Find-MonService([string[]]$Names) {
    foreach ($n in $Names) { if (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$n") { return $n } }
    return $null
}

# Windows service whose program lies below the folder (the name differs between versions).
function Find-MonServiceForImage([string]$Folder, [string]$FileName) {
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Services')
    if ($null -eq $root) { return $null }
    try {
        $trimmed = $Folder.TrimEnd([char]92)
        foreach ($name in $root.GetSubKeyNames()) {
            $key = $null
            try { $key = $root.OpenSubKey($name) } catch { continue }
            if ($null -eq $key) { continue }
            try {
                $image = $key.GetValue('ImagePath')
                if ($image -is [string] -and $image.IndexOf($FileName, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    $image.Replace('"', '').IndexOf($trimmed, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $name }
            } finally { $key.Close() }
        }
    } finally { $root.Close() }
    return $null
}

function Test-MonServiceRunning([string]$Name) {
    if (-not $Name) { return $null }
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    return ($service.Status -eq 'Running')
}

function Restart-MonService([string]$Name, [scriptblock]$Log) {
    if (-not $Name -or $Name -notmatch '^[A-Za-z0-9_\-]+\z') { return $false }
    try {
        Restart-Service -Name $Name -Force -ErrorAction Stop -WarningAction SilentlyContinue
        & $Log "[agent] Service $Name restarted."
        return $true
    } catch {
        & $Log "[agent] Restarting service $Name failed: $($_.Exception.Message)"
        return $false
    }
}

# Text and whether the file had a UTF-8 BOM (kept when writing back).
function Read-MonText([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if ($bom) { $text = [Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3) } else { $text = [Text.Encoding]::UTF8.GetString($bytes) }
    return @{ text = $text; bom = $bom }
}

# Write into the existing file (ACLs stay).
function Write-MonText([string]$Path, [string]$Text, [bool]$Bom) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { $mode = [IO.FileMode]::Truncate } else { $mode = [IO.FileMode]::CreateNew }
    $stream = New-Object IO.FileStream($Path, $mode, [IO.FileAccess]::Write)
    try {
        if ($Bom) { $stream.Write([byte[]](0xEF, 0xBB, 0xBF), 0, 3) }
        $data = [Text.Encoding]::UTF8.GetBytes($Text)
        if ($data.Length -gt 0) { $stream.Write($data, 0, $data.Length) }
    } finally { $stream.Dispose() }
}

# Copy files into a new backup folder; remembers which files did not exist.
function Backup-MonFiles([string]$Product, $Files) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
    $folder = Join-Path (Join-Path (Join-Path $StateDir 'monitoring-backup') $Product) $stamp
    [void](New-Item -ItemType Directory -Force -Path $folder)
    $entries = New-Object Collections.ArrayList
    $seen = @{}
    $index = 0
    foreach ($file in @($Files)) {
        if (-not $file) { continue }
        $k = ([string]$file).ToLowerInvariant()
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $copy = Join-Path $folder ('{0:00}-{1}' -f $index, [IO.Path]::GetFileName($file))
            $index++
            [IO.File]::Copy($file, $copy, $true)
            [void]$entries.Add(@{ original = [string]$file; copy = $copy })
        } else {
            [void]$entries.Add(@{ original = [string]$file; copy = $null })
        }
    }
    $listing = New-Object Collections.Generic.List[string]
    foreach ($e in $entries) {
        $c = $e.copy
        if (-not $c) { $c = '-' }
        $listing.Add("$c`t$($e.original)")
    }
    [IO.File]::WriteAllLines((Join-Path $folder 'files.txt'), $listing.ToArray())
    return @{ folder = $folder; files = $entries }
}

# Previous state: backed up files back, files that did not exist before removed.
function Restore-MonBackup($Backup, [scriptblock]$Log) {
    foreach ($e in $Backup.files) {
        try {
            if ($null -eq $e.copy) {
                if (Test-Path -LiteralPath $e.original -PathType Leaf) { [IO.File]::Delete($e.original) }
            } else {
                [IO.File]::Copy($e.copy, $e.original, $true)
            }
        } catch {
            & $Log "[agent] Could not restore $($e.original): $($_.Exception.Message)"
        }
    }
    & $Log "[agent] Previous configuration restored (backup: $($Backup.folder))."
}

function New-MonClient([string]$Product, [string]$Variant, $Version, [bool]$Running, $Addresses, $Connected, $Detail, [bool]$CanConfigure, [bool]$Held) {
    $list = [string[]]@($Addresses | Where-Object { $null -ne $_ })
    return @{ product = $Product; variant = $Variant; version = $Version; serviceRunning = $Running; addresses = $list
              connected = $Connected; detail = $Detail; canConfigure = $CanConfigure; held = $Held; hostname = $null }
}

# --- Wazuh agent: ossec.conf via XDocument (several <ossec_config> blocks: parsed below a helper root) ---

function Get-XName([string]$Name) { return [System.Xml.Linq.XName]::Get($Name) }

function Import-WazuhConfig([string]$Text) {
    Add-Type -AssemblyName System.Xml.Linq
    $declaration = ''
    $body = $Text
    $m = [regex]::Match($Text, '^\s*<\?xml[^>]*\?>')
    if ($m.Success) { $declaration = $m.Value; $body = $Text.Substring($m.Length) }
    try {
        $doc = [System.Xml.Linq.XDocument]::Parse('<aiopatcher-wrapper>' + $body + '</aiopatcher-wrapper>', [System.Xml.Linq.LoadOptions]::PreserveWhitespace)
    } catch {
        $e = $_.Exception
        if ($e.InnerException) { $e = $e.InnerException }
        throw "ossec.conf is not readable: $($e.Message)"
    }
    return @{ doc = $doc; declaration = $declaration }
}

function Export-WazuhConfig($Loaded) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append([string]$Loaded.declaration)
    foreach ($node in $Loaded.doc.Root.Nodes()) { [void]$sb.Append($node.ToString([System.Xml.Linq.SaveOptions]::DisableFormatting)) }
    return $sb.ToString()
}

function Get-WazuhEndpoints($Doc) {
    $result = New-Object Collections.ArrayList
    foreach ($agent in $Doc.Root.Descendants((Get-XName 'agent'))) {
        foreach ($manager in $agent.Elements((Get-XName 'manager'))) {
            foreach ($endpoint in $manager.Elements((Get-XName 'endpoint'))) { [void]$result.Add($endpoint) }
        }
    }
    return , $result
}

function Get-WazuhClientElement($Doc) {
    foreach ($c in $Doc.Root.Descendants((Get-XName 'client'))) { return $c }
    return $null
}

function Get-XText($Element) {
    if ($null -eq $Element -or [string]::IsNullOrWhiteSpace($Element.Value)) { return $null }
    return $Element.Value.Trim()
}

function Get-WazuhLegacyElements($Client) {
    $result = New-Object Collections.ArrayList
    foreach ($e in $Client.Elements()) {
        if ($e.Name.LocalName -ceq 'server-ip' -or $e.Name.LocalName -ceq 'server-hostname') { [void]$result.Add($e) }
    }
    return , $result
}

function Read-WazuhConfigInfo([string]$Text) {
    $doc = (Import-WazuhConfig $Text).doc
    $endpoints = Get-WazuhEndpoints $doc
    if ($endpoints.Count -gt 0) {
        $list = New-Object Collections.Generic.List[string]
        foreach ($e in $endpoints) { $list.Add($e.Value.Trim()) }
        return @{ isAgent = $true; variant = 'wazuh5'; addresses = $list.ToArray(); enrollAddress = $null; passPath = $null }
    }
    $client = Get-WazuhClientElement $doc
    if ($null -eq $client) { return @{ isAgent = $false; variant = 'wazuh'; addresses = [string[]]@(); enrollAddress = $null; passPath = $null } }

    $enrollment = $client.Element((Get-XName 'enrollment'))
    $enrollAddress = $null
    $passPath = $null
    if ($null -ne $enrollment) {
        $enrollAddress = Get-XText ($enrollment.Element((Get-XName 'manager_address')))
        $passPath = Get-XText ($enrollment.Element((Get-XName 'authorization_pass_path')))
    }
    $servers = @($client.Elements((Get-XName 'server')))
    $legacy = Get-WazuhLegacyElements $client
    $list = New-Object Collections.Generic.List[string]
    if ($servers.Count -eq 0 -and $legacy.Count -gt 0) {
        $port = Get-XText ($client.Element((Get-XName 'port')))
        foreach ($e in $legacy) {
            $a = Get-XText $e
            if ($null -eq $a) { continue }
            if ($null -eq $port) { $list.Add($a) } else { $list.Add("${a}:$port") }
        }
        return @{ isAgent = $true; variant = 'ossec'; addresses = $list.ToArray(); enrollAddress = $enrollAddress; passPath = $passPath }
    }
    foreach ($s in $servers) {
        $a = Get-XText ($s.Element((Get-XName 'address')))
        $p = Get-XText ($s.Element((Get-XName 'port')))
        if ($null -eq $a) { continue }
        if ($null -eq $p) { $list.Add($a) } else { $list.Add("${a}:$p") }
    }
    return @{ isAgent = $true; variant = 'wazuh'; addresses = $list.ToArray(); enrollAddress = $enrollAddress; passPath = $passPath }
}

# Set a child; a new one goes after the last child with its indentation.
function Set-XChild($Parent, [string]$Name, [string]$Value) {
    $existing = $Parent.Element((Get-XName $Name))
    if ($null -ne $existing) { $existing.Value = $Value; return }
    Add-XChildElement $Parent ([System.Xml.Linq.XElement]::new((Get-XName $Name), $Value))
}

function Add-XChildElement($Parent, $Element) {
    $last = $null
    foreach ($e in $Parent.Elements()) { $last = $e }
    if ($null -eq $last) { $Parent.Add($Element); return }
    $indent = $null
    $prev = $last.PreviousNode
    if ($prev -is [System.Xml.Linq.XText] -and [string]::IsNullOrWhiteSpace($prev.Value)) { $indent = $prev.Value }
    $last.AddAfterSelf($Element)
    if ($null -ne $indent) { $Element.AddBeforeSelf([System.Xml.Linq.XText]::new([string]$indent)) }
}

function Remove-XWithWhitespace($Element) {
    $prev = $Element.PreviousNode
    if ($prev -is [System.Xml.Linq.XText] -and [string]::IsNullOrWhiteSpace($prev.Value)) { $prev.Remove() }
    $Element.Remove()
}

function Get-WazuhHostPart([string]$Address) {
    if ($Address.Contains(':') -and -not $Address.StartsWith('[', [StringComparison]::Ordinal)) { return "[$Address]" }
    return $Address
}

# Set the target. NewEnrollment: enroll with the new manager (with EnrollmentBlock: write <enrollment>, 4.0+);
# otherwise only the address changes (keys were moved to the new manager). Throws when the variant cannot do it.
function Update-WazuhConfigText([string]$Text, $Spec, [bool]$NewEnrollment, [bool]$EnrollmentBlock) {
    $address = [string]$Spec.address
    if (-not $address) { throw 'No manager address given.' }
    $loaded = Import-WazuhConfig $Text
    $doc = $loaded.doc

    $endpoints = Get-WazuhEndpoints $doc
    if ($endpoints.Count -gt 0) {
        if ($NewEnrollment) {
            throw 'Wazuh 5 agents enroll with tokens, which AIOPatcher does not support yet. Copy client.keys to the new manager and choose "keys already on the new manager".'
        }
        $value = 'https://' + (Get-WazuhHostPart $address)
        if ($null -ne $Spec.port) { $value += ':' + [string]$Spec.port }
        $endpoints[0].Value = $value
        for ($i = 1; $i -lt $endpoints.Count; $i++) { Remove-XWithWhitespace $endpoints[$i] }
        return Export-WazuhConfig $loaded
    }

    $client = Get-WazuhClientElement $doc
    if ($null -eq $client) { throw 'ossec.conf has no <client> section (is this a Wazuh manager?).' }
    $servers = @($client.Elements((Get-XName 'server')))
    $legacy = Get-WazuhLegacyElements $client

    if ($servers.Count -eq 0 -and $legacy.Count -gt 0) {
        # OSSEC / Wazuh before 3.0
        $ip = $null
        if ([Net.IPAddress]::TryParse($address, [ref]$ip)) { $elementName = 'server-ip' } else { $elementName = 'server-hostname' }
        $legacy[0].ReplaceWith([System.Xml.Linq.XElement]::new((Get-XName $elementName), $address))
        for ($i = 1; $i -lt $legacy.Count; $i++) { Remove-XWithWhitespace $legacy[$i] }
        if ($null -ne $Spec.port) { Set-XChild $client 'port' ([string]$Spec.port) }
        return Export-WazuhConfig $loaded
    }

    if ($servers.Count -eq 0) {
        $serverEl = [System.Xml.Linq.XElement]::new((Get-XName 'server'))
        $client.AddFirst($serverEl)
    } else {
        $serverEl = $servers[0]
        for ($i = 1; $i -lt $servers.Count; $i++) { Remove-XWithWhitespace $servers[$i] }
    }

    Set-XChild $serverEl 'address' $address
    if ($null -ne $Spec.port) { Set-XChild $serverEl 'port' ([string]$Spec.port) }
    if ($null -ne $Spec.protocol) { Set-XChild $serverEl 'protocol' ([string]$Spec.protocol) }

    $enrollment = $client.Element((Get-XName 'enrollment'))
    if ($NewEnrollment -and $EnrollmentBlock) {
        if ($null -eq $enrollment) {
            $enrollment = [System.Xml.Linq.XElement]::new((Get-XName 'enrollment'))
            Add-XChildElement $client $enrollment
        }
        Set-XChild $enrollment 'enabled' 'yes'
        Set-XChild $enrollment 'manager_address' $address
        if ($null -ne $Spec.enrollmentPort) { Set-XChild $enrollment 'port' ([string]$Spec.enrollmentPort) }
        if (-not [string]::IsNullOrWhiteSpace([string]$Spec.groups)) { Set-XChild $enrollment 'groups' ([string]$Spec.groups) }
    } elseif ($null -ne $enrollment -and $null -ne $enrollment.Element((Get-XName 'manager_address'))) {
        # Otherwise the client enrolls with the old manager after a rejection.
        $enrollment.Element((Get-XName 'manager_address')).Value = $address
    }
    return Export-WazuhConfig $loaded
}

function Get-WazuhStateValue($Text) {
    if ($null -eq $Text) { return $null }
    $m = [regex]::Match([string]$Text, '^\s*status\s*=\s*''?(\w+)''?', [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# $true = connected, $false = pending/disconnected, $null = unknown
function Get-WazuhConnected($Text) {
    $value = Get-WazuhStateValue $Text
    if ($null -eq $value) { return $null }
    return ($value -eq 'connected')
}

function Get-WazuhPaths {
    foreach ($root in @((Get-ProgramFilesX86), $env:ProgramFiles)) {
        if (-not $root) { continue }
        $folder = Join-Path $root 'ossec-agent'
        $config = Join-Path $folder 'ossec.conf'
        if (Test-Path -LiteralPath $config -PathType Leaf) {
            return @{
                folder = $folder; config = $config; keys = (Join-Path $folder 'client.keys')
                states = @((Join-Path $folder 'wazuh-agent.state'), (Join-Path $folder 'ossec-agent.state'))
                agentAuth = (Join-Path $folder 'agent-auth.exe'); defaultPass = (Join-Path $folder 'authd.pass')
            }
        }
    }
    return $null
}

# authorization_pass_path (relative to the installation folder) or the default.
function Get-WazuhPassFile($Paths, $Configured) {
    if ($null -eq $Configured) { return $Paths.defaultPass }
    if ([IO.Path]::IsPathRooted([string]$Configured)) { return [string]$Configured }
    return Join-Path $Paths.folder (([string]$Configured).Replace('/', '\'))
}

function Read-WazuhState($Paths) {
    foreach ($f in $Paths.states) {
        try {
            if (Test-Path -LiteralPath $f -PathType Leaf) {
                $fs = New-Object IO.FileStream($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
            }
        } catch { }
    }
    return $null
}

function Get-WazuhVersion($Paths) {
    $file = Join-Path $Paths.folder 'VERSION'
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        $lines = [IO.File]::ReadAllLines($file)
        if ($lines.Count -gt 0 -and $lines[0].Trim().Length -gt 0) { return $lines[0].Trim() }
    }
    $entry = Get-MonUninstallEntry { param($n) Test-WazuhDisplayName $n }
    if ($null -ne $entry) { return $entry.version }
    return $null
}

function Test-WazuhRunning($Paths) {
    $name = Find-MonService @('WazuhSvc', 'OssecSvc')
    if ($null -eq $name) { return $null }
    return Test-MonServiceRunning $name
}

function Restart-Wazuh($Paths, [scriptblock]$Log) {
    $name = Find-MonService @('WazuhSvc', 'OssecSvc')
    if ($null -eq $name) { return $false }
    return Restart-MonService $name $Log
}

function Get-WazuhClient {
    $paths = Get-WazuhPaths
    if ($null -eq $paths) { return $null }
    $detail = $null
    try {
        $info = Read-WazuhConfigInfo (Read-MonText $paths.config).text
    } catch {
        $info = @{ isAgent = $true; variant = 'wazuh'; addresses = [string[]]@(); enrollAddress = $null; passPath = $null }
        $detail = $_.Exception.Message
    }
    if (-not $info.isAgent) { return $null }
    $version = Get-WazuhVersion $paths
    $state = Read-WazuhState $paths
    $value = Get-WazuhStateValue $state
    $shown = $detail
    if ($null -eq $shown -and $value) { $shown = "status: $value" }
    return New-MonClient 'wazuh' $info.variant (Get-MonVersionDisplay $version) ((Test-WazuhRunning $paths) -eq $true) $info.addresses (Get-WazuhConnected $state) $shown ($null -eq $detail) $false
}

function Test-WazuhSpec($Spec, $Secret) {
    if (-not (Test-MonHost $Spec.address)) { return 'Invalid or missing manager address.' }
    if ((Test-MonPortInvalid $Spec.port) -or (Test-MonPortInvalid $Spec.enrollmentPort)) { return 'Invalid port.' }
    if ($null -ne $Spec.protocol -and @('tcp', 'udp') -cnotcontains $Spec.protocol) { return 'Protocol must be tcp or udp.' }
    if ($null -ne $Spec.enrollment -and @('keys', 'new') -cnotcontains $Spec.enrollment) { return "Enrollment must be 'keys' or 'new'." }
    if ($Spec.groups -and -not (Test-MonName $Spec.groups)) { return 'Invalid agent groups.' }
    if ($null -ne $Secret) {
        if (-not ($Secret -is [string]) -or $Secret.Length -gt 512 -or $Secret.Contains("`r") -or $Secret.Contains("`n")) { return 'Invalid enrollment password.' }
    }
    return $null
}

# $null = connected (or no state file and the service runs), otherwise the reason.
function Wait-WazuhConnected($Paths, [string]$Address, [scriptblock]$Log) {
    Send-MonitoringLog
    Start-Sleep -Seconds 10  # the old status stays in the file until the service rewrites it
    $until = [DateTime]::UtcNow.AddSeconds($WazuhConnectTimeoutSeconds)
    $last = $null
    while ([DateTime]::UtcNow -lt $until) {
        $state = Read-WazuhState $Paths
        if ($null -eq $state) {
            # OSSEC/old versions without state file: only check that the service runs
            if ((Test-WazuhRunning $Paths) -eq $true) {
                & $Log '[agent] No state file; the agent service is running (connection not verifiable).'
                return $null
            }
            return 'The Wazuh agent service is not running after the restart.'
        }
        if ((Get-WazuhConnected $state) -eq $true) { return $null }
        $value = Get-WazuhStateValue $state
        if ($value -cne $last) {
            & $Log "[agent] Agent status: $value"
            $last = $value
            Send-MonitoringLog
        }
        Start-Sleep -Seconds 5
    }
    $shown = 'unknown'
    if ($last) { $shown = $last }
    return "The agent did not connect to $Address within $([int]($WazuhConnectTimeoutSeconds / 60)) minutes (status: $shown)."
}

# Back up, rewrite ossec.conf, enroll if needed, restart, wait for "connected"; restore on failure.
function Set-WazuhTarget($Spec, $Secret, [scriptblock]$Log) {
    $paths = Get-WazuhPaths
    if ($null -eq $paths) { return New-MonResult 'Rejected' 'No Wazuh agent installed on this host.' }
    $invalid = Test-WazuhSpec $Spec $Secret
    if ($invalid) { return New-MonResult 'Rejected' $invalid }
    $address = [string]$Spec.address
    $version = Get-WazuhVersion $paths
    if ($null -ne $Spec.managerVersion -and (Compare-MonVersion $version $Spec.managerVersion) -gt 0) {
        return New-MonResult 'Rejected' ("The Wazuh agent ($(Get-MonVersionDisplay $version)) is newer than the manager ($($Spec.managerVersion)). " +
            'Wazuh requires the manager to be the same version or newer; install the matching agent version first.')
    }

    $read = Read-MonText $paths.config
    try { $info = Read-WazuhConfigInfo $read.text } catch { return New-MonResult 'Failed' $_.Exception.Message }
    if (-not $info.isAgent) { return New-MonResult 'Rejected' 'ossec.conf has no client section (Wazuh manager?).' }

    $newEnrollment = ($Spec.enrollment -ceq 'new')
    # <enrollment> exists since 4.0; before, agent-auth enrolls. Unknown version: assume 4.x.
    $enrollmentBlock = ($info.variant -ceq 'wazuh') -and ($null -eq $version -or (Compare-MonVersion $version '4.0.0') -ge 0)
    try {
        $rewritten = Update-WazuhConfigText $read.text $Spec $newEnrollment $enrollmentBlock
    } catch {
        return New-MonResult 'Rejected' $_.Exception.Message
    }

    $passFile = Get-WazuhPassFile $paths $info.passPath
    $backup = Backup-MonFiles 'wazuh' @($paths.config, $paths.keys, $passFile)
    & $Log "[agent] Backup of the Wazuh configuration: $($backup.folder)"

    $failure = $null
    try {
        Write-MonText $paths.config $rewritten $read.bom
        $portText = ''
        if ($null -ne $Spec.port) { $portText = ':' + [string]$Spec.port }
        if ($newEnrollment) { $modeText = ', new enrollment' } else { $modeText = ', keys kept' }
        & $Log "[agent] ossec.conf: manager $address$portText$modeText"
        if ($newEnrollment) {
            if (Test-Path -LiteralPath $paths.keys -PathType Leaf) {
                Write-MonText $paths.keys '' $false
                & $Log '[agent] client.keys emptied (the agent enrolls with the new manager).'
            }
            if ($Secret) {
                Write-MonText $passFile ([string]$Secret + "`n") $false
                & $Log '[agent] Enrollment password written (authd.pass).'
            }
            if (-not $enrollmentBlock) {
                # Before 4.0: enroll once with agent-auth (reads authd.pass itself)
                $authArgs = @('-m', $address)
                if ($null -ne $Spec.enrollmentPort) { $authArgs += @('-p', [string]$Spec.enrollmentPort) }
                if ($Spec.groups) { $authArgs += @('-G', [string]$Spec.groups) }
                $auth = Invoke-MonProcess $paths.agentAuth $authArgs
                if ($null -eq $auth -or $auth.code -ne 0) {
                    $authText = ''
                    if ($null -ne $auth) { $authText = ([string]$auth.err + [string]$auth.out).Trim() }
                    $failure = "Enrollment with agent-auth failed: $authText"
                } else {
                    & $Log '[agent] Enrolled with agent-auth.'
                }
            }
        }
    } catch {
        $failure = "Writing the configuration failed: $($_.Exception.Message)"
    }

    if (-not $failure -and -not (Restart-Wazuh $paths $Log)) { $failure = 'The Wazuh agent could not be restarted.' }
    if (-not $failure) { $failure = Wait-WazuhConnected $paths $address $Log }
    if ($failure) {
        & $Log "[agent] $failure"
        Restore-MonBackup $backup $Log
        [void](Restart-Wazuh $paths $Log)
        return New-MonResult 'Failed' "$failure The previous configuration was restored."
    }
    return New-MonResult 'Succeeded' "Wazuh agent connected to $address."
}

# --- Checkmk agent ---

function Get-CheckmkPaths {
    $programs = Get-ProgramFilesX86
    if (-not $programs) { return $null }
    $service = Join-Path $programs 'checkmk\service'
    $agent = Join-Path $service 'check_mk_agent.exe'
    if (Test-Path -LiteralPath $agent -PathType Leaf) {
        $controller = Join-Path $service 'cmk-agent-ctl.exe'
        if (-not (Test-Path -LiteralPath $controller -PathType Leaf)) { $controller = $null }
        return @{ agent = $agent; controller = $controller; userYaml = (Join-Path $env:ProgramData 'checkmk\agent\check_mk.user.yml'); ini = $null }
    }
    $legacy = Join-Path $programs 'check_mk'
    $agent = Join-Path $legacy 'check_mk_agent.exe'
    if (Test-Path -LiteralPath $agent -PathType Leaf) {
        return @{ agent = $agent; controller = $null; userYaml = $null; ini = (Join-Path $legacy 'check_mk.ini') }
    }
    return $null
}

function Get-CheckmkVariant($Paths) {
    if ($Paths.controller) { return 'checkmk-ctl' }
    if ($Paths.ini) { return 'checkmk-ini' }
    return 'checkmk-legacy'
}

# Values of only_from: "a b", "a,b", "[a, b]", quoted; "~" and "null" are empty.
function Split-CheckmkValue([string]$Value) {
    $trimmed = $Value.Trim().Trim([char[]]'"''')
    if ($trimmed.StartsWith('[', [StringComparison]::Ordinal) -and $trimmed.EndsWith(']', [StringComparison]::Ordinal)) {
        $trimmed = $trimmed.Substring(1, $trimmed.Length - 2)
    }
    $result = New-Object Collections.Generic.List[string]
    foreach ($v in $trimmed.Split([char[]]@(' ', "`t", ','), [StringSplitOptions]::RemoveEmptyEntries)) {
        $v = $v.Trim([char[]]'"''')
        if ($v.Length -gt 0 -and $v -cne '~' -and $v -cne 'null') { $result.Add($v) }
    }
    return $result.ToArray()
}

function Get-CheckmkLines([string]$Text) {
    $list = New-Object Collections.Generic.List[string]
    foreach ($l in $Text.Replace("`r`n", "`n").Split([char]10)) { $list.Add($l) }
    return , $list
}

function Get-CheckmkIndent([string]$Line) { return $Line.Length - $Line.TrimStart([char]32).Length }

function Test-CheckmkContent([string]$Line) {
    return $Line.Trim().Length -gt 0 -and -not $Line.TrimStart().StartsWith('#', [StringComparison]::Ordinal)
}

function Find-CheckmkGlobal($Lines) {
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -cmatch '^global:\s*(#.*)?\z') { return $i } }
    return $null
}

# Lines of the global: block (up to the next content line without indentation).
function Get-CheckmkBlock($Lines, [int]$GlobalLine) {
    $result = New-Object Collections.Generic.List[int]
    for ($i = $GlobalLine + 1; $i -lt $Lines.Count; $i++) {
        if ((Test-CheckmkContent $Lines[$i]) -and (Get-CheckmkIndent $Lines[$i]) -eq 0) { break }
        $result.Add($i)
    }
    return , $result
}

function Get-CheckmkChildIndent($Lines, [int]$GlobalLine) {
    foreach ($i in (Get-CheckmkBlock $Lines $GlobalLine)) {
        if (Test-CheckmkContent $Lines[$i]) { return (Get-CheckmkIndent $Lines[$i]) }
    }
    return $null
}

function Find-CheckmkOnlyFrom($Lines, [int]$GlobalLine) {
    $indent = Get-CheckmkChildIndent $Lines $GlobalLine
    if ($null -eq $indent) { return $null }
    foreach ($i in (Get-CheckmkBlock $Lines $GlobalLine)) {
        if ((Get-CheckmkIndent $Lines[$i]) -eq $indent -and $Lines[$i] -cmatch '^\s+only_from:\s*(?<value>.*)\z') { return @{ line = $i; indent = $indent } }
    }
    return $null
}

function Read-CheckmkYamlOnlyFrom([string]$Text) {
    $lines = Get-CheckmkLines $Text
    $globalLine = Find-CheckmkGlobal $lines
    if ($null -eq $globalLine) { return }
    $entry = Find-CheckmkOnlyFrom $lines $globalLine
    if ($null -eq $entry) { return }
    $m = [regex]::Match($lines[$entry.line], '^\s+only_from:\s*(?<value>.*)\z')
    $values = New-Object Collections.Generic.List[string]
    foreach ($v in @(Split-CheckmkValue ($m.Groups['value'].Value.Split([char]35)[0]))) { $values.Add($v) }
    for ($i = $entry.line + 1; $i -lt $lines.Count -and $lines[$i].Trim().StartsWith('-', [StringComparison]::Ordinal) -and (Get-CheckmkIndent $lines[$i]) -ge $entry.indent; $i++) {
        foreach ($v in @(Split-CheckmkValue ($lines[$i].Trim().Substring(1)))) { $values.Add($v) }
    }
    return $values.ToArray()
}

# Set global: only_from; global: is appended when missing. The rest of the file stays unchanged.
function Update-CheckmkYaml([string]$Text, [string[]]$OnlyFrom) {
    $value = $OnlyFrom -join ' '
    $lines = Get-CheckmkLines $Text
    if ($Text.Contains("`r`n")) { $newline = "`r`n" } else { $newline = "`n" }
    $globalLine = Find-CheckmkGlobal $lines
    if ($null -eq $globalLine) {
        $prefix = $Text
        if ($Text.Length -gt 0 -and -not $Text.EndsWith("`n", [StringComparison]::Ordinal)) { $prefix = $Text + $newline }
        return $prefix + 'global:' + $newline + '    only_from: ' + $value + $newline
    }
    $entry = Find-CheckmkOnlyFrom $lines $globalLine
    if ($null -ne $entry) {
        # remove list lines ("- 10.0.0.1") below only_from
        $end = $entry.line + 1
        while ($end -lt $lines.Count -and $lines[$end].Trim().StartsWith('-', [StringComparison]::Ordinal) -and (Get-CheckmkIndent $lines[$end]) -ge $entry.indent) { $end++ }
        $lines.RemoveRange($entry.line, $end - $entry.line)
        $lines.Insert($entry.line, (' ' * $entry.indent) + 'only_from: ' + $value)
    } else {
        $indent = Get-CheckmkChildIndent $lines $globalLine
        if ($null -eq $indent) { $indent = 4 }
        $lines.Insert($globalLine + 1, (' ' * $indent) + 'only_from: ' + $value)
    }
    return [string]::Join($newline, $lines.ToArray())
}

function Find-CheckmkUuid($Element, [int]$Depth) {
    if ($Depth -gt 3 -or -not ($Element -is [System.Management.Automation.PSCustomObject])) { return $null }
    $own = $Element.PSObject.Properties['uuid']
    if ($null -ne $own -and $own.Value -is [string] -and $own.Value -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z') { return $own.Value }
    foreach ($p in $Element.PSObject.Properties) {
        $nested = Find-CheckmkUuid $p.Value ($Depth + 1)
        if ($nested) { return $nested }
    }
    return $null
}

function Add-CheckmkConnections($Element, $Found, [int]$Depth) {
    if ($Depth -gt 8 -or $null -eq $Element) { return }
    if ($Element -is [array]) {
        foreach ($item in $Element) { Add-CheckmkConnections $item $Found ($Depth + 1) }
        return
    }
    if (-not ($Element -is [System.Management.Automation.PSCustomObject])) { return }
    $siteId = $Element.PSObject.Properties['site_id']
    if ($null -ne $siteId -and $siteId.Value -is [string]) {
        $own = Find-CheckmkUuid $Element 0
        if ($own) { [void]$Found.Add(@{ siteId = $siteId.Value; uuid = $own }); return }
    }
    foreach ($p in $Element.PSObject.Properties) {
        if ($p.Name -cmatch '^[^/\s]+/[A-Za-z0-9_\-]+\z' -and $p.Value -is [System.Management.Automation.PSCustomObject]) {
            $uuid = Find-CheckmkUuid $p.Value 0
            if ($uuid) { [void]$Found.Add(@{ siteId = $p.Name; uuid = $uuid }); continue }
        }
        Add-CheckmkConnections $p.Value $Found ($Depth + 1)
    }
}

# Connections from "cmk-agent-ctl status --json", read tolerantly: objects with "site_id" and "uuid" and
# entries whose key is "server/site" with a "uuid" below.
function Get-CheckmkConnectionList([string]$Json) {
    $result = New-Object Collections.ArrayList
    try { $data = ConvertFrom-Json -InputObject $Json } catch { return , $result }
    $found = New-Object Collections.ArrayList
    Add-CheckmkConnections $data $found 0
    $seen = @{}
    foreach ($c in $found) {
        if (-not $seen.ContainsKey($c.uuid)) { $seen[$c.uuid] = $true; [void]$result.Add($c) }
    }
    return , $result
}

# Server without port ("cmk.example.com:8000" -> "cmk.example.com", "[::1]:8000" -> "::1").
function Get-CheckmkHost([string]$Server) {
    if ($Server.StartsWith('[', [StringComparison]::Ordinal)) {
        $end = $Server.IndexOf(']')
        if ($end -gt 0) { return $Server.Substring(1, $end - 1) }
        return $Server
    }
    if ($Server.Split(':').Count -eq 2) { return $Server.Substring(0, $Server.IndexOf(':')) }
    return $Server
}

function Test-CheckmkMatch($Connection, [string]$Server, [string]$Site) {
    $siteId = [string]$Connection.siteId
    $ownServer = $siteId.Split('/')[0]
    $ownSite = ''
    if ($siteId.Contains('/')) { $ownSite = $siteId.Substring($siteId.LastIndexOf('/') + 1) }
    return ([string]::Equals((Get-CheckmkHost $ownServer), (Get-CheckmkHost $Server), [StringComparison]::OrdinalIgnoreCase) -and $ownSite -ceq $Site)
}

function Get-CheckmkConnections($Paths) {
    $r = Invoke-MonProcess $Paths.controller @('status', '--json')
    if ($null -eq $r -or $r.code -ne 0) {
        $err = ''
        if ($null -ne $r) { $err = ([string]$r.err).Trim() }
        return @{ connections = (New-Object Collections.ArrayList); error = "cmk-agent-ctl status failed: $err" }
    }
    return @{ connections = (Get-CheckmkConnectionList $r.out); error = $null }
}

function Get-CheckmkVersion($Paths) {
    $entry = Get-MonUninstallEntry { param($n) Test-CheckmkDisplayName $n }
    if ($null -ne $entry) { return $entry.version }
    return $null
}

function Test-CheckmkRunning($Paths) {
    $name = Find-MonServiceForImage (Split-Path -Parent $Paths.agent) 'check_mk_agent.exe'
    if ($null -eq $name) { return $null }
    return Test-MonServiceRunning $name
}

function Restart-Checkmk($Paths, [scriptblock]$Log) {
    $name = Find-MonServiceForImage (Split-Path -Parent $Paths.agent) 'check_mk_agent.exe'
    if ($null -eq $name) { return $false }
    return Restart-MonService $name $Log
}

function Get-CheckmkClient {
    $paths = Get-CheckmkPaths
    if ($null -eq $paths) { return $null }
    $version = Get-MonVersionDisplay (Get-CheckmkVersion $paths)
    $running = (Test-CheckmkRunning $paths) -eq $true
    $variant = Get-CheckmkVariant $paths
    if ($paths.controller) {
        $c = Get-CheckmkConnections $paths
        $ids = New-Object Collections.Generic.List[string]
        foreach ($conn in $c.connections) { $ids.Add([string]$conn.siteId) }
        if ($c.error) { $detail = $c.error }
        elseif ($ids.Count -eq 0) { $detail = 'not registered (legacy pull mode)' }
        else { $detail = 'registered with ' + ($ids.ToArray() -join ', ') }
        $connected = $null
        if (-not $c.error) { $connected = ($ids.Count -gt 0) }
        return New-MonClient 'checkmk' $variant $version $running $ids.ToArray() $connected $detail $true $false
    }
    if ($paths.ini) {
        return New-MonClient 'checkmk' $variant $version $running @() $null 'Checkmk 1.x agent (check_mk.ini): AIOPatcher does not change its configuration. Update the client.' $false $false
    }
    if ($paths.userYaml) {
        $onlyFrom = @()
        if (Test-Path -LiteralPath $paths.userYaml -PathType Leaf) { $onlyFrom = @(Read-CheckmkYamlOnlyFrom (Read-MonText $paths.userYaml).text) }
        return New-MonClient 'checkmk' $variant $version $running $onlyFrom $null 'legacy pull mode' $true $false
    }
    return New-MonClient 'checkmk' $variant $version $running @() $null 'Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client.' $false $false
}

function Test-CheckmkSpec($Paths, $Spec, $Secret) {
    if ($Paths.ini) { return 'Checkmk 1.x agent (check_mk.ini): AIOPatcher does not change its configuration. Update the client first.' }
    if ($Paths.controller) {
        if (-not (Test-MonHost $Spec.server) -or -not (Test-MonName $Spec.site) -or -not (Test-MonName $Spec.user) -or -not (Test-MonName $Spec.hostname)) {
            return 'Server, site, registration user and host name are required for the agent controller.'
        }
        if (-not ($Secret -is [string]) -or $Secret.Length -eq 0 -or $Secret.Contains("`r") -or $Secret.Contains("`n")) { return 'The registration password is missing.' }
        return $null
    }
    $onlyFrom = @($Spec.onlyFrom | Where-Object { $null -ne $_ })
    $invalidSources = @($onlyFrom | Where-Object { -not (Test-MonSource $_) })
    if ($null -eq $Spec.onlyFrom -or $onlyFrom.Count -eq 0 -or $onlyFrom.Count -gt 50 -or $invalidSources.Count -gt 0) {
        return 'Legacy agents need the IP addresses of the new site (only_from).'
    }
    if (-not $Paths.userYaml) { return 'Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client.' }
    return $null
}

# Register with the new site, then remove the other connections. If registering fails nothing was changed.
function Register-Checkmk($Paths, $Spec, [string]$Secret, [scriptblock]$Log) {
    $cmkServer = [string]$Spec.server
    $cmkSite = [string]$Spec.site
    $cmkHost = [string]$Spec.hostname
    $cmkUser = [string]$Spec.user
    $before = (Get-CheckmkConnections $Paths).connections
    & $Log "[agent] Registering with $cmkServer/$cmkSite as $cmkHost (user $cmkUser)."
    # The password is briefly visible in the process list: cmk-agent-ctl otherwise only reads it from a terminal.
    $r = Invoke-MonProcess $Paths.controller @('register', '--hostname', $cmkHost, '--server', $cmkServer, '--site', $cmkSite,
        '--user', $cmkUser, '--password', $Secret, '--trust-cert') 180
    if ($null -eq $r -or $r.code -ne 0) {
        $message = ''
        if ($null -ne $r) { $message = ([string]$r.err + ' ' + [string]$r.out).Replace($Secret, '***').Trim() }
        return New-MonResult 'Failed' "Registration with $cmkServer/$cmkSite failed (the host must exist in the site): $message. Nothing was changed."
    }
    $after = Get-CheckmkConnections $Paths
    $matching = @($after.connections | Where-Object { Test-CheckmkMatch $_ $cmkServer $cmkSite })
    if ($after.error -or $matching.Count -eq 0) {
        return New-MonResult 'Failed' ("Registration reported success, but no connection to $cmkServer/$cmkSite is listed. $($after.error)").Trim()
    }
    foreach ($old in @($after.connections | Where-Object { -not (Test-CheckmkMatch $_ $cmkServer $cmkSite) })) {
        $d = Invoke-MonProcess $Paths.controller @('delete', [string]$old.uuid)
        if ($null -ne $d -and $d.code -eq 0) { & $Log "[agent] Old connection $($old.siteId) removed." }
        else {
            $dErr = ''
            if ($null -ne $d) { $dErr = ([string]$d.err).Trim() }
            & $Log "[agent] Removing $($old.siteId) failed: $dErr"
        }
    }
    $replaced = @($before | Where-Object { -not (Test-CheckmkMatch $_ $cmkServer $cmkSite) } | ForEach-Object { $_.siteId })
    $summary = "Registered with $cmkServer/$cmkSite as $cmkHost."
    if ($replaced.Count -gt 0) { $summary += " Removed: $($replaced -join ', ')." }
    return New-MonResult 'Succeeded' $summary
}

function Set-CheckmkOnlyFrom($Paths, [string[]]$OnlyFrom, [scriptblock]$Log) {
    $file = $Paths.userYaml
    $backup = Backup-MonFiles 'checkmk' @($file)
    & $Log "[agent] Backup of ${file}: $($backup.folder)"
    try {
        $text = ''
        $bom = $false
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $read = Read-MonText $file
            $text = $read.text
            $bom = $read.bom
        }
        $rewritten = Update-CheckmkYaml $text $OnlyFrom
        $dir = Split-Path -Parent $file
        if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Force -Path $dir) }
        Write-MonText $file $rewritten $bom
        & $Log "[agent] only_from = $($OnlyFrom -join ' ')"
    } catch {
        Restore-MonBackup $backup $Log
        return New-MonResult 'Failed' "Writing $file failed: $($_.Exception.Message)"
    }
    if (-not (Restart-Checkmk $Paths $Log)) {
        Restore-MonBackup $backup $Log
        [void](Restart-Checkmk $Paths $Log)
        return New-MonResult 'Failed' 'The Checkmk agent could not be restarted. The previous configuration was restored.'
    }
    return New-MonResult 'Succeeded' "only_from set to $($OnlyFrom -join ' '). Whether the new site can query the agent cannot be checked from the host."
}

function Set-CheckmkTarget($Spec, $Secret, [scriptblock]$Log) {
    $paths = Get-CheckmkPaths
    if ($null -eq $paths) { return New-MonResult 'Rejected' 'No Checkmk agent installed on this host.' }
    $invalid = Test-CheckmkSpec $paths $Spec $Secret
    if ($invalid) { return New-MonResult 'Rejected' $invalid }
    $version = Get-CheckmkVersion $paths
    if ($null -ne $Spec.siteVersion -and (Compare-MonVersionMinor $version $Spec.siteVersion) -gt 0) {
        & $Log "[agent] Warning: the agent ($(Get-MonVersionDisplay $version)) is newer than the site ($($Spec.siteVersion)); some services may show UNKNOWN."
    }
    if ($paths.controller) { return Register-Checkmk $paths $Spec ([string]$Secret) $Log }
    return Set-CheckmkOnlyFrom $paths ([string[]]@($Spec.onlyFrom)) $Log
}

# --- Install / uninstall ---

function Get-MonSha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '') } finally { $stream.Dispose(); $sha.Dispose() }
}

function Install-MonitoringClient($Spec, $Secret, [scriptblock]$Log) {
    $package = $Spec.package
    if ($null -eq $package -or -not ($package.fileName -is [string]) -or $package.fileName -notmatch '^[A-Za-z0-9._+~\-]{1,120}\.(deb|msi)\z' -or
        -not ($package.sha256 -is [string]) -or $package.sha256 -notmatch '^[0-9a-fA-F]{64}\z' -or
        -not ($package.id -is [string]) -or $package.id -notmatch '^[A-Za-z0-9._\-]{1,128}\z' -or $package.id.Contains('..')) {
        return New-MonResult 'Rejected' 'No valid package given.'
    }
    $fileName = [string]$package.fileName
    if (-not $fileName.EndsWith('.msi', [StringComparison]::OrdinalIgnoreCase)) { return New-MonResult 'Rejected' "Package $fileName does not fit this system." }
    $product = [string]$Spec.product
    if ($product -ceq 'wazuh') {
        $invalid = Test-WazuhSpec $Spec $Secret
        if ($invalid) { return New-MonResult 'Rejected' $invalid }
        $parts = Get-MonVersionParts $package.version
        if ($null -ne $parts -and $parts[0] -ge 5 -and [Environment]::OSVersion.Version -lt (New-Object Version(6, 3))) {
            return New-MonResult 'Rejected' ('Wazuh 5 agents do not support this operating system (needs Windows 10 / Server 2012 R2, Debian 10 or Ubuntu 20.04 or newer). ' +
                'Keep a 4.x manager for these hosts.')
        }
    } elseif ($product -cne 'checkmk') {
        return New-MonResult 'Rejected' "Unknown monitoring product $product."
    }

    $folder = Join-Path $StateDir 'monitoring-downloads'
    [void](New-Item -ItemType Directory -Force -Path $folder)
    $file = Join-Path $folder $fileName
    $version = [string]$package.version
    try {
        $size = 0
        if ($package.size -is [int] -or $package.size -is [long]) { $size = [long]$package.size }
        $megabytes = ([Math]::Floor($size / 1024) / 1024.0).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)
        & $Log "[agent] Downloading $fileName ($megabytes MB) ..."
        Send-MonitoringLog
        Save-AgentFile ($MonitoringPackagePath + $package.id) $file
        if ((Get-MonSha256 $file) -ne $package.sha256) {
            return New-MonResult 'Failed' 'SHA-256 of the downloaded package does not match. Nothing was installed.'
        }
        if ($product -ceq 'wazuh') {
            $signer = $null
            $signature = Get-AuthenticodeSignature -LiteralPath $file
            if ($signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate) { $signer = $signature.SignerCertificate.Subject }
            if (-not $signer -or $signer.IndexOf('Wazuh', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                $shown = 'none'
                if ($signer) { $shown = $signer }
                return New-MonResult 'Failed' "The package is not signed by Wazuh (signer: $shown). Nothing was installed."
            }
        }

        if ($product -ceq 'wazuh') { $fresh = $null -eq (Get-WazuhPaths) } else { $fresh = $null -eq (Get-CheckmkPaths) }
        $installError = Invoke-MonMsiExec @('/i', $file, '/qn', '/norestart') $Log
        if ($installError) { return New-MonResult 'Failed' $installError }
        if ($fresh) { & $Log "[agent] Installed $fileName." } else { & $Log "[agent] Updated $fileName." }

        # Freshly installed: Wazuh has no key yet, so enroll with the manager
        $target = $Spec
        if ($product -ceq 'wazuh' -and $fresh) {
            $target = $Spec.PSObject.Copy()
            $target | Add-Member -NotePropertyName 'enrollment' -NotePropertyValue 'new' -Force
        }
        if ($product -ceq 'wazuh') { $configured = Set-WazuhTarget $target $Secret $Log } else { $configured = Set-CheckmkTarget $target $Secret $Log }
        if ($configured.outcome -eq 'Succeeded') {
            if ($fresh) { $verb = 'Installed' } else { $verb = 'Updated to' }
            return New-MonResult 'Succeeded' "$verb $product $version. $($configured.summary)"
        }
        return New-MonResult 'Failed' "$product $version is installed, but configuring it failed: $($configured.summary)"
    } catch {
        return New-MonResult 'Failed' "Installing $fileName failed: $($_.Exception.Message)"
    } finally {
        try { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } } catch { }
    }
}

function Uninstall-MonitoringClient([string]$Product, [scriptblock]$Log) {
    if ($Product -ceq 'wazuh') {
        $paths = Get-WazuhPaths
        if ($null -eq $paths) { return New-MonResult 'Rejected' 'No Wazuh agent installed on this host.' }
        $keep = @($paths.config, $paths.keys, $paths.defaultPass)
        $nameMatch = { param($n) $n.StartsWith('Wazuh Agent', [StringComparison]::OrdinalIgnoreCase) }
    } elseif ($Product -ceq 'checkmk') {
        $paths = Get-CheckmkPaths
        if ($null -eq $paths) { return New-MonResult 'Rejected' 'No Checkmk agent installed on this host.' }
        $keep = @(@($paths.userYaml, $paths.ini) | Where-Object { $_ })
        if ($paths.controller) {
            $r = Invoke-MonProcess $paths.controller @('delete-all')
            if ($null -ne $r -and $r.code -eq 0) { & $Log '[agent] Registrations removed (cmk-agent-ctl delete-all).' }
        }
        $nameMatch = { param($n) Test-CheckmkDisplayName $n }
    } else {
        return New-MonResult 'Rejected' "Unknown monitoring product $Product."
    }

    $backup = Backup-MonFiles $Product $keep
    & $Log "[agent] Configuration saved to $($backup.folder) (for a later reinstall with the same identity)."
    $entry = Get-MonUninstallEntry $nameMatch
    if ($null -eq $entry -or -not $entry.productCode) { return New-MonResult 'Failed' 'No Windows Installer entry found; uninstall the client manually.' }
    $uninstallError = Invoke-MonMsiExec @('/x', $entry.productCode, '/qn', '/norestart') $Log
    if ($uninstallError) { return New-MonResult 'Failed' $uninstallError }

    if ($Product -ceq 'wazuh') { $left = $null -ne (Get-WazuhPaths) } else { $left = $null -ne (Get-CheckmkPaths) }
    if ($left) { return New-MonResult 'Failed' "The $Product client was uninstalled, but files or the package are still present. Check the host." }
    if ($Product -ceq 'wazuh') { $where = 'Wazuh manager' } else { $where = 'Checkmk site' }
    return New-MonResult 'Succeeded' "$Product client uninstalled. The host stays listed in the $where until removed there."
}

function Invoke-MonitoringStatus($Policy, [scriptblock]$Log) {
    $allowed = ($Policy.allowMonitoringChanges -is [bool]) -and $Policy.allowMonitoringChanges
    $clients = New-Object Collections.ArrayList
    foreach ($detect in @('Get-WazuhClient', 'Get-CheckmkClient')) {
        try {
            $client = & $detect
            if ($null -ne $client) { [void]$clients.Add($client) }
        } catch {
            & $Log "[agent] Detecting a monitoring client failed: $($_.Exception.Message)"
        }
    }
    $summary = 'no monitoring clients'
    if ($clients.Count -gt 0) {
        $names = New-Object Collections.Generic.List[string]
        foreach ($c in $clients) { $names.Add(("$($c.product) $($c.version)").Trim()) }
        $summary = $names.ToArray() -join ', '
    }
    return @{ outcome = 'Succeeded'; summary = $summary; payload = @{ clients = $clients.ToArray(); changesAllowed = [bool]$allowed } }
}

function Invoke-MonitoringChange($Change, $Policy, [scriptblock]$Log) {
    $allowed = ($Policy.allowMonitoringChanges -is [bool]) -and $Policy.allowMonitoringChanges
    if ($null -eq $Change -or -not ($Change.monitoring -is [System.Management.Automation.PSCustomObject]) -or $MonitoringKinds -cnotcontains [string]$Change.kind) {
        return New-MonResult 'Rejected' 'Change does not belong to module monitoring.'
    }
    if (-not $allowed) {
        & $Log "[agent] Rejected: $MonitoringPolicyReject"
        return New-MonResult 'Rejected' $MonitoringPolicyReject
    }
    $spec = $Change.monitoring
    $secret = $Change.secret
    $kind = [string]$Change.kind
    $product = [string]$spec.product
    & $Log "[agent] ${kind}: $product"
    if ($kind -ceq 'MonitoringConfigure' -and $product -ceq 'wazuh') { return Set-WazuhTarget $spec $secret $Log }
    if ($kind -ceq 'MonitoringConfigure' -and $product -ceq 'checkmk') { return Set-CheckmkTarget $spec $secret $Log }
    if ($kind -ceq 'MonitoringInstall') { return Install-MonitoringClient $spec $secret $Log }
    if ($kind -ceq 'MonitoringUninstall') { return Uninstall-MonitoringClient $product $Log }
    return New-MonResult 'Rejected' "Unknown monitoring product $product."
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
            'monitoring.status' { $r = Invoke-MonitoringStatus $policy $log }
            'monitoring.change' {
                $script:MonTaskId = $id
                $script:MonLines = $lines
                try { $r = Invoke-MonitoringChange $Task.payload $policy $log }
                finally { $script:MonTaskId = $null; $script:MonLines = $null }
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
