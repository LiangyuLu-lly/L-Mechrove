#Requires -Version 5.1
# ASCII-only by design: Windows PowerShell 5.1 reads .ps1 as ANSI unless it has a BOM,
# and this file is executed both by the installer and by developers on GBK systems.
<#
.SYNOPSIS
    Maps the local GPU generation to one of the GCU payload directories bundled by
    the L-Mechrevo installer.

.DESCRIPTION
    The installer ships four vendor payload trees:
        release\GCU-only        -> 50-series (RTX 50xx / Blackwell)   staged as payload\50
        release\GCU-40-51751    -> 40-series, AiStoneService variant   staged as payload\40-51751
        release\GCU-40-51749    -> 40-series, UniwillService variant   staged as payload\40-51749
        release\GCU-common      -> shared UWACPIDriver                 staged as payload\common

    Generation is decided from, in order of strength:
      1. GPU marketing name (Win32_VideoController / Win32_PnPEntity), e.g. "RTX 5080".
      2. PCI device id high byte: 0x2B/0x2C/0x2D/0x2E/0x2F = Blackwell (50-series),
         0x26/0x27/0x28 = Ada (40-series).
      3. HKLM BIOS project id (BIOS_PROJECT_ID): PH6* = 50-series, PH4* = 40-series.

    40-series sub-variant: there is no reliable machine signal that distinguishes the
    5.17.49.19 (UniwillService) build from the newer 5.17.51.34 (AiStoneService) build
    for the same hardware generation, so 40-series defaults to 40-51751 (AiStoneService,
    which matches the 50-series naming shipped by the vendor). 40-51749 stays bundled and
    reachable via an explicit override: -Variant 40-51749 (installer: /GCUVARIANT=40-51749).

    Unknown hardware falls back to 40-51751 as well, matching the requirement
    "50-series -> GCU-only, otherwise the 40-series AiStoneService payload".

.OUTPUTS
    [pscustomobject] with Generation, Variant, ServiceDir, RepoPayload, StagedDir, Reason, Evidence.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1 -SelfTest
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1 -Variant 40-51749 -OutputFile variant.txt
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
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0

$script:PayloadByVariant = [ordered]@{
    '50'       = [pscustomobject]@{ Variant = '50';       Generation = '50'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-only';      StagedDir = 'payload\50' }
    '40-51751' = [pscustomobject]@{ Variant = '40-51751'; Generation = '40'; ServiceDir = 'AiStoneService'; RepoPayload = 'release\GCU-40-51751'; StagedDir = 'payload\40-51751' }
    '40-51749' = [pscustomobject]@{ Variant = '40-51749'; Generation = '40'; ServiceDir = 'UniwillService'; RepoPayload = 'release\GCU-40-51749'; StagedDir = 'payload\40-51749' }
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
        [ValidateSet('Auto', '50', '40-51749', '40-51751')]
        [string]$Variant = 'Auto',
        [string[]]$DeviceId,
        [string]$BiosProjectId,
        [string[]]$GpuName,
        [switch]$Probe
    )

    if ($Variant -ne 'Auto') {
        $entry = $script:PayloadByVariant[$Variant]
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

if ($MyInvocation.InvocationName -eq '.') {
    # Dot-sourced (e.g. by Install-Gcu.ps1): expose the functions only, run nothing.
    return
}

if ($SelfTest) {
    $ok = Invoke-SelfTest
    if (-not $ok) { exit 1 }
    exit 0
}

$selection = Select-GcuPayload -Variant $Variant -DeviceId $DeviceId -BiosProjectId $BiosProjectId -GpuName $GpuName

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
