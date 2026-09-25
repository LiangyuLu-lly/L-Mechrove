#Requires -Version 5.1
# ASCII-only by design: Windows PowerShell 5.1 reads .ps1 as ANSI unless it has a BOM,
# and this file is executed both by the installer and by developers on GBK systems.
<#
.SYNOPSIS
    Maps the local hardware to the GCU payload directory bundled by the L-Mechrevo installer.

.DESCRIPTION
    Single payload (N6, owner decision A): the installer ships ONLY release\GCU-only, which
    carries its own UWACPIDriver, and serves every supported generation (30/40/50) with it.

    The retired 40-series trees are no longer bundled. The broken platform-code -> generation
    heuristic is gone: axis 1 (platform code) must not decide axis 2 (dGPU generation).
    Detection is GPU-name / NVIDIA PCI device-id only and is logged, not a gate: there is only
    one shipped payload, so an undeterminable generation still installs it. There is no
    40-series fallback. -Variant / /GCUVARIANT is refused because no second payload exists.

    The old G0 gate is superseded (owner): the newest GCU is backward compatible to 30-series, the
    vendor ships one GCU/console for all 24 platform codes, and release\GCU-only\...\UserFanTables
    carries all 24 per-model chassis dirs + the 23 flat files while the 40-series payloads carry
    zero per-model dirs - so the newest payload is the superset. The safety net that replaces G0 is
    the post-install/first-run self-check plus the visible fallback (see Install-Gcu.ps1).

.OUTPUTS
    [pscustomobject] with Generation, Variant, ServiceDir, RepoPayload, StagedDir, Reason, Evidence.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1 -SelfTest
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1 -SinglePayload -SelfTest
#>
[CmdletBinding()]
param(
    [string[]]$DeviceId,
    [string]$BiosProjectId,
    [string[]]$GpuName,
    [string]$Variant = 'Auto',
    [string]$OutputFile,
    [switch]$AsJson,
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0

# --- bundle (what the installer stages) -------------------------------------
# N6: exactly one tree. release\GCU-only carries its own UWACPIDriver, so release\GCU-common is
# NOT staged: it is byte-identical to GCU-only\UWACPIDriver (verified: uwacpidriver.cat 11342,
# UWACPIDriver.inf 2034, UWACPIDriver.sys 46352 - same sizes and hashes), so staging it would
# duplicate the driver for no benefit.
$script:Bundle = @(
    [pscustomobject]@{ Key = '50'; RepoPayload = 'release\GCU-only' }
)

# --- selection (which tree the installer copies) ----------------------------
$script:PayloadEntry = [pscustomobject]@{
    Variant = '50'; Generation = '50'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-only'; StagedDir = 'payload\50'
}

function Get-GcuPayloadBundle {
    return $script:Bundle
}

function Assert-GcuPayloadDirs {
    param([Parameter(Mandatory = $true)][string]$Root)
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @(Get-GcuPayloadBundle)) {
        $dir = Join-Path $Root $entry.RepoPayload
        if (-not (Test-Path -LiteralPath $dir)) { $missing.Add($dir) }
    }
    if ($missing.Count -gt 0) {
        throw ("GCU payload directory missing: {0}" -f ($missing -join ', '))
    }
    return @(Get-GcuPayloadBundle)
}

function Get-GpuGeneration {
    # Axis 2 only: dGPU generation from GPU marketing name / NVIDIA PCI device-id high byte.
    # BIOS_PROJECT_ID is deliberately absent - it is axis 1 (platform code) and must never
    # decide the payload. Returns 'unknown' when no supported generation signal is present.
    param(
        [string[]]$Ids,
        [string[]]$Names
    )

    $evidence = New-Object System.Collections.Generic.List[string]
    $generation = 'unknown'

    $deviceClasses = [ordered]@{
        '50' = @(0x2B00, 0x2C00, 0x2D00, 0x2E00, 0x2F00)   # Blackwell
        '40' = @(0x2600, 0x2700, 0x2800)                    # Ada
        '30' = @(0x2200, 0x2400, 0x2500)                    # Ampere (GA10x)
    }

    foreach ($name in @($Names)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        foreach ($generationKey in @('50', '40', '30')) {
            if ($name -match ("(?i)\bRTX\s*{0}[5-9]\d\b" -f $generationKey)) {
                $generation = $generationKey
                $evidence.Add("gpu-name '$name' matches RTX {0}[5-9]x" -f $generationKey)
                break
            }
        }
        if ($generation -ne 'unknown') { break }
    }

    if ($generation -eq 'unknown') {
        foreach ($id in @($Ids)) {
            if ([string]::IsNullOrWhiteSpace($id)) { continue }
            $match = [regex]::Match($id, '(?i)DEV_([0-9A-F]{4})')
            if (-not $match.Success) { continue }
            $dev = [Convert]::ToInt32($match.Groups[1].Value, 16)
            $high = $dev -band 0xFF00
            foreach ($generationKey in @('50', '40', '30')) {
                if ($deviceClasses[$generationKey] -contains $high) {
                    $generation = $generationKey
                    $evidence.Add(("pci-device {0} is {1}-series class (0x{2:X4})" -f $id, $generationKey, $dev))
                    break
                }
            }
            if ($generation -ne 'unknown') { break }
        }
    }

    return [pscustomobject]@{
        Generation = $generation
        Evidence   = $evidence
    }
}

function Get-LiveProbe {
    $ids = New-Object System.Collections.Generic.List[string]
    $names = New-Object System.Collections.Generic.List[string]
    $bios = $null

    try {
        $controllers = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop
    }
    catch {
        $controllers = Get-WmiObject -Class Win32_VideoController -ErrorAction SilentlyContinue
    }
    foreach ($ctrl in @($controllers)) {
        if ($null -eq $ctrl) { continue }
        if ($ctrl.PNPDeviceID -match '(?i)VEN_10DE') {
            $ids.Add([string]$ctrl.PNPDeviceID)
            $names.Add([string]$ctrl.Name)
        }
    }

    if ($ids.Count -eq 0) {
        try {
            $entities = Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction Stop |
                Where-Object { $_.DeviceID -match '(?i)VEN_10DE' }
        }
        catch {
            $entities = @()
        }
        foreach ($entity in @($entities)) {
            if ($entity.Name -match '(?i)nvidia') {
                $ids.Add([string]$entity.DeviceID)
                $names.Add([string]$entity.Name)
            }
        }
    }

    $registryPaths = @(
        'SOFTWARE\OEM\GamingCenter2\ItemSupport',
        'SOFTWARE\OEM\GamingCenter\ItemSupport',
        'SOFTWARE\OEM\ControlCenter\ItemSupport'
    )
    foreach ($path in $registryPaths) {
        try {
            $value = (Get-ItemProperty -LiteralPath ("HKLM:\" + $path) -ErrorAction Stop).BIOS_PROJECT_ID
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) { $bios = [string]$value; break }
        }
        catch { }
    }
    if ([string]::IsNullOrWhiteSpace($bios)) {
        try {
            $product = (Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' -ErrorAction Stop).SystemProductName
            if ($product -match '(?i)\b(PH[46][A-Z0-9]+)\b') { $bios = $Matches[1] }
        }
        catch { }
    }

    return [pscustomobject]@{
        DeviceId      = $ids.ToArray()
        GpuName       = $names.ToArray()
        BiosProjectId = $bios
    }
}

function Select-GcuPayload {
    [CmdletBinding()]
    param(
        [string]$Variant = 'Auto',
        [string[]]$DeviceId,
        [string]$BiosProjectId,
        [string[]]$GpuName,
        [switch]$Probe
    )

    if ($Variant -ne 'Auto') {
        Write-Warning ("-Variant '{0}' is retired: only the newest GCU payload (release\GCU-only) is shipped. /GCUVARIANT no longer has any effect; continuing as Auto." -f $Variant)
        $Variant = 'Auto'
    }

    # Auto-probe only when the caller supplied no hardware data at all. A BIOS project id alone
    # does not count as hardware data: axis 1 cannot stand in for the dGPU generation.
    if ($Probe -or ((-not $DeviceId -or $DeviceId.Count -eq 0) -and (-not $GpuName -or $GpuName.Count -eq 0) -and [string]::IsNullOrWhiteSpace($BiosProjectId))) {
        $live = Get-LiveProbe
        if (-not $DeviceId -or $DeviceId.Count -eq 0) { $DeviceId = $live.DeviceId }
        if (-not $GpuName -or $GpuName.Count -eq 0) { $GpuName = $live.GpuName }
    }

    $detected = Get-GpuGeneration -Ids $DeviceId -Names $GpuName
    $entry = $script:PayloadEntry
    $known = @('30', '40', '50') -contains $detected.Generation
    $generation = if ($known) { $detected.Generation } else { $entry.Generation }
    $reason = if ($known) {
        ("{0}-series detected; installing the shipped GCU payload {1}" -f $generation, $entry.RepoPayload)
    }
    else {
        'installing the shipped GCU payload (no generation selection)'
    }

    return [pscustomobject]@{
        Generation  = $generation
        Variant     = $entry.Variant
        ServiceDir  = $entry.ServiceDir
        RepoPayload = $entry.RepoPayload
        StagedDir   = $entry.StagedDir
        Reason      = $reason
        Evidence    = $detected.Evidence
    }
}

function Invoke-SelfTest {
    # Every input, including undeterminable generation and the retired override, installs
    # the one shipped payload. There is no second tree to fall back to.
    $cases = @(
        @{ Name = 'RTX 5080 laptop -> GCU-only';       Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2C19&SUBSYS_60411D05&REV_A1'); GpuName = @('NVIDIA GeForce RTX 5080 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 5090 desktop -> GCU-only';      Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2B85'); GpuName = @('NVIDIA GeForce RTX 5090') }; Expect = '50' },
        @{ Name = 'RTX 4090 laptop -> GCU-only';       Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2717'); GpuName = @('NVIDIA GeForce RTX 4090 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 4070 laptop -> GCU-only';       Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2820'); GpuName = @('NVIDIA GeForce RTX 4070 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 3080 laptop -> GCU-only';       Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2206'); GpuName = @('NVIDIA GeForce RTX 3080 Laptop GPU') }; Expect = '50' },
        @{ Name = 'PCIE 2C19 -> GCU-only';             Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2C19') }; Expect = '50' },
        @{ Name = 'Intel-only still ships GCU-only';   Args = @{ DeviceId = @('PCI\VEN_8086&DEV_7D67'); GpuName = @('Intel(R) Graphics') }; Expect = '50' },
        @{ Name = 'retired 40-51749 override';         Args = @{ Variant = '40-51749'; DeviceId = @('PCI\VEN_10DE&DEV_2717'); GpuName = @('NVIDIA GeForce RTX 4090 Laptop GPU') }; Expect = '50' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $p = @{ Variant = 'Auto'; DeviceId = @(); GpuName = @(); BiosProjectId = '' }
        foreach ($key in $case.Args.Keys) { $p[$key] = $case.Args[$key] }
        $actual = ''
        try {
            $result = Select-GcuPayload -Variant $p.Variant -DeviceId $p.DeviceId -GpuName $p.GpuName -BiosProjectId $p.BiosProjectId
            $actual = $result.Variant
        }
        catch {
            $actual = 'THROW'
        }
        $ok = ($actual -eq $case.Expect)
        if (-not $ok) { $failed++ }
        $mark = if ($ok) { 'PASS' } else { 'FAIL' }
        Write-Host ("  [{0}] {1} -> {2} (expected {3})" -f $mark, $case.Name, $actual, $case.Expect)
    }
    Write-Host ("SelfTest: {0} case(s), {1} failed" -f $cases.Count, $failed)
    return ($failed -eq 0)
}

if ($MyInvocation.InvocationName -eq '.') {
    # Dot-sourced (e.g. by Install-Gcu.ps1 or Build-Installer.ps1): expose the functions only.
    return
}

if ($SelfTest) {
    if (-not (Invoke-SelfTest)) { exit 1 }
    exit 0
}

try {
    $selection = Select-GcuPayload -Variant $Variant -DeviceId $DeviceId -BiosProjectId $BiosProjectId -GpuName $GpuName
}
catch {
    Write-Host ("GCU payload selection FAILED: " + $_.Exception.Message)
    Write-Error $_
    exit 2
}

if (-not [string]::IsNullOrWhiteSpace($OutputFile)) {
    Set-Content -LiteralPath $OutputFile -Value $selection.Variant -Encoding ASCII -Force
}

if ($AsJson) {
    $selection | ConvertTo-Json -Depth 4
}
else {
    Write-Host ("Generation : {0}-series" -f $selection.Generation)
    Write-Host ("Variant    : {0}" -f $selection.Variant)
    Write-Host ("ServiceDir : {0}" -f $selection.ServiceDir)
    Write-Host ("Selection  : {0}-series -> {1}" -f $selection.Generation, $selection.RepoPayload)
    Write-Host ("Reason     : {0}" -f $selection.Reason)
    if ($selection.Evidence) {
        foreach ($item in $selection.Evidence) { Write-Host ("Evidence   : {0}" -f $item) }
    }
}
