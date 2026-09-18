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
    [switch]$NoStart,
    [switch]$SinglePayload,
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:RuleName = 'L-Mechrevo - Block remote GCU MQTT'
$script:ServiceName = 'GCUBridge'
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

function Stop-GcuProcesses {
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Log ("  stopping {0} (status={1})" -f $script:ServiceName, $service.Status)
        Stop-Service -Name $script:ServiceName -Force -ErrorAction SilentlyContinue
    }
    foreach ($name in @('GCUService', 'GCUBridge')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Write-Log ("  stopping leftover process {0} (pid {1})" -f $name, $process.Id)
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Milliseconds 500
}

function Test-ExpectedBinPath {
    param([string]$PathName, [string]$ExpectedExe)
    if ([string]::IsNullOrWhiteSpace($PathName)) { return $false }
    $actual = $PathName.Trim().Trim('"')
    return ($actual.TrimEnd('"') -ieq $ExpectedExe)
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
    param([pscustomobject]$Selection, [string]$ServiceExe)
    try {
        $key = 'HKLM:\SOFTWARE\L-Mechrevo'
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -LiteralPath $key -Name 'GcuVariant' -Value $Selection.Variant
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceDir' -Value $Selection.ServiceDir
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceExe' -Value $ServiceExe
        Set-ItemProperty -LiteralPath $key -Name 'GcuInstalledUtc' -Value ((Get-Date).ToUniversalTime().ToString('o'))
        Write-Log '  install marker written to HKLM\SOFTWARE\L-Mechrevo'
    }
    catch {
        Write-Log ('  WARNING: could not write install marker: ' + $_.Exception.Message)
    }
}

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
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

    # Fast path: already installed and healthy -> only re-assert the firewall rule.
    $currentBinPath = Get-ServiceBinPath
    $alreadyCurrent = (Test-ExpectedBinPath -PathName $currentBinPath -ExpectedExe $serviceExe) -and `
        (Test-Path -LiteralPath $serviceExe) -and (Test-Path -LiteralPath $gcuServiceExe) -and (Test-Path -LiteralPath $driverSys)

    if ($alreadyCurrent -and -not $DryRun) {
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-Log 'already installed and RUNNING; verifying signatures and firewall only'
            Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
            Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
            Assert-SignedFile -Path $driverSys -Label 'UWACPIDriver'
            Ensure-FirewallRule
            Set-InstallMarker -Selection $selection -ServiceExe $serviceExe
            Write-Log 'GCU install OK (no changes needed)'
            exit 0
        }
    }

    if (-not $DryRun) {
        Stop-GcuProcesses
    }

    Write-Log ('[1/5] copying {0} payload' -f $selection.Variant)
    Copy-Tree -Source $serviceSource -Destination $serviceTarget
    Copy-Tree -Source $driverSource -Destination $driverTarget

    Write-Log '[2/5] verifying Authenticode signatures'
    Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
    Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
    Assert-SignedFile -Path $driverSys -Label 'UWACPIDriver'

    if ($DryRun) {
        Write-Log 'DRY RUN: copied + signature-verified only; skipping driver/service/firewall changes'
        Write-Log ("DRY RUN output: {0}" -f $serviceTarget)
        exit 0
    }

    Write-Log '[3/5] installing UWACPI kernel driver'
    Install-UwacpiDriver -DriverDir $driverTarget

    Write-Log '[4/5] registering GCUBridge service'
    Install-Service -ServiceExe $serviceExe

    Write-Log '[5/5] firewall rule + service start'
    Ensure-FirewallRule
    if ($NoStart) {
        Write-Log '  -NoStart set; service left stopped'
    }
    else {
        Start-GcuService
    }

    Set-InstallMarker -Selection $selection -ServiceExe $serviceExe
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
