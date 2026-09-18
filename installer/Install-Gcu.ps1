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
    [string]$StatusDir,
    [string]$AppExe,
    [string]$ConfigDir,
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
        catch {
            # A missing value is normal on a first install; anything else (denied read) must be
            # visible, because a silently unreadable marker disables the anti-downgrade check.
            Write-Log ("  install marker value '{0}' unreadable: {1}" -f $name, $_.Exception.Message)
        }
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

function Test-GcuPostInstall {
    # Four invariants. ItemSupport and ServiceReady are written by the vendor service, never by us;
    # we only read them. Failure is explicit because Inno [Run] has no ignoreerrors.
    [CmdletBinding()]
    param(
        [string[]]$ServiceNames,
        [int[]]$ListenerPids,
        [bool]$ItemSupportPresent,
        [int]$ServiceReady,
        [string]$ExpectedServiceName = 'GCUBridge'
    )
    $names = @($ServiceNames)
    $pids = @($ListenerPids)
    $observed = if ($names.Count -gt 0) { $names -join ', ' } else { '(none)' }
    $checks = @(
        [pscustomobject]@{
            Name = 'single-service'
            Ok = (($names.Count -eq 1) -and ($names -contains $ExpectedServiceName))
            Detail = ("expected exactly one GCU service ({0}); found {1}: {2}" -f $ExpectedServiceName, $names.Count, $observed)
        },
        [pscustomobject]@{
            Name = 'single-13688-owner'
            Ok = ($pids.Count -eq 1)
            Detail = ("expected exactly one listener on TCP 13688; found {0}" -f $pids.Count)
        },
        [pscustomobject]@{
            Name = 'item-support'
            Ok = [bool]$ItemSupportPresent
            Detail = 'HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport missing - the vendor service did not write it'
        },
        [pscustomobject]@{
            Name = 'service-ready'
            Ok = ($ServiceReady -eq 1)
            Detail = ("ServiceReady == {0} (expected 1) - the vendor service did not become ready; see %ProgramData%\L-Mechrevo\logs\gcu-install-*.log" -f $ServiceReady)
        }
    )
    $failed = @($checks | Where-Object { -not $_.Ok })
    return [pscustomobject]@{ Ok = ($failed.Count -eq 0); Checks = $checks; Failed = $failed }
}

# ---------------------------------------------------------------------------
# N7 (owner correction): remove the vendor's official console as well as the
# existing GCU service, leaving only our console. The earlier "prompt the user,
# never delete" rule was the orchestrator's invention and is withdrawn.
#
# Removing a vendor application is destructive, so the scope is pinned by the
# predicate below: vendor-console + GCU artefacts ARE removable; unrelated
# software is NOT. Every step is announced and logged; a locked component is
# reported and skipped, never fatal.
# ---------------------------------------------------------------------------

# Concrete, verifiable vendor identifiers. Grounded in the app-side
# OfficialConsoleIsolation markers and the vendor packages documented in
# docs\hardware\README.md (GamingCenter3_Cross.UWP_5.17.*, CCU.WinUI).
$script:VendorPackageMarkers = @(
    'CCU.WinUI', 'GamingCenter3_Cross.UWP', 'GamingCenterU', 'ControlCenterU', 'GCUUI'
)
$script:VendorProcessNames = @(
    'CCUWinUI', 'SystrayComponent', 'ControlCenterU', 'GamingCenterU', 'GCUUI'
)
# ASCII-only file: the Chinese display names are built from code points, never typed literally.
$script:VendorUninstallMarkers = @(
    'GamingCenter', 'ControlCenter', 'Mechrevo Gaming', 'AISTONE', 'Uniwill',
    (-join @([char]0x7535, [char]0x7ADE, [char]0x63A7, [char]0x5236, [char]0x53F0)),   # dian jing kong zhi tai
    (-join @([char]0x63A7, [char]0x5236, [char]0x53F0))                                # kong zhi tai
)
# Only these exact directories may be removed. Never a parent, never a wildcard.
$script:VendorDirectoryAllowList = @(
    'L-Mechrevo\GCU', 'L-Mechrevo\GCU\AiStoneService', 'L-Mechrevo\GCU\UniwillService',
    'L-Mechrevo\GCU\UWACPIDriver', 'L-Mechrevo\GCU\payload'
)

function Test-VendorArtefactRemovable {
    # The single decision point for "may this be removed?". Pure and testable: no I/O.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('package', 'directory', 'uninstall', 'shortcut', 'autostart', 'process')]
        [string]$Kind,
        [Parameter(Mandatory = $true)][string]$Value
    )
    if ([string]::IsNullOrWhiteSpace($Value)) { return 'KEEP' }

    switch ($Kind) {
        'package' {
            foreach ($marker in $script:VendorPackageMarkers) {
                if ($Value -like ('*' + $marker + '*')) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        'process' {
            foreach ($name in $script:VendorProcessNames) {
                if ($Value -ieq $name) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        'uninstall' {
            foreach ($marker in $script:VendorUninstallMarkers) {
                if ($Value -like ('*' + $marker + '*')) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        'shortcut' {
            # A shortcut is removable only when its own file name carries a vendor marker.
            $leaf = Split-Path -Leaf $Value
            foreach ($marker in @('GamingCenter', 'ControlCenter', 'CCUWinUI', 'SystrayComponent', 'GCUUI')) {
                if ($leaf -like ('*' + $marker + '*')) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        'autostart' {
            foreach ($marker in @('CCUWinUI.exe', 'SystrayComponent.exe', 'ControlCenterU.exe', 'GamingCenterU.exe', 'GCUUI.exe')) {
                if ($Value -like ('*' + $marker + '*')) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        'directory' {
            # Allow-list only: the value must END with one of the exact vendor GCU paths.
            $normalized = $Value.TrimEnd('\')
            foreach ($allowed in $script:VendorDirectoryAllowList) {
                if ($normalized -like ('*' + $allowed)) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        default { return 'KEEP' }
    }
}

function Remove-VendorConsole {
    # Idempotent, logged, non-fatal. Reports what was found, removed and skipped.
    $removed = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]

    # 1. UWP/MSIX package form.
    $packages = @()
    try {
        $packages = @(Get-AppxPackage -ErrorAction Stop | Where-Object {
            (Test-VendorArtefactRemovable -Kind 'package' -Value $_.Name) -eq 'REMOVABLE'
        })
    }
    catch { Write-Log ('  could not enumerate Appx packages: ' + $_.Exception.Message) }

    if ($packages.Count -eq 0) {
        Write-Log '  no official console package found'
    }
    foreach ($package in $packages) {
        Write-Log ('  found official console package: {0}' -f $package.PackageFullName)
        try {
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
            $removed.Add($package.PackageFullName)
            Write-Log ('  removed official console package: {0}' -f $package.PackageFullName)
        }
        catch {
            $skipped.Add($package.PackageFullName)
            Write-Log ('  official console package could not be removed ({0}); continuing: {1}' -f $package.PackageFullName, $_.Exception.Message)
        }
    }

    # 2. Desktop-exe form: stop the UI processes, then remove the allow-listed dirs.
    foreach ($name in $script:VendorProcessNames) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Write-Log ('  stopping official console process: {0} (pid {1})' -f $name, $process.Id)
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    $programRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($root in $programRoots) {
        foreach ($allowed in $script:VendorDirectoryAllowList) {
            $candidate = Join-Path $root $allowed
            if (-not (Test-Path -LiteralPath $candidate)) { continue }
            if ((Test-VendorArtefactRemovable -Kind 'directory' -Value $candidate) -ne 'REMOVABLE') {
                Write-Log ('  refusing to remove non-allow-listed directory: {0}' -f $candidate)
                continue
            }
            Write-Log ('  found official console directory: {0}' -f $candidate)
            try {
                Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction Stop
                $removed.Add($candidate)
                Write-Log ('  removed official console directory: {0}' -f $candidate)
            }
            catch {
                $skipped.Add($candidate)
                Write-Log ('  official console directory could not be removed ({0}); continuing: {1}' -f $candidate, $_.Exception.Message)
            }
        }
    }

    # 3. Autostart entries pointing at a vendor console executable.
    foreach ($hive in @('HKCU:', 'HKLM:')) {
        foreach ($sub in @('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce')) {
            $key = Join-Path $hive $sub
            if (-not (Test-Path -LiteralPath $key)) { continue }
            foreach ($property in @((Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' })) {
                if ((Test-VendorArtefactRemovable -Kind 'autostart' -Value ([string]$property.Value)) -ne 'REMOVABLE') { continue }
                Write-Log ('  found official console autostart entry: {0} = {1}' -f $property.Name, $property.Value)
                try {
                    Remove-ItemProperty -LiteralPath $key -Name $property.Name -ErrorAction Stop
                    $removed.Add(('{0}\{1}' -f $key, $property.Name))
                    Write-Log ('  removed official console autostart entry: {0}' -f $property.Name)
                }
                catch {
                    $skipped.Add(('{0}\{1}' -f $key, $property.Name))
                    Write-Log ('  autostart entry could not be removed ({0}); continuing: {1}' -f $property.Name, $_.Exception.Message)
                }
            }
        }
    }

    return [pscustomobject]@{ Removed = $removed; Skipped = $skipped }
}

function Get-GcuFallbackGuidance {
    # N6: the visible fallback that replaces the retired G0 gate. HONEST LIMITATION: this project
    # has no download server, so there is no one-click cloud download. The guidance tells the user
    # exactly which payload to fetch and where it lives, and how to install it by hand.
    $lines = @(
        'The newest GCU payload (release\GCU-only) did not come up on this machine.'
        'If this is a 40-series machine, fetch the matching 40-series payload and install it by hand:'
        '  1. release\GCU-40-51751  (AiStoneService variant; the 40-series default)'
        '  2. release\GCU-40-51749  (UniwillService variant; the older 40-series console)'
        'Both live in the L-Mechrevo source tree under release\ and are NOT shipped in this package.'
        'There is no download server for this project, so the payload cannot be fetched automatically.'
        'To install one by hand: copy its <ServiceDir> folder over {app}\GCU\<ServiceDir>, then run'
        '  powershell -NoProfile -ExecutionPolicy Bypass -File "{app}\GCU\Install-Gcu.ps1" -StagingRoot "{app}\GCU" -TargetDir "{app}\GCU"'
        'and re-run the self-check. Report the failure with %ProgramData%\L-Mechrevo\logs\gcu-install-*.log.'
    )
    return [pscustomobject]@{
        Reason   = 'the newest GCU payload did not become ready on this machine'
        Payloads = @('release\GCU-40-51751', 'release\GCU-40-51749')
        Lines    = $lines
    }
}

function Get-GcuPostInstallFacts {
    $services = @()
    try {
        $services = @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop |
            Where-Object { $_.PathName -and ($_.PathName -match '(?i)\\GCU\\') } |
            Select-Object -ExpandProperty Name)
    }
    catch { $services = @() }
    $pids = @()
    if (Get-Command -Name Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $pids = @(Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique)
    }
    $itemSupportPresent = $false
    $serviceReady = -1
    try {
        $item = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction Stop
        $itemSupportPresent = (@($item.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' }).Count -gt 0)
        $ready = $item.PSObject.Properties['ServiceReady']
        if ($null -ne $ready) { $serviceReady = [int]$ready.Value }
    }
    catch {
        # Post-install verification reads this; a denied read must not look like "absent".
        Write-Log ('  ItemSupport unreadable during post-install verification: ' + $_.Exception.Message)
    }
    return [pscustomobject]@{
        ServiceNames = $services
        ListenerPids = $pids
        ItemSupportPresent = $itemSupportPresent
        ServiceReady = $serviceReady
    }
}

function Write-GcuInstallStatus {
    [CmdletBinding()]
    param([string]$StatusDir, [string]$Status, [string]$Reason, [object]$Checks)
    if ([string]::IsNullOrWhiteSpace($StatusDir)) { return $null }
    if (-not (Test-Path -LiteralPath $StatusDir)) { New-Item -ItemType Directory -Path $StatusDir -Force | Out-Null }
    $path = Join-Path $StatusDir 'gcu-install-status.json'
    $payload = [ordered]@{
        Status     = $Status
        Reason     = $Reason
        UpdatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Checks     = @($Checks)
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
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

# ---------------------------------------------------------------------------
# N5: privileges are acquired ONCE, here, while the installer is elevated.
# The app is permanently elevated by owner decision; the installer is the only
# place elevation is obtained, so the autostart task must exist before the app
# ever runs. An unelevated app cannot create a highest-privileges task, which is
# the same permission root cause as the original "reboot does not autostart" bug.
# ---------------------------------------------------------------------------

function Get-AutostartTaskName {
    # Per-user task name, matching the app's own naming so the app-side repair path
    # recognises the installer-created task instead of creating a second one.
    $sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
    return ('LMechrevo_' + $sid)
}

function Register-AutostartTask {
    param([Parameter(Mandatory = $true)][string]$AppExe)
    if (-not (Test-Path -LiteralPath $AppExe)) {
        throw ("autostart task target not found: {0}" -f $AppExe)
    }
    $taskName = Get-AutostartTaskName
    $userName = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).Name

    # N15 #14 regression fix: the action MUST carry the `startup` argument. The app's own contract
    # compares the action's arguments to "startup" (Helpers\Startup.cs:329) and registers it that
    # way (:360); a task without it never matches the app's plan, so the app rewrites it and the
    # launch path is wrong - which is exactly the "autostart does nothing" field report. The
    # argument is a fixed literal, not user input, so it is not an injectable surface.
    $action = New-ScheduledTaskAction -Execute $AppExe -Argument 'startup'
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
    $logonTrigger.Delay = 'PT10S'
    $consoleTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
    $consoleTrigger.Delay = 'PT10S'
    $principal = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    # -Force overwrites the same-named task in place: no delete window, so a denied re-register
    # can never leave the machine without an autostart entry.
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logonTrigger, $consoleTrigger) `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Log ("  autostart task registered: {0} (RunLevel=Highest, action='{1}' with no arguments)" -f $taskName, $AppExe)
    return $taskName
}

function Grant-AppDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    $userName = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).Name
    # Modify = read/write/delete for this user only. Deliberately not a broad principal and not the
    # widest right: the app needs to write its own config/logs, nothing more.
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $userName, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
    Write-Log ("  ACL granted (Modify): {0} -> {1}" -f $userName, $Path)
}

function Grant-AcpiDriverAccess {
    # \\.\ACPIDriver is opened by the app for the read-only EC snapshot and the charge-limit write.
    # Running elevated already covers it; this only makes the grant explicit and idempotent so a
    # future non-elevated consumer is not silently denied. No EC/firmware write is added here.
    $driver = Join-Path $env:SystemRoot 'System32\drivers\UWACPIDriver.sys'
    if (-not (Test-Path -LiteralPath $driver)) {
        Write-Log '  UWACPIDriver.sys not present; skipping device ACL (driver install step owns it)'
        return
    }
    $userName = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).Name
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($userName, 'ReadAndExecute', 'Allow')
    $acl = Get-Acl -LiteralPath $driver
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $driver -AclObject $acl
    Write-Log ("  ACL granted (ReadAndExecute): {0} -> {1}" -f $userName, $driver)
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

    Write-Log '[1/8] removing any prior generation service/registration (uninstall-first)'
    $existingService = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Log ('  found existing GCU service: {0} (status={1}); removing it' -f $script:ServiceName, $existingService.Status)
    }
    else {
        Write-Log '  no existing GCU service found'
    }
    Invoke-PriorGenerationUninstall -TargetDir $TargetDir -UninstallScriptPath $UninstallScriptPath -LogDir $LogDir -DryRun:$DryRun
    if (-not $DryRun) { Assert-Port13688Free }

    # N7 (owner correction): the vendor's official console is removed too, so only our console
    # remains. Announced and logged; a locked component is reported and skipped, never fatal.
    Write-Log '[2/8] removing the vendor official console (packages, directories, autostart)'
    if ($DryRun) {
        Write-Log '  DRY RUN: vendor-console removal skipped'
    }
    else {
        $vendorResult = Remove-VendorConsole
        Write-Log ('  vendor-console removal done: {0} removed, {1} skipped' -f $vendorResult.Removed.Count, $vendorResult.Skipped.Count)
        if ($vendorResult.Skipped.Count -gt 0) {
            Write-Log '  the following vendor artefacts could not be removed and need a manual step or a reboot:'
            foreach ($item in $vendorResult.Skipped) { Write-Log ('    - ' + $item) }
        }
    }

    Write-Log ('[3/8] copying {0} payload' -f $selection.Variant)
    Copy-Tree -Source $serviceSource -Destination $serviceTarget
    Copy-Tree -Source $driverSource -Destination $driverTarget

    Write-Log '[4/8] verifying Authenticode signatures'
    Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
    Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
    Assert-SignedFile -Path $driverSys -Label 'UWACPIDriver'

    if ($DryRun) {
        Write-Log 'DRY RUN: copied + signature-verified only; skipping driver/service/firewall changes'
        Write-Log ("DRY RUN output: {0}" -f $serviceTarget)
        exit 0
    }

    Write-Log '[5/8] installing UWACPI kernel driver'
    Install-UwacpiDriver -DriverDir $driverTarget

    Write-Log '[6/8] registering GCUBridge service'
    Install-Service -ServiceExe $serviceExe

    Write-Log '[7/8] firewall rule + service start'
    Ensure-FirewallRule
    if ($NoStart) {
        Write-Log '  -NoStart set; service left stopped'
    }
    else {
        Start-GcuService

        # Post-install verification: the vendor service is the source of truth for ItemSupport and
        # ServiceReady; a failure is surfaced explicitly (Inno [Run] has no ignoreerrors).
        Write-Log 'post-install verification (single service / 13688 owner / ItemSupport / ServiceReady)'
        $facts = Get-GcuPostInstallFacts
        $verdict = Test-GcuPostInstall -ServiceNames $facts.ServiceNames -ListenerPids $facts.ListenerPids -ItemSupportPresent $facts.ItemSupportPresent -ServiceReady $facts.ServiceReady
        $resolvedStatusDir = if (-not [string]::IsNullOrWhiteSpace($StatusDir)) { $StatusDir } else { $LogDir }
        $verdictReason = (@($verdict.Checks) | ForEach-Object { ('{0}={1}' -f $_.Name, $_.Ok) }) -join ' '
        $statusPath = Write-GcuInstallStatus -StatusDir $resolvedStatusDir -Status $(if ($verdict.Ok) { 'ready' } else { 'failed' }) -Reason $verdictReason -Checks $verdict.Checks
        if (-not $verdict.Ok) {
            $reasons = (@($verdict.Failed) | ForEach-Object { ('{0}: {1}' -f $_.Name, $_.Detail) }) -join '; '
            Write-Log ('FATAL: post-install verification failed: ' + $reasons)
            Write-Log ('FATAL: see ' + $script:LogFile + ' (gcu-install-*.log)')
            # N6: the visible fallback. Never silent, never a fake cloud download.
            $guidance = Get-GcuFallbackGuidance
            foreach ($line in $guidance.Lines) { Write-Log ('FALLBACK: ' + $line) }
            exit 1
        }
        Write-Log ('post-install verification OK; status file: ' + $statusPath)
    }

    Set-InstallMarker -Selection $selection -ServiceExe $serviceExe -PayloadSha256 $payloadIdentity.Sha256 -InstallerVersion $InstallerVersion

    # N5: acquire every privilege the app needs, once, while we are elevated.
    Write-Log '[8/8] privileges: autostart task (highest), directory ACLs, device access'
    if ([string]::IsNullOrWhiteSpace($AppExe)) {
        $AppExe = Join-Path (Split-Path -Parent $TargetDir) 'L-Mechrevo.exe'
    }
    Register-AutostartTask -AppExe $AppExe | Out-Null
    Grant-AppDirectoryAcl -Path (Split-Path -Parent $TargetDir)
    Grant-AppDirectoryAcl -Path $TargetDir
    # The app's config/log dir is %AppData%\MechrevoLite (Logger.ResolveAppPath). Resolve it here
    # rather than passing {userappdata} through Inno: under admin install mode that constant is the
    # elevating account's profile, not the invoking user's, and Inno warns about exactly that.
    if ([string]::IsNullOrWhiteSpace($ConfigDir)) {
        $ConfigDir = Join-Path $env:APPDATA 'MechrevoLite'
    }
    Grant-AppDirectoryAcl -Path $ConfigDir
    Grant-AppDirectoryAcl -Path $LogDir
    Grant-AcpiDriverAccess

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
