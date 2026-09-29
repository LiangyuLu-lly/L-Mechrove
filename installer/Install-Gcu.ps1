#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
# This is the GCU payload installer the Inno Setup package runs AFTER copying files.
# It installs the GCU payload selected for this machine (newest payload for 30/40/50, the
# GamingCenterU legacy service for GTX 10/16 + RTX 20), copies it under
# %ProgramFiles%\L-Mechrevo\GCU as an OVERLAY (the vendor service's own user state - modes,
# fan curves, settings - survives), verifies Authenticode, installs the payload's ACPI driver,
# registers + starts the GCUBridge service and adds the MQTT blocking firewall rule.
# The whole step is idempotent: re-running repairs instead of failing.
<#
.SYNOPSIS
    Install the shipped GCU vendor payload bundled by L-Mechrevo.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-Gcu.ps1 `
        -PayloadRoot "C:\Program Files\L-Mechrevo\GCU\payload" `
        -TargetDir  "C:\Program Files\L-Mechrevo\GCU"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StagingRoot,
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [ValidateSet('Auto')][string]$Variant = 'Auto',
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
$script:InstalledDriverPublishedName = $null

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

# State the vendor GCU service itself writes at runtime under <ServiceDir>\MyControlCenter: the
# per-mode profiles (the custom-mode power limits), the user fan curves and the feature settings.
# robocopy /E overwrote all of it with the payload's factory copies on every upgrade, and the old
# uninstall-first upgrade deleted it outright - users lost their tuned modes and fan curves.
$script:GcuUserStateDirectories = @(
    'UserPofiles', 'BTSavingSettings', 'GPUPowerSavingSettings', 'LCSavingSettings', 'LiquidHWOC',
    'SQLiteDB', 'AppSettings', 'KeyboardManager', 'DisplayProfile', 'GamingMonitor'
)

function Get-GcuUserStateFiles {
    # Relative paths (under MyControlCenter) of the user state present in an installed tree.
    param([string]$ServiceDir)
    $root = Join-Path $ServiceDir 'MyControlCenter'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $script:GcuUserStateDirectories) {
        $path = Join-Path $root $dir
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue)) {
            $files.Add($file.FullName.Substring($root.Length).TrimStart('\'))
        }
    }
    # User fan curves are the top-level M<mode>T<slot>.json; the per-model subfolders are vendor data.
    $fan = Join-Path $root 'UserFanTables'
    if (Test-Path -LiteralPath $fan) {
        foreach ($file in @(Get-ChildItem -LiteralPath $fan -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^M\d+T\d+\.json$' })) {
            $files.Add('UserFanTables\' + $file.Name)
        }
    }
    return $files.ToArray()
}

function Save-GcuUserState {
    param([string]$ServiceDir, [string]$BackupDir)
    $relative = @(Get-GcuUserStateFiles -ServiceDir $ServiceDir)
    if ($relative.Count -eq 0) { return $null }
    $root = Join-Path $ServiceDir 'MyControlCenter'
    if (Test-Path -LiteralPath $BackupDir) { Remove-Item -LiteralPath $BackupDir -Recurse -Force }
    foreach ($rel in $relative) {
        $destination = Join-Path $BackupDir $rel
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath (Join-Path $root $rel) -Destination $destination -Force
    }
    Write-Log ('  vendor service user state saved ({0} files)' -f $relative.Count)
    return [pscustomobject]@{ BackupDir = $BackupDir; Files = $relative }
}

function Restore-GcuUserState {
    param([object]$Saved, [string]$ServiceDir)
    if ($null -eq $Saved) { return }
    $root = Join-Path $ServiceDir 'MyControlCenter'
    $restored = 0
    foreach ($rel in @($Saved.Files)) {
        $source = Join-Path $Saved.BackupDir $rel
        if (-not (Test-Path -LiteralPath $source)) { continue }
        $destination = Join-Path $root $rel
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $restored++
    }
    Remove-Item -LiteralPath $Saved.BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log ('  vendor service user state restored ({0} files)' -f $restored)
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
    # Service replacement only: keep the autostart and charge-limit tasks. Older uninstall scripts do
    # not know the switch (passing it would fail their parameter binding), so it is added only when
    # the script declares it.
    if ((Get-Content -LiteralPath $scriptPath -Raw) -match '\[switch\]\$KeepTasks') { $uninstallArgs += '-KeepTasks' }
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
        [string]$ExpectedServiceName = 'GCUBridge',
        # GamingCenterU legacy service (GTX 10/16, RTX 20): its decompiled service contains no
        # ItemSupport/ServiceReady writer, so those two facts are advisory for it, not gates.
        [switch]$Legacy
    )
    $names = @($ServiceNames)
    $pids = @($ListenerPids)
    $observed = if ($names.Count -gt 0) { $names -join ', ' } else { '(none)' }
    $advisory = if ($Legacy) { ' (legacy service: advisory only)' } else { '' }
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
            Ok = ([bool]$ItemSupportPresent -or [bool]$Legacy)
            Detail = ('HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport missing - the vendor service did not write it' + $advisory)
        },
        [pscustomobject]@{
            Name = 'service-ready'
            Ok = (($ServiceReady -eq 1) -or [bool]$Legacy)
            Detail = (("ServiceReady == {0} (expected 1) - the vendor service did not become ready; see %ProgramData%\L-Mechrevo\logs\gcu-install-*.log" -f $ServiceReady) + $advisory)
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

# Concrete, verifiable vendor identifiers, taken from the vendor packages
# documented in docs\hardware\README.md (GamingCenter3_Cross.UWP_5.17.*, CCU.WinUI).
# The app no longer isolates or restores the official console: the first-run
# guide asks the user to uninstall it, and this installer removes it.
# 30/40-series consoles install the UWP package "ControlCenter3" (their uninstaller removes
# "ControlCenter3_<ver>_neutral_~_..."); the 50-series console is the MSIX "CCU.WinUI".
$script:VendorPackageMarkers = @(
    'CCU.WinUI', 'ControlCenter3', 'GamingCenter3_Cross.UWP', 'GamingCenterU', 'ControlCenterU', 'GCUUI'
)
$script:VendorProcessNames = @(
    'CCUWinUI', 'SystrayComponent', 'ControlCenterU', 'GamingCenterU', 'GCUUI', 'OSDTpDetect', 'GameTesting'
)
# Every vendor console generation (Control Center 4.x/5.x, the GX build, GamingCenterU) is one Inno
# Setup product with the same AppId, so its uninstall key name is the precise identifier.
$script:VendorUninstallKeyNames = @('{6ea3ce12-b991-4b65-9f8d-b148eaaecd87}_is1')
# DisplayName rules. The old bare 'Mechrevo' / 'ji xie ge ming' markers also matched our own
# "L-Mechrevo" entry and unrelated Mechrevo software (the warranty-card app), so generic names
# must match exactly and brand words only count together with a console word.
$script:VendorUninstallExactNames = @(
    'Control Center', 'Control Center Service', 'GamingCenterU', 'ControlCenterU', 'Gaming Center', 'GamingCenter', 'ControlCenter'
)
# ASCII-only file: the Chinese display names are built from code points, never typed literally.
$script:VendorUninstallMarkers = @(
    'GamingCenter', 'ControlCenter', 'Mechrevo Gaming', 'AISTONE', 'Uniwill', 'CCU.WinUI', 'CCUWinUI',
    (-join @([char]0x7535, [char]0x7ADE, [char]0x63A7, [char]0x5236, [char]0x53F0))   # dian jing kong zhi tai
)
$script:VendorBrandWords = @('Mechrevo', (-join @([char]0x673A, [char]0x68B0, [char]0x9769, [char]0x547D)))   # ji xie ge ming
$script:VendorConsoleWords = @('Control', 'Center', (-join @([char]0x63A7, [char]0x5236)), (-join @([char]0x4E2D, [char]0x5FC3)))   # kong zhi / zhong xin
$script:NeverRemoveUninstallWords = @((-join @([char]0x4FDD, [char]0x4FEE)))   # bao xiu (warranty)
# Product folders under Program Files. Never L-Mechrevo\GCU (our payload).
$script:VendorDirectoryAllowList = @(
    'OEM\ControlCenter',
    'OEM\CCUWinUI',
    'OEM\GamingCenter',
    'ControlCenter',
    'CCUWinUI',
    'GamingCenter',
    'GamingCenter3',
    'AISTONE'
)

function Test-VendorArtefactRemovable {
    # The single decision point for "may this be removed?". Pure and testable: no I/O.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('package', 'directory', 'uninstall', 'uninstallkey', 'shortcut', 'autostart', 'process')]
        [string]$Kind,
        # AllowEmptyString: uninstall keys without a DisplayName are normal. A Mandatory [string] rejects
        # '' with a binding error, which used to abort the whole vendor-console removal on the first
        # such key (field log: "cannot bind argument to parameter 'Value' because it is an empty string").
        [Parameter(Mandatory = $true)][AllowEmptyString()][AllowNull()][string]$Value
    )
    if ([string]::IsNullOrWhiteSpace($Value)) { return 'KEEP' }

    switch ($Kind) {
        'uninstallkey' {
            foreach ($keyName in $script:VendorUninstallKeyNames) {
                if ($Value.Trim() -ieq $keyName) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
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
            $name = $Value.Trim()
            # Our own products are never vendor artefacts ("L-Mechrevo", "L-Mechrevo GCU").
            if ($name -like 'L-Mechrevo*') { return 'KEEP' }
            foreach ($word in $script:NeverRemoveUninstallWords) {
                if ($name -like ('*' + $word + '*')) { return 'KEEP' }
            }
            foreach ($exact in $script:VendorUninstallExactNames) {
                if ($name -ieq $exact) { return 'REMOVABLE' }
            }
            foreach ($marker in $script:VendorUninstallMarkers) {
                if ($name -like ('*' + $marker + '*')) { return 'REMOVABLE' }
            }
            $brand = @($script:VendorBrandWords | Where-Object { $name -like ('*' + $_ + '*') }).Count -gt 0
            $console = @($script:VendorConsoleWords | Where-Object { $name -like ('*' + $_ + '*') }).Count -gt 0
            if ($brand -and $console) { return 'REMOVABLE' }
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
            $normalized = $Value.TrimEnd('\')
            if ($normalized -like '*\L-Mechrevo\GCU*') { return 'KEEP' }
            foreach ($allowed in $script:VendorDirectoryAllowList) {
                if ($normalized -like ('*' + $allowed)) { return 'REMOVABLE' }
            }
            return 'KEEP'
        }
        default { return 'KEEP' }
    }
}

function Get-NotePropertyString {
    # StrictMode forbids $obj.DisplayName when the registry value is missing.
    param($Object, [string]$Name)
    if ($null -eq $Object) { return '' }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value) { return '' }
    return [string]$prop.Value
}

function Invoke-Native {
    # PS 5.1 + $ErrorActionPreference='Stop': "& native 2>&1" turns every stderr line into a
    # terminating NativeCommandError. reg.exe prints its success message on stderr, so native tools
    # run through here with a local 'Continue' and are judged by their exit code only.
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$Arguments = @())
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { [string]$_ })
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally { $ErrorActionPreference = $saved }
}

function Get-VendorUninstallCommandLine {
    # Pure: the command line actually run for a vendor uninstall entry. Every vendor console is an
    # Inno Setup product; its plain UninstallString opens a confirmation dialog, which in a hidden
    # window just hangs until the timeout. Inno uninstallers get the documented silent switches.
    param([string]$Quiet, [string]$Normal)
    $line = $Quiet
    if ([string]::IsNullOrWhiteSpace($line)) { $line = $Normal }
    if ([string]::IsNullOrWhiteSpace($line)) { return '' }
    $line = $line.Trim()
    if ($line -match '(?i)msiexec' -and $line -notmatch '(?i)/qn') {
        $line = $line + ' /qn /norestart'
    }
    elseif ($line -match '(?i)unins\d{3}\.exe' -and $line -notmatch '(?i)/VERYSILENT') {
        $line = $line + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    }
    return $line
}

function Invoke-VendorUninstallCommand {
    param([string]$DisplayName, [string]$Quiet, [string]$Normal)
    $line = Get-VendorUninstallCommandLine -Quiet $Quiet -Normal $Normal
    if ([string]::IsNullOrWhiteSpace($line)) { return $false }
    Write-Log ('  running uninstall for {0}: {1}' -f $DisplayName, $line)
    try {
        $process = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $line) -PassThru -WindowStyle Hidden
        if ($null -eq $process) { return $false }
        # The vendor uninstallers also run DISM / Remove-AppxPackage; give them time.
        if (-not $process.WaitForExit(300000)) {
            try { $process.Kill() } catch { Write-Log ('  kill timed-out uninstall failed for {0}' -f $DisplayName) }
            Write-Log ('  uninstall timed out for {0}' -f $DisplayName)
            return $false
        }
        Write-Log ('  uninstall exit code {0} for {1}' -f $process.ExitCode, $DisplayName)
        return ($process.ExitCode -eq 0)
    }
    catch {
        Write-Log ('  uninstall command failed for {0}: {1}' -f $DisplayName, $_.Exception.Message)
        return $false
    }
}

$script:GcuRegistryKey = 'HKLM\SOFTWARE\OEM\GamingCenter2'

function Save-GcuRegistrySnapshot {
    # The vendor uninstaller runs "DefaultTool <id> -u" and "GCUService <id> -u", which delete
    # HKLM\SOFTWARE\OEM\GamingCenter2\RGBKeyboard (and more) - state our GCU payload keeps using.
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath ('Registry::' + $script:GcuRegistryKey))) { return $null }
    if ([string]::IsNullOrWhiteSpace($Directory)) { $Directory = $env:TEMP }
    $file = Join-Path $Directory ('gcu-registry-{0:yyyyMMdd-HHmmss}.reg' -f (Get-Date))
    $result = Invoke-Native -FilePath (Join-Path $env:SystemRoot 'System32\reg.exe') -Arguments @('export', $script:GcuRegistryKey, $file, '/y', '/reg:64')
    if ($result.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $file)) {
        Write-Log ('  WARNING: could not snapshot {0} (reg export exit {1})' -f $script:GcuRegistryKey, $result.ExitCode)
        return $null
    }
    Write-Log ('  GCU registry snapshot saved: {0}' -f $file)
    return $file
}

function Restore-GcuRegistrySnapshot {
    param([string]$File)
    if ([string]::IsNullOrWhiteSpace($File) -or -not (Test-Path -LiteralPath $File)) { return }
    $result = Invoke-Native -FilePath (Join-Path $env:SystemRoot 'System32\reg.exe') -Arguments @('import', $File, '/reg:64')
    if ($result.ExitCode -eq 0) {
        Write-Log '  GCU registry restored from the pre-uninstall snapshot'
        Remove-Item -LiteralPath $File -Force -ErrorAction SilentlyContinue
    }
    else { Write-Log ('  WARNING: GCU registry restore failed (reg import exit {0}); snapshot kept at {1}' -f $result.ExitCode, $File) }
}

function Invoke-DeviceRescan {
    # The vendor uninstaller runs "devcon remove acpi\INOU0000": the EC/ACPI device node disappears
    # until the next PnP enumeration. Re-enumerate so the driver (still in the store) binds again.
    $result = Invoke-Native -FilePath 'pnputil.exe' -Arguments @('/scan-devices')
    Write-Log ('  pnputil /scan-devices exit {0}' -f $result.ExitCode)
}

function Remove-VendorConsoleCore {
    # Idempotent, logged, non-fatal. Reports what was found, removed and skipped.
    param([string]$SnapshotDirectory)
    $removed = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]
    $registrySnapshot = $null
    $vendorUninstallRan = $false

    foreach ($name in $script:VendorProcessNames) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Write-Log ('  stopping official console process: {0} (pid {1})' -f $name, $process.Id)
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # 1. Add/Remove Programs uninstall (Win32 / MSI). This is the real "uninstall official console".
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    $uninstallFound = 0
    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($item in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $props = $null
            try { $props = Get-ItemProperty -LiteralPath $item.PSPath -ErrorAction Stop } catch { continue }
            $display = Get-NotePropertyString -Object $props -Name 'DisplayName'
            $byKey = (Test-VendorArtefactRemovable -Kind 'uninstallkey' -Value $item.PSChildName) -eq 'REMOVABLE'
            $byName = (Test-VendorArtefactRemovable -Kind 'uninstall' -Value $display) -eq 'REMOVABLE'
            if (-not ($byKey -or $byName)) { continue }
            if ([string]::IsNullOrWhiteSpace($display)) { $display = $item.PSChildName }
            $uninstallFound++
            Write-Log ('  found official console uninstall entry: {0} (key {1})' -f $display, $item.PSChildName)
            if ($null -eq $registrySnapshot) { $registrySnapshot = Save-GcuRegistrySnapshot -Directory $SnapshotDirectory }
            $vendorUninstallRan = $true
            if (Invoke-VendorUninstallCommand -DisplayName $display -Quiet (Get-NotePropertyString -Object $props -Name 'QuietUninstallString') -Normal (Get-NotePropertyString -Object $props -Name 'UninstallString')) {
                $removed.Add($display)
                Write-Log ('  removed official console via uninstall: {0}' -f $display)
            }
            else {
                $skipped.Add($display)
                Write-Log ('  official console uninstall could not be removed ({0}); continuing' -f $display)
            }
        }
    }
    if ($uninstallFound -eq 0) {
        Write-Log '  no official console uninstall entry found'
    }
    if ($vendorUninstallRan) {
        # Undo the vendor uninstaller's side effects on state we keep using (see the helpers above).
        Restore-GcuRegistrySnapshot -File $registrySnapshot
        Invoke-DeviceRescan
    }

    # 2. UWP/MSIX: current user, all users, and provisioned image.
    $packages = @()
    try {
        $packages = @(Get-AppxPackage -AllUsers -ErrorAction Stop | Where-Object {
            (Test-VendorArtefactRemovable -Kind 'package' -Value $_.Name) -eq 'REMOVABLE'
        })
    }
    catch {
        Write-Log ('  Get-AppxPackage -AllUsers failed, falling back to current user: ' + $_.Exception.Message)
        try {
            $packages = @(Get-AppxPackage -ErrorAction Stop | Where-Object {
                (Test-VendorArtefactRemovable -Kind 'package' -Value $_.Name) -eq 'REMOVABLE'
            })
        }
        catch { Write-Log ('  could not enumerate Appx packages: ' + $_.Exception.Message) }
    }

    if ($packages.Count -eq 0) {
        Write-Log '  no official console package found'
    }
    foreach ($package in $packages) {
        Write-Log ('  found official console package: {0}' -f $package.PackageFullName)
        try {
            Remove-AppxPackage -Package $package.PackageFullName -AllUsers -ErrorAction Stop
            $removed.Add($package.PackageFullName)
            Write-Log ('  removed official console package: {0}' -f $package.PackageFullName)
        }
        catch {
            try {
                Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
                $removed.Add($package.PackageFullName)
                Write-Log ('  removed official console package (current user): {0}' -f $package.PackageFullName)
            }
            catch {
                $skipped.Add($package.PackageFullName)
                Write-Log ('  official console package could not be removed ({0}); continuing: {1}' -f $package.PackageFullName, $_.Exception.Message)
            }
        }
    }

    try {
        foreach ($prov in @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)) {
            if ((Test-VendorArtefactRemovable -Kind 'package' -Value $prov.DisplayName) -ne 'REMOVABLE') { continue }
            Write-Log ('  found official console provisioned package: {0}' -f $prov.PackageName)
            try {
                Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction Stop | Out-Null
                $removed.Add($prov.PackageName)
                Write-Log ('  removed official console provisioned package: {0}' -f $prov.PackageName)
            }
            catch {
                $skipped.Add($prov.PackageName)
                Write-Log ('  official console provisioned package could not be removed ({0}); continuing: {1}' -f $prov.PackageName, $_.Exception.Message)
            }
        }
    }
    catch { Write-Log ('  could not enumerate provisioned Appx packages: ' + $_.Exception.Message) }

    # 3. Desktop-exe folders on the allow-list.
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

    # 4. Autostart entries pointing at a vendor console executable.
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

    # 5. Start Menu / desktop shortcuts.
    $shortcutRoots = @(
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:PUBLIC 'Desktop'),
        (Join-Path $env:USERPROFILE 'Desktop')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    foreach ($root in $shortcutRoots) {
        foreach ($lnk in @(Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue)) {
            if ((Test-VendorArtefactRemovable -Kind 'shortcut' -Value $lnk.FullName) -ne 'REMOVABLE') { continue }
            Write-Log ('  found official console shortcut: {0}' -f $lnk.FullName)
            try {
                Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction Stop
                $removed.Add($lnk.FullName)
                Write-Log ('  removed official console shortcut: {0}' -f $lnk.FullName)
            }
            catch {
                $skipped.Add($lnk.FullName)
                Write-Log ('  official console shortcut could not be removed ({0}); continuing: {1}' -f $lnk.FullName, $_.Exception.Message)
            }
        }
    }

    return [pscustomobject]@{ Removed = $removed; Skipped = $skipped }
    }

function Remove-VendorConsole {
    param([string]$SnapshotDirectory)
    try {
        return Remove-VendorConsoleCore -SnapshotDirectory $SnapshotDirectory
    }
    catch {
        Write-Log ('  vendor-console removal failed (non-fatal): ' + $_.Exception.Message)
        return [pscustomobject]@{
            Removed = New-Object System.Collections.Generic.List[string]
            Skipped = New-Object System.Collections.Generic.List[string]
        }
    }
}

function Test-VendorConsolePresent {
    # Read-only probe with the same predicate as the removal: uninstall entries, product folders,
    # UWP/MSIX packages. Used to decide whether the verify-only fast path is safe.
    try {
        foreach ($root in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')) {
            if (-not (Test-Path -LiteralPath $root)) { continue }
            foreach ($item in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                if ((Test-VendorArtefactRemovable -Kind 'uninstallkey' -Value $item.PSChildName) -eq 'REMOVABLE') { return $true }
                $props = $null
                try { $props = Get-ItemProperty -LiteralPath $item.PSPath -ErrorAction Stop } catch { continue }
                $display = Get-NotePropertyString -Object $props -Name 'DisplayName'
                if ((Test-VendorArtefactRemovable -Kind 'uninstall' -Value $display) -eq 'REMOVABLE') { return $true }
            }
        }
        foreach ($programRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
            foreach ($allowed in $script:VendorDirectoryAllowList) {
                if (Test-Path -LiteralPath (Join-Path $programRoot $allowed)) { return $true }
            }
        }
        $packages = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object {
            (Test-VendorArtefactRemovable -Kind 'package' -Value $_.Name) -eq 'REMOVABLE' })
        if ($packages.Count -gt 0) { return $true }
    }
    catch { Write-Log ('  vendor console probe failed (treated as present): ' + $_.Exception.Message); return $true }
    return $false
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
        'They are not published: no download server hosts them, so they cannot be fetched automatically.'
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

function Select-GcuMqttPort {
    # Pure: which of the bridge's listening ports is the MQTT broker. Every decompiled console/service
    # generation uses 13688; the legacy bridge's method bodies are encrypted, so its port is taken
    # from what it actually listens on (the service's own WCF endpoint 58594 is never the broker).
    param([int[]]$Ports)
    $candidates = @($Ports | Where-Object { $_ -gt 0 -and $_ -ne 58594 } | Sort-Object -Unique)
    if ($candidates -contains 13688) { return 13688 }
    if ($candidates.Count -gt 0) { return [int]$candidates[0] }
    return 0
}

function Get-GcuBridgeListenPorts {
    if (-not (Get-Command -Name Get-NetTCPConnection -ErrorAction SilentlyContinue)) { return @() }
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $script:ServiceName) -ErrorAction SilentlyContinue
    if ($null -eq $service -or -not $service.ProcessId) { return @() }
    return @(Get-NetTCPConnection -State Listen -OwningProcess ([int]$service.ProcessId) -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty LocalPort -Unique)
}

function Get-GcuPostInstallFacts {
    param([switch]$Legacy)
    $services = @()
    try {
        $services = @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop |
            Where-Object { $_.PathName -and ($_.PathName -match '(?i)\\GCU\\') } |
            Select-Object -ExpandProperty Name)
    }
    catch { $services = @() }
    $pids = @()
    $port = 13688
    if ($Legacy) {
        $measured = Select-GcuMqttPort -Ports @(Get-GcuBridgeListenPorts)
        if ($measured -gt 0) { $port = $measured }
    }
    if (Get-Command -Name Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $pids = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
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
        MqttPort = $port
    }
}

function Wait-GcuPostInstallFacts {
    # GCUBridge writes ItemSupport / ServiceReady and binds 13688 after Start-Service returns.
    # A single immediate probe is a field false-fail: setup then exits 1 while the service is
    # still coming up, and the app sits on "GCU connecting" forever.
    param([int]$TimeoutSeconds = 30, [switch]$Legacy)
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $facts = Get-GcuPostInstallFacts -Legacy:$Legacy
    $verdict = Test-GcuPostInstall -ServiceNames $facts.ServiceNames -ListenerPids $facts.ListenerPids `
        -ItemSupportPresent $facts.ItemSupportPresent -ServiceReady $facts.ServiceReady -Legacy:$Legacy
    while (-not $verdict.Ok) {
        if ([datetime]::UtcNow -ge $deadline) { break }
        $reason = (@($verdict.Failed) | ForEach-Object { $_.Name }) -join ','
        Write-Log ("  post-install not ready yet ({0}); retrying" -f $reason)
        Start-Sleep -Seconds 1
        $facts = Get-GcuPostInstallFacts -Legacy:$Legacy
        $verdict = Test-GcuPostInstall -ServiceNames $facts.ServiceNames -ListenerPids $facts.ListenerPids `
            -ItemSupportPresent $facts.ItemSupportPresent -ServiceReady $facts.ServiceReady -Legacy:$Legacy
    }
    return [pscustomobject]@{ Facts = $facts; Verdict = $verdict }
}

function Set-GcuMqttPortMarker {
    # The app connects to the port recorded here (13688 when absent). Written only from a measured
    # listener, so a wrong guess can never redirect the app.
    param([int]$Port)
    if ($Port -le 0) { return }
    try {
        $key = 'HKLM:\SOFTWARE\L-Mechrevo'
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -LiteralPath $key -Name 'GcuMqttPort' -PropertyType DWord -Value $Port -Force | Out-Null
        Write-Log ('  GCU MQTT port recorded: {0}' -f $Port)
    }
    catch { Write-Log ('  WARNING: could not record the GCU MQTT port: ' + $_.Exception.Message) }
    # A broker on another port (legacy bridge) needs its own inbound block rule.
    if ($Port -ne 13688) {
        try { Ensure-FirewallRule -Port $Port }
        catch { Write-Log ('  WARNING: could not block TCP {0}: {1}' -f $Port, $_.Exception.Message) }
    }
}

function Set-RebootRequiredMarker {
    # Read (and cleared) by the Inno NeedRestart hook: only a driver install that asked for a reboot
    # makes setup offer one, instead of every install nagging for a restart.
    param([string]$Reason)
    try {
        $key = 'HKLM:\SOFTWARE\L-Mechrevo'
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -LiteralPath $key -Name 'RebootRequired' -PropertyType DWord -Value 1 -Force | Out-Null
        Write-Log ('  reboot required: ' + $Reason)
    }
    catch { Write-Log ('  WARNING: could not record the reboot request: ' + $_.Exception.Message) }
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

function Install-AcpiDriver {
    # Installs the payload's own EC/ACPI driver: UWACPIDriver.inf (newest payload) or ACPIDriver.inf
    # (GamingCenterU legacy payload). Both bind ACPI\INOU0000 and expose \\.\ACPIDriver.
    # Returns $true when pnputil reports that a reboot is needed to finish (exit 3010).
    param([string]$DriverDir, [string]$InfName = 'UWACPIDriver.inf')
    $inf = Join-Path $DriverDir $InfName
    if (-not (Test-Path -LiteralPath $inf)) {
        throw ("{0} not found: {1}" -f $InfName, $inf)
    }
    Write-Log ("  pnputil /add-driver `"{0}`" /install" -f $inf)
    $result = Invoke-Native -FilePath 'pnputil.exe' -Arguments @('/add-driver', $inf, '/install')
    $code = $result.ExitCode
    $result.Output | ForEach-Object { Write-Log ("    " + $_) }
    # The store name of OUR package (also printed when it already existed). Uninstall removes exactly
    # this one, never an older copy of the same INF that the OEM image or the vendor console staged.
    $published = @($result.Output | ForEach-Object { [regex]::Match($_, '(?i)\boem\d+\.inf\b') } |
        Where-Object { $_.Success } | ForEach-Object { $_.Value }) | Select-Object -First 1
    $script:InstalledDriverPublishedName = if ($published) { [string]$published } else { $null }
    if ($code -eq 3010) {
        Write-Log '  driver installed; Windows needs a reboot to finish binding it'
        return $true
    }
    if ($code -ne 0) {
        # Idempotency: an already-present driver can make pnputil return non-zero (259 = up to date).
        # File names are not localized, so this check works on any display language.
        $enum = Invoke-Native -FilePath 'pnputil.exe' -Arguments @('/enum-drivers')
        $present = @($enum.Output | Where-Object { $_ -match [regex]::Escape($InfName) }).Count -gt 0
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
    return $false
}

function Install-UwacpiDriver {
    # Kept for callers of the pre-beta21 name.
    param([string]$DriverDir)
    return Install-AcpiDriver -DriverDir $DriverDir -InfName 'UWACPIDriver.inf'
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
    # One rule per blocked port, all sharing the display name (uninstall removes them by name).
    param([int]$Port = 13688)
    $cmd = Get-Command -Name New-NetFirewallRule -ErrorAction SilentlyContinue
    if ($cmd) {
        $existing = @(Get-NetFirewallRule -DisplayName $script:RuleName -ErrorAction SilentlyContinue |
            Where-Object { @(($_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue).LocalPort) -contains [string]$Port })
        if ($existing.Count -gt 0) {
            Write-Log ('  firewall block rule already present (TCP {0})' -f $Port)
        }
        else {
            New-NetFirewallRule -DisplayName $script:RuleName -Direction Inbound -Action Block `
                -Protocol TCP -LocalPort $Port -Profile Any -Enabled True | Out-Null
            Write-Log ('  firewall inbound block rule created (TCP {0})' -f $Port)
        }
        return
    }
    Write-Log '  WARNING: New-NetFirewallRule unavailable; trying netsh fallback'
    if ($Port -eq 13688) { & netsh.exe advfirewall firewall delete rule ("name=" + $script:RuleName) | Out-Null }
    & netsh.exe advfirewall firewall add rule ("name=" + $script:RuleName) dir=in action=block protocol=TCP ("localport=" + $Port) | Out-Null
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
        Write-Log ("  skipping autostart task: app exe not found ({0})" -f $AppExe)
        return $null
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
    Write-Log ("  autostart task registered: {0} (RunLevel=Highest, action='{1}' Argument=startup)" -f $taskName, $AppExe)
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

function Revoke-AppDirectoryUserAcl {
    # Installers before beta21 granted the installing user Modify on {app} and {app}\GCU. An explicit
    # per-user ACE also applies to that user's FILTERED (non-elevated) token, so any process of the
    # user could replace L-Mechrevo.exe - which the Highest autostart task and the SYSTEM charge task
    # then run elevated: a silent UAC bypass. It also made the app refuse to register the charge task
    # (ExecutableTrust sees a user-writable image). Remove those grants; keep everything inherited.
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    # Same bits as ExecutableTrust.ImageReplacingRights: anything that lets a user replace a file.
    $replacing = [System.Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership, WriteAttributes, WriteExtendedAttributes'
    $acl = Get-Acl -LiteralPath $Path
    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($rule in @($acl.Access)) {
        if ($rule.IsInherited -or [string]$rule.AccessControlType -ne 'Allow') { continue }
        $sid = $null
        try { $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $sid = $null }
        # Only ordinary accounts (S-1-5-21-...); Administrators, SYSTEM, TrustedInstaller, Users-read stay.
        if ([string]::IsNullOrEmpty($sid) -or -not $sid.StartsWith('S-1-5-21-')) { continue }
        if (($rule.FileSystemRights -band $replacing) -eq 0) { continue }
        [void]$acl.RemoveAccessRuleSpecific($rule)
        $removed.Add([string]$rule.IdentityReference)
    }
    if ($removed.Count -eq 0) { return }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Write-Log ("  ACL revoked (user write grant from an older installer): {0} -> {1}" -f ($removed -join ', '), $Path)
}

function Enable-LMechrevoTasks {
    # Setup disables every LMechrevo* task before replacing files (StopLockedAppProcesses), because a
    # Highest task with RestartCount would relaunch the app mid-copy. Nothing re-enabled the SYSTEM
    # charge-limit task afterwards, so it stayed disabled after every upgrade.
    foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'LMechrevo*' })) {
        if ([string]$task.State -ne 'Disabled') { continue }
        try {
            Enable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null
            Write-Log ('  scheduled task re-enabled: ' + $task.TaskName)
        }
        catch { Write-Log ('  WARNING: could not re-enable scheduled task {0}: {1}' -f $task.TaskName, $_.Exception.Message) }
    }
}

function Invoke-PrivilegeStep {
    # N5: privileges are acquired here, while setup is elevated - on EVERY install path, including
    # the verify-only fast path (which previously skipped it and left the tasks disabled).
    param([string]$AppExe, [string]$TargetDir, [string]$ConfigDir, [string]$LogDir, [string]$DriverSysName)
    if ([string]::IsNullOrWhiteSpace($AppExe)) {
        $AppExe = Join-Path (Split-Path -Parent $TargetDir) 'L-Mechrevo.exe'
    }
    Register-AutostartTask -AppExe $AppExe | Out-Null
    Enable-LMechrevoTasks
    # The install and payload directories stay admin-only (Program Files default ACL).
    Revoke-AppDirectoryUserAcl -Path (Split-Path -Parent $TargetDir)
    Revoke-AppDirectoryUserAcl -Path $TargetDir
    # Only the per-user config and the shared log directory are writable for the user.
    if ([string]::IsNullOrWhiteSpace($ConfigDir)) {
        $ConfigDir = Join-Path $env:APPDATA 'MechrevoLite'
    }
    Grant-AppDirectoryAcl -Path $ConfigDir
    Grant-AppDirectoryAcl -Path $LogDir
    Grant-AcpiDriverAccess -DriverSysName $DriverSysName
}

function Grant-AcpiDriverAccess {
    # \\.\ACPIDriver is opened by the app for the read-only EC snapshot and the charge-limit write.
    # Running elevated already covers it; this only makes the grant explicit and idempotent so a
    # future non-elevated consumer is not silently denied. No EC/firmware write is added here.
    param([string]$DriverSysName = 'UWACPIDriver.sys')
    $driver = Join-Path $env:SystemRoot ('System32\drivers\' + $DriverSysName)
    if (-not (Test-Path -LiteralPath $driver)) {
        Write-Log ('  {0} not present; skipping device ACL (driver install step owns it)' -f $DriverSysName)
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
    param([pscustomobject]$Selection, [string]$ServiceExe, [string]$PayloadSha256, [string]$InstallerVersion, [string]$DriverInf)
    try {
        $key = 'HKLM:\SOFTWARE\L-Mechrevo'
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -LiteralPath $key -Name 'GcuVariant' -Value $Selection.Variant
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceDir' -Value $Selection.ServiceDir
        Set-ItemProperty -LiteralPath $key -Name 'GcuServiceExe' -Value $ServiceExe
        if (-not [string]::IsNullOrWhiteSpace($DriverInf)) {
            Set-ItemProperty -LiteralPath $key -Name 'GcuDriverInf' -Value $DriverInf
        }
        if (-not [string]::IsNullOrWhiteSpace($script:InstalledDriverPublishedName)) {
            Set-ItemProperty -LiteralPath $key -Name 'GcuDriverPublishedName' -Value $script:InstalledDriverPublishedName
        }
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
    # -SinglePayload is accepted for old callers but no longer forwarded: the selector has no such
    # parameter since beta21, and passing it made the selection fail outright.
    $selectorArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $selectorScript, '-Variant', $Variant, '-AsJson')
    $selectionJson = & $selectorExe @selectorArgs
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($selectionJson | Out-String))) {
        throw ("GCU payload selection failed (exit {0})" -f $LASTEXITCODE)
    }
    $selection = ($selectionJson | Out-String) | ConvertFrom-Json
    Write-Log ("Selection   = {0}-series / {1} ({2})" -f $selection.Generation, $selection.Variant, $selection.Reason)

    # Each payload carries its own driver: UWACPIDriver (newest) or ACPIDriver (GamingCenterU legacy).
    $driverDirName = if ($selection.PSObject.Properties['DriverDir'] -and $selection.DriverDir) { [string]$selection.DriverDir } else { 'UWACPIDriver' }
    $driverInfName = if ($selection.PSObject.Properties['DriverInf'] -and $selection.DriverInf) { [string]$selection.DriverInf } else { 'UWACPIDriver.inf' }
    $driverSysName = if ($selection.PSObject.Properties['DriverSys'] -and $selection.DriverSys) { [string]$selection.DriverSys } else { 'UWACPIDriver.sys' }
    $legacyPayload = [bool]($selection.PSObject.Properties['Legacy'] -and $selection.Legacy)
    Write-Log ("Driver      = {0}\{1}{2}" -f $driverDirName, $driverInfName, $(if ($legacyPayload) { ' (legacy service)' } else { '' }))

    $serviceSource = Join-Path (Join-Path $StagingRoot $selection.StagedDir) $selection.ServiceDir
    $serviceTarget = Join-Path $TargetDir $selection.ServiceDir
    $driverSource = Join-Path (Join-Path $StagingRoot $selection.StagedDir) $driverDirName
    if (-not (Test-Path -LiteralPath (Join-Path $driverSource $driverInfName))) {
        $driverSource = Join-Path (Join-Path $StagingRoot 'payload\common') $driverDirName
    }
    $driverTarget = Join-Path $TargetDir $driverDirName
    $serviceExe = Join-Path $serviceTarget 'GCUBridge.exe'
    $gcuServiceExe = Join-Path (Join-Path $serviceTarget 'MyControlCenter') 'GCUService.exe'
    $driverSys = Join-Path $driverTarget $driverSysName
    if ([string]::IsNullOrWhiteSpace($ConfigDir)) { $ConfigDir = Join-Path $env:APPDATA 'MechrevoLite' }

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

    # First system change: close the user-write grants older installers left on {app} / {app}\GCU,
    # before anything (the user-state backup included) is written below them.
    if (-not $DryRun) {
        Revoke-AppDirectoryUserAcl -Path (Split-Path -Parent $TargetDir)
        Revoke-AppDirectoryUserAcl -Path $TargetDir
    }

    # Fast path: already installed, healthy and the same payload identity -> verify only.
    $currentBinPath = Get-ServiceBinPath
    $alreadyCurrent = (Test-ExpectedBinPath -PathName $currentBinPath -ExpectedExe $serviceExe) -and `
        (Test-Path -LiteralPath $serviceExe) -and (Test-Path -LiteralPath $gcuServiceExe) -and (Test-Path -LiteralPath $driverSys)
    $currentIdentitySha = if (Test-Path -LiteralPath $gcuServiceExe) { (Get-GcuPayloadIdentity -ServiceDir $serviceTarget).Sha256 } else { $null }
    $action = Get-InstallAction -AlreadyCurrent $alreadyCurrent -InstalledSha256 $currentIdentitySha -IncomingSha256 $payloadIdentity.Sha256

    if ($action -eq 'VerifyOnly' -and -not $DryRun) {
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        # A vendor console installed since the last run must go through the full path: its
        # uninstaller deletes the GCUBridge service by name, which would take ours with it.
        $vendorPresent = Test-VendorConsolePresent
        if ($vendorPresent) { Write-Log 'official console artefacts found; running the full install to remove them first' }
        if ($service -and $service.Status -eq 'Running' -and -not $vendorPresent) {
            Write-Log 'already installed with the same payload identity and RUNNING; verifying signatures, firewall, and post-install facts'
            Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
            Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
            Assert-SignedFile -Path $driverSys -Label $driverDirName
            Ensure-FirewallRule
            Set-InstallMarker -Selection $selection -ServiceExe $serviceExe -PayloadSha256 $payloadIdentity.Sha256 -InstallerVersion $InstallerVersion -DriverInf $driverInfName
            Write-Log 'privileges: autostart task, scheduled tasks, directory ACLs, device access'
            Invoke-PrivilegeStep -AppExe $AppExe -TargetDir $TargetDir -ConfigDir $ConfigDir -LogDir $LogDir -DriverSysName $driverSysName
            # Service Running is not enough: ItemSupport / ServiceReady / 13688 can still be missing.
            Write-Log 'post-install verification (single service / 13688 owner / ItemSupport / ServiceReady)'
            $waited = Wait-GcuPostInstallFacts -TimeoutSeconds 30 -Legacy:$legacyPayload
            $verdict = $waited.Verdict
            if ($verdict.Ok) { Set-GcuMqttPortMarker -Port $waited.Facts.MqttPort }
            $resolvedStatusDir = if (-not [string]::IsNullOrWhiteSpace($StatusDir)) { $StatusDir } else { $LogDir }
            $verdictReason = (@($verdict.Checks) | ForEach-Object { ('{0}={1}' -f $_.Name, $_.Ok) }) -join ' '
            $statusPath = Write-GcuInstallStatus -StatusDir $resolvedStatusDir -Status $(if ($verdict.Ok) { 'ready' } else { 'failed' }) -Reason $verdictReason -Checks $verdict.Checks
            if (-not $verdict.Ok) {
                $reasons = (@($verdict.Failed) | ForEach-Object { ('{0}: {1}' -f $_.Name, $_.Detail) }) -join '; '
                Write-Log ('FATAL: post-install verification failed: ' + $reasons)
                Write-Log ('FATAL: see ' + $script:LogFile + ' (gcu-install-*.log)')
                $guidance = Get-GcuFallbackGuidance
                foreach ($line in $guidance.Lines) { Write-Log ('FALLBACK: ' + $line) }
                exit 1
            }
            Write-Log ('GCU install OK (no changes needed); status file: ' + $statusPath)
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
        # Snapshots go under {app}\GCU (admin-only), never into a user-writable temp directory.
        $vendorResult = Remove-VendorConsole -SnapshotDirectory $TargetDir
        Write-Log ('  vendor-console removal done: {0} removed, {1} skipped' -f $vendorResult.Removed.Count, $vendorResult.Skipped.Count)
        if ($vendorResult.Skipped.Count -gt 0) {
            Write-Log '  the following vendor artefacts could not be removed and need a manual step or a reboot:'
            foreach ($item in $vendorResult.Skipped) { Write-Log ('    - ' + $item) }
            Set-RebootRequiredMarker -Reason 'locked official console components are removed on the next start'
        }
    }

    # Overlay install: payload files are refreshed, the vendor service's runtime user state
    # (per-mode profiles, user fan curves, feature settings) is carried over unchanged.
    Write-Log ('[3/8] copying {0} payload (overlay; the service user state is kept)' -f $selection.Variant)
    $savedState = if ($DryRun) { $null } else { Save-GcuUserState -ServiceDir $serviceTarget -BackupDir (Join-Path $TargetDir 'state-backup') }
    Copy-Tree -Source $serviceSource -Destination $serviceTarget
    Restore-GcuUserState -Saved $savedState -ServiceDir $serviceTarget
    Copy-Tree -Source $driverSource -Destination $driverTarget

    Write-Log '[4/8] verifying Authenticode signatures'
    Assert-SignedFile -Path $serviceExe -Label 'GCUBridge'
    Assert-SignedFile -Path $gcuServiceExe -Label 'GCUService'
    Assert-SignedFile -Path $driverSys -Label $driverDirName

    if ($DryRun) {
        Write-Log 'DRY RUN: copied + signature-verified only; skipping driver/service/firewall changes'
        Write-Log ("DRY RUN output: {0}" -f $serviceTarget)
        exit 0
    }

    Write-Log ('[5/8] installing the {0} kernel driver' -f $driverDirName)
    if (Install-AcpiDriver -DriverDir $driverTarget -InfName $driverInfName) {
        Set-RebootRequiredMarker -Reason ('{0} asked for a reboot to finish binding' -f $driverInfName)
    }

    Write-Log '[6/8] registering GCUBridge service'
    Install-Service -ServiceExe $serviceExe

    Write-Log '[7/8] firewall rule + service start'
    Ensure-FirewallRule
    if ($NoStart) {
        Write-Log '  -NoStart set; service left stopped'
    }
    else {
        Start-GcuService
    }

    Set-InstallMarker -Selection $selection -ServiceExe $serviceExe -PayloadSha256 $payloadIdentity.Sha256 -InstallerVersion $InstallerVersion -DriverInf $driverInfName

    # Autostart must be registered even if GCU verification later fails. Previously this
    # ran after `exit 1`, so a ServiceReady miss left the machine with no boot task.
    Write-Log '[8/8] privileges: autostart task (highest), scheduled tasks, directory ACLs, device access'
    Invoke-PrivilegeStep -AppExe $AppExe -TargetDir $TargetDir -ConfigDir $ConfigDir -LogDir $LogDir -DriverSysName $driverSysName

    if (-not $NoStart) {
        Write-Log 'post-install verification (single service / 13688 owner / ItemSupport / ServiceReady)'
        $waited = Wait-GcuPostInstallFacts -TimeoutSeconds 30 -Legacy:$legacyPayload
        $facts = $waited.Facts
        $verdict = $waited.Verdict
        if ($verdict.Ok) { Set-GcuMqttPortMarker -Port $facts.MqttPort }
        $resolvedStatusDir = if (-not [string]::IsNullOrWhiteSpace($StatusDir)) { $StatusDir } else { $LogDir }
        $verdictReason = (@($verdict.Checks) | ForEach-Object { ('{0}={1}' -f $_.Name, $_.Ok) }) -join ' '
        $statusPath = Write-GcuInstallStatus -StatusDir $resolvedStatusDir -Status $(if ($verdict.Ok) { 'ready' } else { 'failed' }) -Reason $verdictReason -Checks $verdict.Checks
        if (-not $verdict.Ok) {
            $reasons = (@($verdict.Failed) | ForEach-Object { ('{0}: {1}' -f $_.Name, $_.Detail) }) -join '; '
            Write-Log ('FATAL: post-install verification failed: ' + $reasons)
            Write-Log ('FATAL: see ' + $script:LogFile + ' (gcu-install-*.log)')
            $guidance = Get-GcuFallbackGuidance
            foreach ($line in $guidance.Lines) { Write-Log ('FALLBACK: ' + $line) }
            exit 1
        }
        Write-Log ('post-install verification OK; status file: ' + $statusPath)
    }

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
