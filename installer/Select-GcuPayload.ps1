#Requires -Version 5.1
# ASCII-only by design: Windows PowerShell 5.1 reads .ps1 as ANSI unless it has a BOM,
# and this file is executed both by the installer and by developers on GBK systems.
<#
.SYNOPSIS
    Maps the local hardware to the GCU payload directory bundled by the L-Mechrevo installer.

.DESCRIPTION
    Two modes:

    Multi-payload (default while G0 is unpassed):
        Bundles four vendor trees and picks one by GPU generation:
            release\GCU-only        -> 50-series (RTX 50xx / Blackwell)   staged as payload\50
            release\GCU-40-51751    -> 40-series, AiStoneService variant   staged as payload\40-51751
            release\GCU-40-51749    -> 40-series, UniwillService variant   staged as payload\40-51749
            release\GCU-common      -> shared UWACPIDriver                 staged as payload\common

    Single-payload (-SinglePayload / #ifdef SingleGcuPayload):
        Ships only release\GCU-only and serves every supported generation (30/40/50) with it.
        The retired 40-series trees are gone and the broken PH4*/PH6* platform->generation
        heuristic is not consulted: axis 1 (platform code) must not decide axis 2 (dGPU
        generation). Detection is GPU-name / NVIDIA PCI device-id only; an undeterminable
        generation exits non-zero with a readable reason and NEVER falls back to a 40-series
        payload. -Variant / /GCUVARIANT is refused because no second payload exists.

        G0 gate: single-payload mode may only be enabled once real 30-series AND 40-series
        hardware proves the 1.2.0.0 payload serves them. Until then the default stays
        multi-payload and the 40-series trees stay bundled.

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
    [ValidateSet('Auto', '50', '40-51749', '40-51751')]
    [string]$Variant = 'Auto',
    [string]$OutputFile,
    [switch]$AsJson,
    [switch]$SinglePayload,
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0

# --- bundle (what the installer stages) -------------------------------------
$script:MultiBundle = @(
    [pscustomobject]@{ Key = '50';       RepoPayload = 'release\GCU-only' }
    [pscustomobject]@{ Key = '40-51749'; RepoPayload = 'release\GCU-40-51749' }
    [pscustomobject]@{ Key = '40-51751'; RepoPayload = 'release\GCU-40-51751' }
    [pscustomobject]@{ Key = 'common';   RepoPayload = 'release\GCU-common' }
)
$script:SingleBundle = @(
    [pscustomobject]@{ Key = '50';       RepoPayload = 'release\GCU-only' }
)

# --- selection (which tree the installer copies) ----------------------------
$script:PayloadByVariant = [ordered]@{
    '50'       = [pscustomobject]@{ Variant = '50';       Generation = '50'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-only';      StagedDir = 'payload\50' }
    '40-51751' = [pscustomobject]@{ Variant = '40-51751'; Generation = '40'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-40-51751'; StagedDir = 'payload\40-51751' }
    '40-51749' = [pscustomobject]@{ Variant = '40-51749'; Generation = '40'; ServiceDir = 'UniwillService'; RepoPayload = 'release\GCU-40-51749'; StagedDir = 'payload\40-51749' }
}
$script:SinglePayloadEntry = [pscustomobject]@{
    Variant = '50'; Generation = '50'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-only'; StagedDir = 'payload\50'
}

function Get-GcuPayloadBundle {
    param([switch]$SinglePayload)
    if ($SinglePayload) { return $script:SingleBundle }
    return $script:MultiBundle
}

function Assert-GcuPayloadDirs {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$SinglePayload
    )
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @(Get-GcuPayloadBundle -SinglePayload:$SinglePayload)) {
        $dir = Join-Path $Root $entry.RepoPayload
        if (-not (Test-Path -LiteralPath $dir)) { $missing.Add($dir) }
    }
    if ($missing.Count -gt 0) {
        throw ("GCU payload directory missing: {0}" -f ($missing -join ', '))
    }
    return @(Get-GcuPayloadBundle -SinglePayload:$SinglePayload)
}

function Get-NvidiaGeneration {
    param(
        [string[]]$Ids,
        [string[]]$Names,
        [string]$BiosProject
    )

    $evidence = New-Object System.Collections.Generic.List[string]
    $is50 = $false
    $is40 = $false

    foreach ($name in @($Names)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -match '(?i)\bRTX\s*50[5-9]\d\b') {
            $is50 = $true
            $evidence.Add("gpu-name '$name' matches RTX 50[5-9]x")
        }
        elseif ($name -match '(?i)\bRTX\s*40[5-9]\d\b') {
            $is40 = $true
            $evidence.Add("gpu-name '$name' matches RTX 40[5-9]x")
        }
    }

    foreach ($id in @($Ids)) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        $match = [regex]::Match($id, '(?i)DEV_([0-9A-F]{4})')
        if (-not $match.Success) { continue }
        $dev = [Convert]::ToInt32($match.Groups[1].Value, 16)
        $high = $dev -band 0xFF00
        if (@(0x2B00, 0x2C00, 0x2D00, 0x2E00, 0x2F00) -contains $high) {
            $is50 = $true
            $evidence.Add(("pci-device {0} is Blackwell-class (0x{1:X4})" -f $id, $dev))
        }
        elseif (@(0x2600, 0x2700, 0x2800) -contains $high) {
            $is40 = $true
            $evidence.Add(("pci-device {0} is Ada-class (0x{1:X4})" -f $id, $dev))
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($BiosProject)) {
        if ($BiosProject -match '(?i)^PH6') {
            $is50 = $true
            $evidence.Add("BIOS project id '$BiosProject' is a PH6 (50-series) platform")
        }
        elseif ($BiosProject -match '(?i)^PH4') {
            $is40 = $true
            $evidence.Add("BIOS project id '$BiosProject' is a PH4 (40-series) platform")
        }
    }

    $generation = if ($is50) { '50' } elseif ($is40) { '40' } else { 'unknown' }
    return [pscustomobject]@{
        Generation = $generation
        Is50       = $is50
        Is40       = $is40
        Evidence   = $evidence
    }
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

function Select-SingleGcuPayload {
    param(
        [string]$Variant = 'Auto',
        [string[]]$DeviceId,
        [string]$BiosProjectId,
        [string[]]$GpuName,
        [switch]$Probe
    )

    if ($Variant -ne 'Auto') {
        throw ("single-payload mode: -Variant '{0}' is retired; -Variant / /GCUVARIANT is no longer supported because only the 50-series GCU-only payload is shipped (G0)." -f $Variant)
    }

    # Auto-probe only when the caller supplied no hardware data at all. A BIOS project id alone
    # does not count as hardware data: axis 1 cannot stand in for the dGPU generation.
    if ($Probe -or ((-not $DeviceId -or $DeviceId.Count -eq 0) -and (-not $GpuName -or $GpuName.Count -eq 0) -and [string]::IsNullOrWhiteSpace($BiosProjectId))) {
        $live = Get-LiveProbe
        if (-not $DeviceId -or $DeviceId.Count -eq 0) { $DeviceId = $live.DeviceId }
        if (-not $GpuName -or $GpuName.Count -eq 0) { $GpuName = $live.GpuName }
    }

    $detected = Get-GpuGeneration -Ids $DeviceId -Names $GpuName
    if (@('30', '40', '50') -notcontains $detected.Generation) {
        $observedNames = New-Object System.Collections.Generic.List[string]
        foreach ($name in @($GpuName)) {
            if (-not [string]::IsNullOrWhiteSpace($name)) { $observedNames.Add([string]$name) }
        }
        $observed = if ($observedNames.Count -gt 0) { ($observedNames -join ', ') } else { '(none)' }
        throw ("cannot determine the NVIDIA dGPU generation (30/40/50) from the GPU name or PCI device id (observed: {0}); refusing to install the GCU payload - no fallback. G0 must prove the 50-series payload serves every supported generation." -f $observed)
    }

    $entry = $script:SinglePayloadEntry
    return [pscustomobject]@{
        Generation  = $detected.Generation
        Variant     = $entry.Variant
        ServiceDir  = $entry.ServiceDir
        RepoPayload = $entry.RepoPayload
        StagedDir   = $entry.StagedDir
        Reason      = ("single-payload mode: {0}-series detected -> {1} (G0)" -f $detected.Generation, $entry.RepoPayload)
        Evidence    = $detected.Evidence
    }
}

function Select-GcuPayload {
    [CmdletBinding()]
    param(
        [string]$Variant = 'Auto',
        [string[]]$DeviceId,
        [string]$BiosProjectId,
        [string[]]$GpuName,
        [switch]$Probe,
        [switch]$SinglePayload
    )

    if ($SinglePayload) {
        return Select-SingleGcuPayload -Variant $Variant -DeviceId $DeviceId -BiosProjectId $BiosProjectId -GpuName $GpuName -Probe:$Probe
    }

    if ($Variant -ne 'Auto') {
        $entry = $script:PayloadByVariant[$Variant]
        if ($null -eq $entry) { throw ("unknown -Variant '{0}'" -f $Variant) }
        return [pscustomobject]@{
            Generation = $entry.Generation
            Variant    = $entry.Variant
            ServiceDir = $entry.ServiceDir
            RepoPayload = $entry.RepoPayload
            StagedDir  = $entry.StagedDir
            Reason     = "explicit override -Variant $Variant"
            Evidence   = @("explicit override")
        }
    }

    if ($Probe -or ((-not $DeviceId -or $DeviceId.Count -eq 0) -and (-not $GpuName -or $GpuName.Count -eq 0) -and [string]::IsNullOrWhiteSpace($BiosProjectId))) {
        $live = Get-LiveProbe
        if (-not $DeviceId -or $DeviceId.Count -eq 0) { $DeviceId = $live.DeviceId }
        if (-not $GpuName -or $GpuName.Count -eq 0) { $GpuName = $live.GpuName }
        if ([string]::IsNullOrWhiteSpace($BiosProjectId)) { $BiosProjectId = $live.BiosProjectId }
    }

    $detected = Get-NvidiaGeneration -Ids $DeviceId -Names $GpuName -BiosProject $BiosProjectId

    if ($detected.Generation -eq '50') {
        $variant = '50'
        $reason = '50-series evidence present -> GCU-only payload'
    }
    else {
        $variant = '40-51751'
        if ($detected.Generation -eq '40') {
            $reason = '40-series evidence present -> AiStoneService payload (51751 default: no reliable signal distinguishes 51749 vs 51751)'
        }
        else {
            $reason = 'no 50-series evidence -> 40-series AiStoneService payload (51751 default; use -Variant 40-51749 to override)'
        }
    }

    $entry = $script:PayloadByVariant[$variant]
    return [pscustomobject]@{
        Generation  = $entry.Generation
        Variant     = $entry.Variant
        ServiceDir  = $entry.ServiceDir
        RepoPayload = $entry.RepoPayload
        StagedDir   = $entry.StagedDir
        Reason      = $reason
        Evidence    = $detected.Evidence
    }
}

function Invoke-SelfTest {
    $cases = @(
        @{ Name = 'RTX 5080 laptop -> 50';                    Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2C19&SUBSYS_60411D05&REV_A1'); GpuName = @('NVIDIA GeForce RTX 5080 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 5090 desktop -> 50';                   Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2B85'); GpuName = @('NVIDIA GeForce RTX 5090') }; Expect = '50' },
        @{ Name = 'RTX 4090 laptop -> 40-51751';              Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2717'); GpuName = @('NVIDIA GeForce RTX 4090 Laptop GPU') }; Expect = '40-51751' },
        @{ Name = 'RTX 4070 laptop -> 40-51751';              Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2820'); GpuName = @('NVIDIA GeForce RTX 4070 Laptop GPU') }; Expect = '40-51751' },
        @{ Name = 'BIOS PH6TRX1 -> 50';                       Args = @{ BiosProjectId = 'PH6TRX1' }; Expect = '50' },
        @{ Name = 'BIOS PH4TQx1 -> 40-51751';                 Args = @{ BiosProjectId = 'PH4TQx1' }; Expect = '40-51751' },
        @{ Name = 'unknown Intel-only -> 40-51751 fallback';  Args = @{ DeviceId = @('PCI\VEN_8086&DEV_7D67'); GpuName = @('Intel(R) Graphics') }; Expect = '40-51751' },
        @{ Name = 'explicit override 40-51749';               Args = @{ Variant = '40-51749' }; Expect = '40-51749' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $p = @{ Variant = 'Auto'; DeviceId = @(); GpuName = @(); BiosProjectId = '' }
        foreach ($key in $case.Args.Keys) { $p[$key] = $case.Args[$key] }
        $result = Select-GcuPayload -Variant $p.Variant -DeviceId $p.DeviceId -GpuName $p.GpuName -BiosProjectId $p.BiosProjectId
        $ok = ($result.Variant -eq $case.Expect)
        if (-not $ok) { $failed++ }
        $mark = if ($ok) { 'PASS' } else { 'FAIL' }
        Write-Host ("  [{0}] {1} -> {2} (expected {3})" -f $mark, $case.Name, $result.Variant, $case.Expect)
    }
    Write-Host ("SelfTest: {0} case(s), {1} failed" -f $cases.Count, $failed)
    return ($failed -eq 0)
}

function Invoke-SelfTestSingle {
    $cases = @(
        @{ Name = 'RTX 5080 -> single GCU-only';       Args = @{ GpuName = @('NVIDIA GeForce RTX 5080 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 4090 -> single GCU-only';       Args = @{ GpuName = @('NVIDIA GeForce RTX 4090 Laptop GPU') }; Expect = '50' },
        @{ Name = 'RTX 3080 -> single GCU-only';       Args = @{ GpuName = @('NVIDIA GeForce RTX 3080 Laptop GPU') }; Expect = '50' },
        @{ Name = 'PCIE 2C19 -> single GCU-only';      Args = @{ DeviceId = @('PCI\VEN_10DE&DEV_2C19') }; Expect = '50' },
        @{ Name = 'retired 40-51749 override';         Args = @{ Variant = '40-51749' }; Expect = 'THROW' },
        @{ Name = 'Intel-only must not fall back';     Args = @{ GpuName = @('Intel(R) Graphics') }; Expect = 'THROW' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $p = @{ Variant = 'Auto'; DeviceId = @(); GpuName = @() }
        foreach ($key in $case.Args.Keys) { $p[$key] = $case.Args[$key] }
        $threw = $false
        $actual = ''
        try {
            $result = Select-SingleGcuPayload -Variant $p.Variant -DeviceId $p.DeviceId -GpuName $p.GpuName
            $actual = $result.Variant
        }
        catch {
            $threw = $true
            $actual = 'THROW'
        }
        $ok = ($actual -eq $case.Expect)
        if (-not $ok) { $failed++ }
        $mark = if ($ok) { 'PASS' } else { 'FAIL' }
        Write-Host ("  [{0}] {1} -> {2} (expected {3})" -f $mark, $case.Name, $actual, $case.Expect)
    }
    Write-Host ("SelfTest (single-payload): {0} case(s), {1} failed" -f $cases.Count, $failed)
    return ($failed -eq 0)
}

if ($MyInvocation.InvocationName -eq '.') {
    # Dot-sourced (e.g. by Install-Gcu.ps1 or Build-Installer.ps1): expose the functions only.
    return
}

if ($SelfTest) {
    $ok = if ($SinglePayload) { Invoke-SelfTestSingle } else { Invoke-SelfTest }
    if (-not $ok) { exit 1 }
    exit 0
}

try {
    $selection = Select-GcuPayload -Variant $Variant -DeviceId $DeviceId -BiosProjectId $BiosProjectId -GpuName $GpuName -SinglePayload:$SinglePayload
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
