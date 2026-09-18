#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
# This is the GCU payload installer the Inno Setup package runs AFTER copying files.
# It selects the vendor payload matching the local GPU generation, copies it under
# %ProgramFiles%\L-Mechrevo\GCU, verifies Authenticode, installs the UWACPI driver,
# registers + starts the GCUBridge service and adds the MQTT blocking firewall rule.
# The whole step is idempotent: re-running repairs instead of failing.
<#
.SYNOPSIS
    Install the generation-matched GCU vendor payload bundled by L-Mechrevo.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-Gcu.ps1 `
        -PayloadRoot "C:\Program Files\L-Mechrevo\GCU\payload" `
        -TargetDir  "C:\Program Files\L-Mechrevo\GCU"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StagingRoot,
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [ValidateSet('Auto', '50', '40-51749', '40-51751')][string]$Variant = 'Auto',
    [string]$LogDir,
    [string]$UninstallScriptPath,
    [string]$InstallerVersion,
    [switch]$NoStart,
    [switch]$SinglePayload,
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:RuleName = 'L-Mechrevo - Block remote GCU MQTT'
# Every bundled vendor payload registers the same service name, so the "legacy registration" is
# the same name installed by the vendor install.bat with a different binPath; the uninstall-first
# step removes it by name before the new payload is registered.
$script:ServiceName = 'GCUBridge'
$script:LegacyServiceNames = @('GCUBridge')
$script:LogFile = $null

function Write-Log {
    param([string]$Message)
    $line = ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message)
    if ($script:LogFile) {
        Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
    }
    Write-Host $line
}

function Assert-SignedFile {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("Missing required file ({0}): {1}" -f $Label, $Path)
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    if ([string]$sig.Status -ne 'Valid') {
        throw ("Authenticode verification failed ({0}): {1} -> {2}" -f $Label, $Path, $sig.Status)
    }
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }
    Write-Log ("  signature OK ({0}): {1} [{2}]" -f $Label, (Split-Path $Path -Leaf), $subject)
}

function Copy-Tree {
    param([string]$Source, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Source)) {
        throw ("Payload source directory not found: {0}" -f $Source)
    }
    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
    $robocopy = Join-Path $env:SystemRoot 'System32\robocopy.exe'
    if (Test-Path -LiteralPath $robocopy) {
        Write-Log ("  robocopy `"{0}`" -> `"{1}`"" -f $Source, $Destination)
        & $robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $code = $LASTEXITCODE
        if ($code -ge 8) {
            throw ("robocopy failed ({0}) copying {1} -> {2}" -f $code, $Source, $Destination)
        }
    }
    else {
        Write-Log ("  Copy-Item -Recurse `"{0}`" -> `"{1}`"" -f $Source, $Destination)
        Copy-Item -LiteralPath (Join-Path $Source '*') -Destination $Destination -Recurse -Force
    }
}

function Get-ServiceBinPath {
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $script:ServiceName) -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    return [string]$service.PathName
}

function Invoke-PriorGenerationUninstall {
    # Uninstall-first: remove the current/legacy GCUBridge registration (the vendor install.bat
    # registers the same name) and the firewall rule before the new payload is copied, so two
    # generations cannot coexist. A failure here aborts the install instead of stacking a second
    # service on top of a stale one.
    param(
        [string]$TargetDir,
        [string]$UninstallScriptPath,
        [string]$LogDir,
        [switch]$DryRun
    )
    $scriptPath = if (-not [string]::IsNullOrWhiteSpace($UninstallScriptPath)) {
        $UninstallScriptPath
    }
    else {
        Join-Path $PSScriptRoot 'Uninstall-Gcu.ps1'
    }
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw ("prior-generation uninstall script not found: {0}; aborting install" -f $scriptPath)
    }
    $powershellExe = Join-Path $PSHOME 'powershell.exe'
    $uninstallArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath, '-TargetDir', $TargetDir, '-KeepDriver')
    if (-not [string]::IsNullOrWhiteSpace($LogDir)) { $uninstallArgs += @('-LogDir', $LogDir) }
    if ($DryRun) { $uninstallArgs += '-DryRun' }
    Write-Log ('  uninstall-first: {0} {1}' -f $scriptPath, ($uninstallArgs -join ' '))
    & $powershellExe @uninstallArgs
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw ("prior-generation uninstall failed (exit {0}); aborting install" -f $code)
    }
    Write-Log '  prior-generation service/registration removed'
}

function Assert-Port13688Free {
    # A single owner: after the prior service is gone, nothing may still be listening on 13688.
    $cmd = Get-Command -Name Get-NetTCPConnection -ErrorAction SilentlyContinue
    if (-not $cmd) { return }
    $owners = @(Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -gt 0) {
        $pid0 = $owners[0]
        $proc = Get-Process -Id $pid0 -ErrorAction SilentlyContinue
        throw ("TCP 13688 is still owned by PID {0} ({1}) after the prior-generation uninstall; aborting install" -f $pid0, $(if ($proc) { $proc.ProcessName } else { 'unknown' }))
    }
}

function Test-ExpectedBinPath {
    param([string]$PathName, [string]$ExpectedExe)
    if ([string]::IsNullOrWhiteSpace($PathName)) { return $false }
    $actual = $PathName.Trim().Trim('"')
    return ($actual.TrimEnd('"') -ieq $ExpectedExe)
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("file not found for hashing: {0}" -f $Path)
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-GcuPayloadIdentity {
    # Identity is the SHA256 of the service binary. The FileVersion is deliberately NOT part of
    # identity: the payloads do not share a version line (1.2.0.0 vs 1.0.2.70), and same-named
    # files can share a version while differing by hash.
    param([Parameter(Mandatory = $true)][string]$ServiceDir)
    $exe = Join-Path (Join-Path $ServiceDir 'MyControlCenter') 'GCUService.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        $exe = Join-Path $ServiceDir 'GCUService.exe'
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        throw ("GCUService.exe not found under payload service dir: {0}" -f $ServiceDir)
    }
    return [pscustomobject]@{ Sha256 = (Get-FileSha256 -Path $exe); ExePath = $exe }
}

function Compare-PayloadIdentity {
    # Hash-only comparison. There is intentionally no version parameter: version numbers cannot
    # order or identify these payloads (unshared version line, same-name/different-hash).
    [CmdletBinding()]
    param(
        [string]$InstalledSha256,
        [string]$IncomingSha256
    )
    if ([string]::IsNullOrWhiteSpace($InstalledSha256)) { return 'NotInstalled' }
    if ($InstalledSha256 -ieq $IncomingSha256) { return 'Same' }
    return 'Different'
}

function Get-InstalledMarker {
    $key = 'HKLM:\SOFTWARE\L-Mechrevo'
    $marker = [pscustomobject]@{ GcuVariant = $null; GcuPayloadSha256 = $null; GcuInstallerVersion = $null }
    if (-not (Test-Path -LiteralPath $key)) { return $marker }
    foreach ($name in @('GcuVariant', 'GcuPayloadSha256', 'GcuInstallerVersion')) {
        try {
            $value = (Get-ItemProperty -LiteralPath $key -Name $name -ErrorAction Stop).$name
            if ($null -ne $value) { $marker.$name = [string]$value }
        }
        catch { }
    }
    return $marker
}

function Test-InstallerDowngrade {
    # Ordering uses the installer/app version line (shared and monotonic), never the GCU service
    # FileVersion, which the payloads do not share (1.2.0.0 vs 1.0.2.70).
    [CmdletBinding()]
    param(
        [string]$InstalledVersion,
        [string]$IncomingVersion
    )
    if ([string]::IsNullOrWhiteSpace($InstalledVersion)) { return 'NoInstalled' }
    if ([string]::IsNullOrWhiteSpace($IncomingVersion)) { return 'UnknownIncoming' }
    $installed = $null
    $incoming = $null
    if (-not [System.Version]::TryParse($InstalledVersion, [ref]$installed)) { return 'UnknownInstalled' }
    if (-not [System.Version]::TryParse($IncomingVersion, [ref]$incoming)) { return 'UnknownIncoming' }
    if ($installed -gt $incoming) { return 'Downgrade' }
    return 'NotOlder'
}

function Get-InstallAction {
    # Idempotency: an already-current install whose recorded payload identity matches the bundled
    # one needs only verification, never a re-copy or re-registration.
    [CmdletBinding()]
    param(
        [bool]$AlreadyCurrent,
        [string]$InstalledSha256,
        [string]$IncomingSha256
    )
    if (-not $AlreadyCurrent) { return 'Install' }
    if ((Compare-PayloadIdentity -InstalledSha256 $InstalledSha256 -IncomingSha256 $IncomingSha256) -eq 'Same') { return 'VerifyOnly' }
    return 'Install'
}

function Install-UwacpiDriver {
    param([string]$DriverDir)
    $inf = Join-Path $DriverDir 'UWACPIDriver.inf'
    if (-not (Test-Path -LiteralPath $inf)) {
        throw ("UWACPIDriver.inf not found: {0}" -f $inf)
    }
    Write-Log ("  pnputil /add-driver `"{0}`" /install" -f $inf)
    $output = & pnputil.exe /add-driver $inf /install 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Log ("    " + [string]$_) }
    if ($code -ne 0) {
        # Idempotency: an already-present driver can make pnputil return non-zero.
        $enum = & pnputil.exe /enum-drivers 2>&1
        $present = ($enum | Select-String -SimpleMatch 'uwacpidriver.inf' -Quiet)
        if ($present) {
            Write-Log ("  driver already present in the driver store (pnputil exit {0}); continuing" -f $code)
        }
        else {
            throw ("pnputil /add-driver failed with exit code {0}" -f $code)
        }
    }
    else {
        Write-Log '  driver installed/updated OK'
    }
}

function Install-Service {
    param([string]$ServiceExe)
    if (-not (Test-Path -LiteralPath $ServiceExe)) {
        throw ("GCUBridge.exe not found: {0}" -f $ServiceExe)
    }
    $current = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $script:ServiceName) -ErrorAction SilentlyContinue
    if ($null -ne $current) {
        Write-Log ("  existing service found (state={0}); stopping and recreating to pin binPath" -f $current.State)
        Stop-Service -Name $script:ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $script:ServiceName | Out-Null
        $waited = 0
        while ((Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $script:ServiceName) -ErrorAction SilentlyContinue) -and $waited -lt 20) {
            Start-Sleep -Milliseconds 500
            $waited++
        }
    }
    Write-Log ("  New-Service {0} -> `"{1}`"" -f $script:ServiceName, $ServiceExe)
    New-Service -Name $script:ServiceName -BinaryPathName ('"' + $ServiceExe + '"') `
        -DisplayName $script:ServiceName -StartupType Automatic `
        -Description 'L-Mechrevo GCU MQTT broker and hardware service host.' | Out-Null
}

function Ensure-FirewallRule {
    $cmd = Get-Command -Name New-NetFirewallRule -ErrorAction SilentlyContinue
    if ($cmd) {
        $existing = Get-NetFirewallRule -DisplayName $script:RuleName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Log '  firewall block rule already present'
        }
        else {
            New-NetFirewallRule -DisplayName $script:RuleName -Direction Inbound -Action Block `
                -Protocol TCP -LocalPort 13688 -Profile Any -Enabled True | Out-Null
            Write-Log '  firewall inbound block rule created (TCP 13688)'
        }
        return
    }
    Write-Log '  WARNING: New-NetFirewallRule unavailable; trying netsh fallback'
    & netsh.exe advfirewall firewall delete rule ("name=" + $script:RuleName) | Out-Null
    & netsh.exe advfirewall firewall add rule ("name=" + $script:RuleName) dir=in action=block protocol=TCP localport=13688 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'netsh firewall rule creation failed' }
}

function Start-GcuService {
    Write-Log ("  Start-Service {0}" -f $script:ServiceName)
    Start-Service -Name $script:ServiceName
    $waited = 0
    do {
        Start-Sleep -Milliseconds 500
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        $waited++
    } while ($service -and $service.Status -ne 'Running' -and $waited -lt 40)
    if (-not $service -or $service.Status -ne 'Running') {
        throw ("{0} did not reach Running (status={1})" -f $script:ServiceName, $(if ($service) { $service.Status } else { 'missing' }))
    }
    Write-Log '  service is RUNNING'
}

function Set-InstallMarker {
    param([pscustomobject]$Selection, [string]$ServiceExe, [string]$PayloadSha256, [string]$InstallerVersion)
    try {
        $key = 'HKLM:\SOFTWARE\L-Mechrevo'
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -LiteralPath $key -Name 'GcuVariant' -Value $Selection.Variant
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceDir' -Value $Selection.ServiceDir
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceExe' -Value $ServiceExe
        if (-not [string]::IsNullOrWhiteSpace($PayloadSha256)) {
            Set-ItemProperty -LiteralPath $key -Name 'GcuPayloadSha256' -Value $PayloadSha256
        }
        if (-not [string]::IsNullOrWhiteSpace($InstallerVersion)) {
            Set-ItemProperty -LiteralPath $key -Name 'GcuInstallerVersion' -Value $InstallerVersion
        }
        Set-ItemProperty -LiteralPath $key -Name 'GcuInstalledUtc' -Value ((Get-Date).ToUniversalTime().ToString('o'))
        Write-Log '  install marker written to HKLM\SOFTWARE\L-Mechrevo (payload identity = SHA256)'
    }
    catch {
        Write-Log ('  WARNING: could not write install marker: ' + $_.Exception.Message)
    }
}

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
if ($MyInvocation.InvocationName -eq '.') {
    # Dot-sourced (tests / the build): expose the functions only, run nothing.
    return
}

try {
    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        $LogDir = Join-Path $env:ProgramData 'L-Mechrevo\logs'
    }
    if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    $script:LogFile = Join-Path $LogDir ('gcu-install-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

    Write-Log '=== L-Mechrevo GCU payload install ==='
    Write-Log ("StagingRoot = {0}" -f $StagingRoot)
    Write-Log ("TargetDir   = {0}" -f $TargetDir)
    Write-Log ("Variant     = {0}" -f $Variant)
    Write-Log ("SinglePayload = {0}" -f [bool]$SinglePayload)
    Write-Log ("DryRun      = {0}" -f [bool]$DryRun)

    $selectorScript = Join-Path $PSScriptRoot 'Select-GcuPayload.ps1'
    $selectorExe = Join-Path $PSHOME 'powershell.exe'
    $selectorArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $selectorScript, '-Variant', $Variant, '-AsJson')
    if ($SinglePayload) { $selectorArgs += '-SinglePayload' }
    $selectionJson = & $selectorExe @selectorArgs
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($selectionJson | Out-String))) {
        throw ("GCU payload selection failed (exit {0})" -f $LASTEXITCODE)
    }
    $selection = ($selectionJson | Out-String) | ConvertFrom-Json
    Write-Log ("Selection   = {0}-series / {1} ({2})" -f $selection.Generation, $selection.Variant, $selection.Reason)

    $serviceSource = Join-Path (Join-Path $StagingRoot $selection.StagedDir) $selection.ServiceDir
    $serviceTarget = Join-Path $TargetDir $selection.ServiceDir
    $driverSource = Join-Path (Join-Path $StagingRoot 'payload\common') 'UWACPIDriver'
    if (-not (Test-Path -LiteralPath (Join-Path $driverSource 'UWACPIDriver.inf'))) {
        $driverSource = Join-Path (Join-Path $StagingRoot $selection.StagedDir) 'UWACPIDriver'
    }
    $driverTarget = Join-Path $TargetDir 'UWACPIDriver'
    $serviceExe = Join-Path $serviceTarget 'GCUBridge.exe'
    $gcuServiceExe = Join-Path (Join-Path $serviceTarget 'MyControlCenter') 'GCUService.exe'
    $driverSys = Join-Path $driverTarget 'UWACPIDriver.sys'

    if (-not (Test-Path -LiteralPath (Join-Path $serviceSource 'GCUBridge.exe'))) {
        throw ("selected payload is incomplete; missing GCUBridge.exe under {0}" -f $serviceSource)
    }
    # Identity is the SHA256 of the service binary; the copy is byte-identical so the source hash
    # is what will be installed.
    $payloadIdentity = Get-GcuPayloadIdentity -ServiceDir $serviceSource
    Write-Log ("payload identity (SHA256) = {0}" -f $payloadIdentity.Sha256)

    # Anti-downgrade (installer/app version line, shared and monotonic): refuse before any change.
    $marker = Get-InstalledMarker
    $downgrade = Test-InstallerDowngrade -InstalledVersion $marker.GcuInstallerVersion -IncomingVersion $InstallerVersion
    if ($downgrade -eq 'Downgrade') {
        throw ("refusing to downgrade: installed L-Mechrevo {0} is newer than this installer {1}; no service/driver/firewall changes made - use a newer installer to upgrade." -f $marker.GcuInstallerVersion, $InstallerVersion)
    }
    Write-Log ("downgrade check = {0} (installed='{1}' incoming='{2}')" -f $downgrade, $marker.GcuInstallerVersion, $InstallerVersion)

    # Fast path: already installed, healthy and the same payload identity -> verify only.
    $currentBinPath = Get-ServiceBinPath
    $alreadyCurrent = (Test-ExpectedBinPath -PathName $currentBinPath -ExpectedExe $serviceExe) -and `
        (Test-Path -LiteralPath $serviceExe) -and (Test-Path -LiteralPath $gcuServiceExe) -and (Test-Path -LiteralPath $driverSys)
    $currentIdentitySha = if (Test-Path -LiteralPath $gcuServiceExe) { (Get-GcuPayloadIdentity -ServiceDir $serviceTarget).Sha256 } else { $null }
    $action = Get-InstallAction -AlreadyCurrent $alreadyCurrent -InstalledSha256 $currentIdentitySha -IncomingSha256 $payloadIdentity.Sha256

    if ($action -eq 'VerifyOnly' -and -not $DryRun) {
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-Log 'already installed with the same payload identity and RUNNING; verifying signatures and firewall only'
            Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
            Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
            Assert-SignedFile -Path $driverSys -Label 'UWACPIDriver'
            Ensure-FirewallRule
            Set-InstallMarker -Selection $selection -ServiceExe $serviceExe -PayloadSha256 $payloadIdentity.Sha256 -InstallerVersion $InstallerVersion
            Write-Log 'GCU install OK (no changes needed)'
            exit 0
        }
    }

    Write-Log '[1/6] removing any prior generation service/registration (uninstall-first)'
    Invoke-PriorGenerationUninstall -TargetDir $TargetDir -UninstallScriptPath $UninstallScriptPath -LogDir $LogDir -DryRun:$DryRun
    if (-not $DryRun) { Assert-Port13688Free }

    Write-Log ('[2/6] copying {0} payload' -f $selection.Variant)
    Copy-Tree -Source $serviceSource -Destination $serviceTarget
    Copy-Tree -Source $driverSource -Destination $driverTarget

    Write-Log '[3/6] verifying Authenticode signatures'
    Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
    Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
    Assert-SignedFile -Path $driverSys -Label 'UWACPIDriver'

    if ($DryRun) {
        Write-Log 'DRY RUN: copied + signature-verified only; skipping driver/service/firewall changes'
        Write-Log ("DRY RUN output: {0}" -f $serviceTarget)
        exit 0
    }

    Write-Log '[4/6] installing UWACPI kernel driver'
    Install-UwacpiDriver -DriverDir $driverTarget

    Write-Log '[5/6] registering GCUBridge service'
    Install-Service -ServiceExe $serviceExe

    Write-Log '[6/6] firewall rule + service start'
    Ensure-FirewallRule
    if ($NoStart) {
        Write-Log '  -NoStart set; service left stopped'
    }
    else {
        Start-GcuService
    }

    Set-InstallMarker -Selection $selection -ServiceExe $serviceExe -PayloadSha256 $payloadIdentity.Sha256 -InstallerVersion $InstallerVersion
    Write-Log 'GCU install OK'
    exit 0
}
catch {
    if ($script:LogFile) {
        Write-Log ('FATAL: ' + $_.Exception.Message)
        Write-Log ($_.ScriptStackTrace)
    }
    else {
        Write-Error $_
    }
    exit 1
}
