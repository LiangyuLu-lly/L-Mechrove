# ASCII-only by design. A child powershell.exe (5.1) reads .ps1 as ANSI, so any
# non-ASCII character here would be corrupted. Keep all comments in English.
#
# Extracts a machine-readable hardware map from the official Mechrevo/Tongfang
# control center payloads that ship in this tree, so the project can eventually
# talk to the EC directly instead of going through the vendor MQTT service.
#
# What it reads (all offline, no device access):
#   1. _decompiled/GCUService/GCUService.decompiled.cs
#      ConfuserEx stubbed every method BODY but left const/enum/struct
#      declarations intact, so the EC register names, addresses, project id
#      enums and bit-flag enums all survive and can be recovered verbatim.
#   2. ControlCenter_*/**/UserFanTables/<PROJECT>/M<mode>T<table>.json
#      Per-model factory fan curves and power limits, in plain JSON.
#   3. ControlCenter_*/**/RGBKeyboard.reg
#      The keyboard / lightbar zone universe (which zone layouts exist at all).
#
# Output: docs/hardware/*.json plus a summary on stdout.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\Extract-HardwareMap.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\Extract-HardwareMap.ps1 -OutputDirectory docs\hardware

[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    # $PSScriptRoot is empty in some nested -File invocations, so fall back to the
    # script path recorded in $MyInvocation before giving up.
    $scriptDirectory = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
        $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Definition
    }
    if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
        throw 'Cannot determine the repository root. Pass -RepositoryRoot explicitly.'
    }
    $RepositoryRoot = Split-Path -Parent $scriptDirectory
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'docs\hardware'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Write-Section([string]$text) {
    Write-Output ''
    Write-Output ('== ' + $text + ' ' + ('=' * [Math]::Max(0, 62 - $text.Length)))
}

function Save-Json($object, [string]$fileName) {
    $path = Join-Path $OutputDirectory $fileName
    # Indented output: these files are meant to be read and diffed by humans.
    $object | ConvertTo-Json -Depth 12 | Set-Content -Path $path -Encoding UTF8
    $kb = [math]::Round((Get-Item $path).Length / 1KB, 1)
    Write-Output ("  wrote {0,-32} {1,8} KB" -f $fileName, $kb)
    return $path
}

# ---------------------------------------------------------------------------
# 1. Decompiled service: constants, enums, structs
# ---------------------------------------------------------------------------

function Get-TypeScopes([string[]]$lines) {
    # Tracks the enclosing type per line using brace depth, so every constant
    # can be attributed to the class/struct that declares it. That attribution
    # is what resolves most duplicate EC addresses: the same address means
    # different things in ECSpec vs RamFan2_ECSpec vs ITE_SPEC.
    $stack = New-Object System.Collections.Generic.List[object]
    $owner = New-Object 'string[]' $lines.Count
    $kind = New-Object 'string[]' $lines.Count
    $depth = 0
    $pending = $null

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        $declaration = [regex]::Match($line, '\b(class|struct|enum|interface)\s+([A-Za-z_]\w*)')
        if ($declaration.Success) {
            $pending = @{ Name = $declaration.Groups[2].Value; Kind = $declaration.Groups[1].Value }
        }

        if ($stack.Count -gt 0) {
            $owner[$i] = $stack[$stack.Count - 1].Name
            $kind[$i] = $stack[$stack.Count - 1].Kind
        }
        else {
            $owner[$i] = '<global>'
            $kind[$i] = 'none'
        }

        $opens = ([regex]::Matches($line, '\{')).Count
        $closes = ([regex]::Matches($line, '\}')).Count

        for ($o = 0; $o -lt $opens; $o++) {
            $depth++
            if ($null -ne $pending) {
                $stack.Add(@{ Name = $pending.Name; Kind = $pending.Kind; Depth = $depth })
                $pending = $null
            }
        }
        for ($c = 0; $c -lt $closes; $c++) {
            if ($stack.Count -gt 0 -and $stack[$stack.Count - 1].Depth -eq $depth) {
                $stack.RemoveAt($stack.Count - 1)
            }
            $depth--
            if ($depth -lt 0) { $depth = 0 }
        }
    }

    return @{ Owner = $owner; Kind = $kind }
}

function ConvertTo-Int64Value([string]$raw) {
    if ($raw -like '0x*' -or $raw -like '0X*') { return [Convert]::ToInt64($raw.Substring(2), 16) }
    return [int64]$raw
}

function Read-DecompiledService([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $scopes = Get-TypeScopes $lines
    $owner = $scopes.Owner
    $kind = $scopes.Kind

    $constants = New-Object System.Collections.Generic.List[object]
    $enumMembers = New-Object System.Collections.Generic.List[object]
    $structFields = New-Object System.Collections.Generic.List[object]
    $ioctls = New-Object System.Collections.Generic.List[object]

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        $c = [regex]::Match($line, 'const\s+(ushort|uint|int|byte|short|long)\s+([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+|\d+)')
        if ($c.Success) {
            $name = $c.Groups[2].Value
            $value = ConvertTo-Int64Value $c.Groups[3].Value
            $record = [pscustomobject]@{
                Name       = $name
                DeclaredIn = $owner[$i]
                CType      = $c.Groups[1].Value
                Value      = $value
                Hex        = ('0x{0:X}' -f $value)
                Line       = $i + 1
            }
            $constants.Add($record)
            if ($name -like 'IOCTL_*') { $ioctls.Add($record) }
            continue
        }

        if ($kind[$i] -eq 'enum') {
            $e = [regex]::Match($line, '^\s*([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)\s*,?\s*$')
            if ($e.Success) {
                $value = ConvertTo-Int64Value $e.Groups[2].Value
                $enumMembers.Add([pscustomobject]@{
                        Enum   = $owner[$i]
                        Member = $e.Groups[1].Value
                        Value  = $value
                        Hex    = ('0x{0:X}' -f $value)
                        Line   = $i + 1
                    })
                continue
            }
            $bare = [regex]::Match($line, '^\s*([A-Za-z_]\w*)\s*,\s*$')
            if ($bare.Success) {
                $enumMembers.Add([pscustomobject]@{
                        Enum   = $owner[$i]
                        Member = $bare.Groups[1].Value
                        Value  = $null
                        Hex    = $null
                        Line   = $i + 1
                    })
                continue
            }
        }

        if ($kind[$i] -eq 'struct') {
            $f = [regex]::Match($line, '^\s*public\s+(byte|ushort|uint|int|short|bool)\s+([A-Za-z_]\w*)\s*;')
            if ($f.Success) {
                $structFields.Add([pscustomobject]@{
                        Struct = $owner[$i]
                        Field  = $f.Groups[2].Value
                        CType  = $f.Groups[1].Value
                        Line   = $i + 1
                    })
            }
        }
    }

    return @{
        Lines        = $lines.Count
        Constants    = $constants
        EnumMembers  = $enumMembers
        StructFields = $structFields
        Ioctls       = $ioctls
    }
}

Write-Section 'decompiled service'
$servicePath = Join-Path $RepositoryRoot '_decompiled\GCUService\GCUService.decompiled.cs'
if (-not (Test-Path $servicePath)) {
    throw "Decompiled service not found: $servicePath"
}
$service = Read-DecompiledService $servicePath
Write-Output ("  source lines      : {0:N0}" -f $service.Lines)
Write-Output ("  constants         : {0}" -f $service.Constants.Count)
Write-Output ("  enum members      : {0}" -f $service.EnumMembers.Count)
Write-Output ("  struct fields     : {0}" -f $service.StructFields.Count)
Write-Output ("  IOCTL constants   : {0}" -f $service.Ioctls.Count)

# ---------------------------------------------------------------------------
# 2. EC register map with duplicate classification
# ---------------------------------------------------------------------------

# Generic positional aliases. When a specific name shares an address with one of
# these, it is not a real conflict: the generic name is just "byte N of the OEM
# block" and the specific name says what that byte is used for.
$aliasPatterns = @(
    '^ADDR_AP_OEM_BYTE\d*$',
    '^ADDR_BIOS_OEM_BYTE\d*$',
    '^ADDR_SUPPORT_BYTE\d*$',
    '^ADDR_AP_OEM_BYTE\d+_.*$'
)

function Test-IsGenericAlias([string]$name) {
    foreach ($pattern in $aliasPatterns) {
        if ($name -match $pattern) { return $true }
    }
    return $false
}

$addresses = @($service.Constants | Where-Object { $_.CType -eq 'ushort' })

$registerGroups = New-Object System.Collections.Generic.List[object]
foreach ($group in ($addresses | Group-Object { $_.DeclaredIn + '|' + $_.Value })) {
    $first = $group.Group[0]
    $names = @($group.Group | ForEach-Object Name | Sort-Object -Unique)
    $specific = @($names | Where-Object { -not (Test-IsGenericAlias $_) })
    $aliases = @($names | Where-Object { Test-IsGenericAlias $_ })

    $status = 'unique'
    if ($specific.Count -gt 1) { $status = 'ambiguous' }
    elseif ($aliases.Count -gt 0 -and $specific.Count -eq 1) { $status = 'aliased' }
    elseif ($specific.Count -eq 0) { $status = 'alias-only' }

    $registerGroups.Add([pscustomobject]@{
            DeclaredIn    = $first.DeclaredIn
            Address       = $first.Value
            Hex           = $first.Hex
            PrimaryName   = if ($specific.Count -ge 1) { $specific[0] } else { $names[0] }
            SpecificNames = $specific
            GenericAlias  = $aliases
            Status        = $status
        })
}

$byStatus = $registerGroups | Group-Object Status
Write-Section 'EC register map'
Write-Output ("  address constants : {0}" -f $addresses.Count)
Write-Output ("  declaring types   : {0}" -f (($addresses | Group-Object DeclaredIn).Count))
Write-Output ("  register slots    : {0}" -f $registerGroups.Count)
foreach ($s in ($byStatus | Sort-Object Name)) {
    Write-Output ("    {0,-12} {1}" -f $s.Name, $s.Count)
}

$crossType = @($registerGroups | Group-Object Address | Where-Object { $_.Count -gt 1 })
Write-Output ("  addresses reused across types (resolved by declaring type): {0}" -f $crossType.Count)

$ambiguous = @($registerGroups | Where-Object { $_.Status -eq 'ambiguous' })
Write-Output ("  addresses needing per-model disambiguation: {0}" -f $ambiguous.Count)
foreach ($a in ($ambiguous | Sort-Object Address)) {
    Write-Output ("    {0} {1} <- {2}" -f $a.DeclaredIn, $a.Hex, ($a.SpecificNames -join ' | '))
}

# ---------------------------------------------------------------------------
# 3. Project / model identity enums
# ---------------------------------------------------------------------------

$identityEnums = @('ProjectID', 'BIOS_PROJECT_ID', 'GN20_GPU_SKU', 'GN21_GPU_SKU')
$identity = @{}
foreach ($name in $identityEnums) {
    $members = @($service.EnumMembers | Where-Object { $_.Enum -eq $name })
    # Implicit enum values: fill in sequentially the way the C# compiler does.
    $next = 0
    $filled = New-Object System.Collections.Generic.List[object]
    foreach ($m in $members) {
        if ($null -ne $m.Value) { $next = [int64]$m.Value }
        $filled.Add([pscustomobject]@{
                Member   = $m.Member
                Value    = $next
                Hex      = ('0x{0:X}' -f $next)
                Explicit = ($null -ne $m.Value)
            })
        $next++
    }
    $identity[$name] = $filled
}

Write-Section 'project identity'
foreach ($name in $identityEnums) {
    Write-Output ("  {0,-18} {1} members" -f $name, $identity[$name].Count)
}

# ProjectID uses a family/variant encoding: a family gets a small id (for
# example PHxAxxx = 23) and its variants get (family << 8) | index. Recovering
# that relationship makes the model tree explicit instead of implicit.
$projectFamilies = New-Object System.Collections.Generic.List[object]
$projectIds = $identity['ProjectID']
foreach ($p in $projectIds) {
    $family = $null
    if ($p.Value -gt 255) {
        $familyValue = [int64]([math]::Floor($p.Value / 256))
        $match = @($projectIds | Where-Object { $_.Value -eq $familyValue })
        if ($match.Count -eq 1) { $family = $match[0].Member }
    }
    $projectFamilies.Add([pscustomobject]@{
            Member   = $p.Member
            Value    = $p.Value
            Hex      = $p.Hex
            Family   = $family
            Variant  = if ($null -ne $family) { [int64]($p.Value % 256) } else { $null }
        })
}
$withFamily = @($projectFamilies | Where-Object { $null -ne $_.Family })
Write-Output ("  ProjectID variants resolved to a family: {0}" -f $withFamily.Count)

# ---------------------------------------------------------------------------
# 4. Per-model factory fan tables
# ---------------------------------------------------------------------------

Write-Section 'factory fan tables'
$fanSources = @(Get-ChildItem $RepositoryRoot -Directory -Filter 'ControlCenter*_Mechrevo' -ErrorAction SilentlyContinue)
$fanModels = New-Object System.Collections.Generic.List[object]
$fanVersions = New-Object System.Collections.Generic.List[object]

foreach ($source in $fanSources) {
    $tableRoots = @(Get-ChildItem $source.FullName -Directory -Recurse -Filter 'UserFanTables' -ErrorAction SilentlyContinue)
    foreach ($tableRoot in $tableRoots) {
        $version = $source.Name -replace '^ControlCenter[X]?_', '' -replace '_Mechrevo$', ''
        $modelDirs = @(Get-ChildItem $tableRoot.FullName -Directory -ErrorAction SilentlyContinue)
        $fanVersions.Add([pscustomobject]@{ Version = $version; Models = $modelDirs.Count; Path = $tableRoot.FullName.Replace($RepositoryRoot, '').TrimStart('\') })
        Write-Output ("  {0,-14} {1} model directories" -f $version, $modelDirs.Count)

        foreach ($modelDir in $modelDirs) {
            $tables = New-Object System.Collections.Generic.List[object]
            foreach ($file in (Get-ChildItem $modelDir.FullName -Filter *.json -ErrorAction SilentlyContinue | Sort-Object Name)) {
                try { $data = Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { continue }
                $cpu = @($data.CPU | ForEach-Object { [pscustomobject]@{ UpT = $_.UpT; DownT = $_.DownT; Duty = $_.Duty } })
                $gpu = @($data.GPU | ForEach-Object { [pscustomobject]@{ UpT = $_.UpT; DownT = $_.DownT; Duty = $_.Duty } })
                $tables.Add([pscustomobject]@{
                        Table                  = $data.Name
                        Activated              = $data.Activated
                        PL1                    = $data.PL1
                        PL2                    = $data.PL2
                        PL1Dc                  = $data.PL1_dc
                        PL2Dc                  = $data.PL2_dc
                        Tcc                    = $data.TCC
                        CpuTempDefaultMaxLevel = $data.CpuTemp_DefaultMaxLevel
                        GpuTempDefaultMaxLevel = $data.GpuTemp_DefaultMaxLevel
                        Cpu                    = $cpu
                        Gpu                    = $gpu
                    })
            }
            if ($tables.Count -gt 0) {
                $fanModels.Add([pscustomobject]@{
                        Project        = $modelDir.Name
                        ConsoleVersion = $version
                        Tables         = $tables
                    })
            }
        }
    }
}

$distinctModels = @($fanModels | ForEach-Object Project | Sort-Object -Unique)
Write-Output ("  distinct model projects with factory tables: {0}" -f $distinctModels.Count)

# ---------------------------------------------------------------------------
# 5. Keyboard / lightbar zone universe
# ---------------------------------------------------------------------------

Write-Section 'keyboard and lightbar zones'
$zoneRecords = New-Object System.Collections.Generic.List[object]
foreach ($source in $fanSources) {
    foreach ($reg in (Get-ChildItem $source.FullName -Recurse -Filter 'RGBKeyboard.reg' -ErrorAction SilentlyContinue)) {
        $version = $source.Name -replace '^ControlCenter[X]?_', '' -replace '_Mechrevo$', ''
        $keys = @(Select-String -Path $reg.FullName -Pattern '^\[HKEY' -Encoding Unicode -ErrorAction SilentlyContinue |
            ForEach-Object { ($_.Line.Trim() -replace '^\[HKEY_LOCAL_MACHINE\\SOFTWARE\\OEM\\GamingCenter2\\', '') -replace '\]$', '' })
        foreach ($key in $keys) {
            if ($key -eq 'RGBKeyboard') { continue }
            $zoneRecords.Add([pscustomobject]@{
                    ConsoleVersion = $version
                    Zone           = ($key -replace '^RGBKeyboard\\', '')
                })
        }
        Write-Output ("  {0,-14} {1} zone sections" -f $version, ($keys.Count - 1))
    }
}
$zones = @($zoneRecords | Group-Object Zone | ForEach-Object {
        [pscustomobject]@{
            Zone     = $_.Name
            Versions = @($_.Group | ForEach-Object ConsoleVersion | Sort-Object -Unique)
        }
    } | Sort-Object Zone)
Write-Output ("  distinct zone layouts: {0}" -f $zones.Count)

# ---------------------------------------------------------------------------
# 6. Emit
# ---------------------------------------------------------------------------

Write-Section 'output'
$stamp = (Get-Date).ToUniversalTime().ToString('o')

Save-Json ([pscustomobject]@{
        GeneratedUtc = $stamp
        Source       = '_decompiled/GCUService/GCUService.decompiled.cs'
        Note         = 'ConfuserEx stubbed method bodies; const/enum/struct declarations survived and are reproduced verbatim.'
        Ioctls       = @($service.Ioctls | Sort-Object Name)
        Registers    = @($registerGroups | Sort-Object DeclaredIn, Address)
        StructFields = @($service.StructFields | Sort-Object Struct, Line)
    }) 'ec-registers.json' | Out-Null

Save-Json ([pscustomobject]@{
        GeneratedUtc    = $stamp
        Source          = '_decompiled/GCUService/GCUService.decompiled.cs'
        ProjectId       = @($projectFamilies | Sort-Object Value)
        BiosProjectId   = $identity['BIOS_PROJECT_ID']
        Gn20GpuSku      = $identity['GN20_GPU_SKU']
        Gn21GpuSku      = $identity['GN21_GPU_SKU']
    }) 'project-ids.json' | Out-Null

Save-Json ([pscustomobject]@{
        GeneratedUtc = $stamp
        Source       = 'ControlCenter_*/RGBKeyboard.reg'
        Zones        = $zones
    }) 'keyboard-zones.json' | Out-Null

Save-Json ([pscustomobject]@{
        GeneratedUtc = $stamp
        Source       = 'ControlCenter_*/UserFanTables'
        Versions     = $fanVersions
        Models       = @($fanModels | Sort-Object ConsoleVersion, Project)
    }) 'fan-table-defaults.json' | Out-Null

$flagEnums = @($service.EnumMembers | Where-Object { $_.Enum -match 'Flag$|Byte.*Flag|SpeedByteFlag' } | Group-Object Enum | ForEach-Object {
        [pscustomobject]@{ Enum = $_.Name; Members = @($_.Group | Select-Object Member, Value, Hex) }
    } | Sort-Object Enum)
Save-Json ([pscustomobject]@{
        GeneratedUtc = $stamp
        Source       = '_decompiled/GCUService/GCUService.decompiled.cs'
        Note         = 'Bit-level meaning for EC status/control bytes.'
        FlagEnums    = $flagEnums
    }) 'ec-bit-flags.json' | Out-Null

Write-Section 'summary'
Write-Output ("  models with factory tables : {0}" -f $distinctModels.Count)
Write-Output ("  ProjectID members          : {0}" -f $identity['ProjectID'].Count)
Write-Output ("  BIOS_PROJECT_ID members    : {0}" -f $identity['BIOS_PROJECT_ID'].Count)
Write-Output ("  EC register slots          : {0}" -f $registerGroups.Count)
Write-Output ("  needing disambiguation     : {0}" -f $ambiguous.Count)
Write-Output ("  keyboard/lightbar zones    : {0}" -f $zones.Count)
Write-Output ("  flag enums                 : {0}" -f $flagEnums.Count)
Write-Output ''
Write-Output ('Output directory: ' + $OutputDirectory)
