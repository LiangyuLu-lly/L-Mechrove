#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
<#
.SYNOPSIS
    Build the L-Mechrevo Inno Setup installer from the current publish output.

.DESCRIPTION
    Reads the version from src\MechrevoLiteWin\MechrevoLite.csproj, locates the
    newest self-contained single-file publish directory under dist\, validates the
    four GCU payload trees, ensures an ISCC.exe compiler is available (provisioning
    one under %TEMP%\ulw\tools\innosetup when needed), compiles installer\L-Mechrevo.iss
    and writes hash / staging evidence next to the installer.

    This script never installs Inno Setup system-wide and never touches Program Files.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1 -ProvisionCompiler
#>
[CmdletBinding()]
param(
    [string]$AppSourceDir,
    [string]$OutputDir,
    [string]$IsccPath,
    [switch]$ProvisionCompiler
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $PSScriptRoot 'L-Mechrevo.iss'
$toolsDir = Join-Path $env:TEMP 'ulw\tools'
$innoDir = Join-Path $toolsDir 'innosetup'

# Reuse the selector's payload bundle so what the build validates is what the installer ships.
# Dot-sourcing Select-GcuPayload.ps1 is side-effect free (it returns early on dot-source).
. (Join-Path $PSScriptRoot 'Select-GcuPayload.ps1')

# Keep this script ASCII-only while still requiring Chinese user-facing filenames.
$notesName = (-join @([char]0x66F4, [char]0x65B0, [char]0x65E5, [char]0x5FD7)) + '.txt'
$guideName = (-join @([char]0x7528, [char]0x524D, [char]0x5FC5, [char]0x770B)) + '.txt'

function Get-CsprojVersions {
    param([string]$ProjectFile)
    [xml]$xml = [System.IO.File]::ReadAllText($ProjectFile)
    $version = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $version -or [string]::IsNullOrWhiteSpace($version.InnerText)) {
        throw ("csproj <Version> is missing: {0}" -f $ProjectFile)
    }
    $assembly = $xml.SelectSingleNode('/Project/PropertyGroup/AssemblyVersion')
    $numeric = if ($assembly -and -not [string]::IsNullOrWhiteSpace($assembly.InnerText)) {
        $assembly.InnerText.Trim()
    }
    else { '0.0.0.0' }
    return [pscustomobject]@{ Version = $version.InnerText.Trim(); Numeric = $numeric }
}

function Get-ReleaseLabel {
    param([string]$Version)
    $clean = ($Version -split '\+')[0]
    $separator = $clean.IndexOf('-')
    $label = if ($separator -ge 0) { $clean.Substring($separator + 1) } else { $clean }
    if ($label.StartsWith('beta.', [System.StringComparison]::OrdinalIgnoreCase)) {
        $label = 'beta' + $label.Substring(5)
    }
    return $label
}

function Find-AppSourceDir {
    param([string]$RootPath, [string]$Label)
    $preferred = Join-Path $RootPath (Join-Path 'dist' $Label)
    if (Test-Path -LiteralPath (Join-Path $preferred 'L-Mechrevo.exe')) { return $preferred }
    $distRoot = Join-Path $RootPath 'dist'
    if (-not (Test-Path -LiteralPath $distRoot)) { throw ("dist directory not found: {0}" -f $distRoot) }
    $dirs = Get-ChildItem -LiteralPath $distRoot -Directory | Sort-Object LastWriteTime -Descending
    foreach ($dir in $dirs) {
        if (Test-Path -LiteralPath (Join-Path $dir.FullName 'L-Mechrevo.exe')) { return $dir.FullName }
    }
    throw ("no dist\<label> directory contains L-Mechrevo.exe under {0}" -f $distRoot)
}

function Resolve-Iscc {
    param([string]$Explicit)
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        throw ("ISCC.exe not found at -IsccPath {0}" -f $Explicit)
    }
    $candidates = @(
        (Join-Path $innoDir '{app}\ISCC.exe'),
        (Join-Path $innoDir 'ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}

function Install-Iscc {
    Write-Host 'ISCC.exe not found; provisioning Inno Setup 6.7.3 under %TEMP% (no system install)...'
    $innounp = Join-Path $toolsDir 'innounp\innounp.exe'
    if (-not (Test-Path -LiteralPath $innounp)) {
        throw ("innounp.exe is required to unpack Inno Setup but was not found at {0}" -f $innounp)
    }
    $setupExe = Join-Path $toolsDir 'innosetup-6.7.3.exe'
    if (-not (Test-Path -LiteralPath $setupExe)) {
        $url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
        Invoke-WebRequest -Uri $url -OutFile $setupExe -UseBasicParsing -TimeoutSec 300
    }
    New-Item -ItemType Directory -Path $innoDir -Force | Out-Null
    & $innounp -x -b -y -q -d"$innoDir" $setupExe | Out-Null
    $isl = Join-Path $innoDir '{app}\Languages\ChineseSimplified.isl'
    if (-not (Test-Path -LiteralPath $isl)) {
        $islUrl = 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl'
        Invoke-WebRequest -Uri $islUrl -OutFile $isl -UseBasicParsing -TimeoutSec 120
    }
}

function Ensure-Utf8Bom {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return }
    $text = [System.IO.File]::ReadAllText($Path)
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

# --- version -----------------------------------------------------------------
$versions = Get-CsprojVersions -ProjectFile (Join-Path $root 'src\MechrevoLiteWin\MechrevoLite.csproj')
$label = Get-ReleaseLabel -Version $versions.Version
Write-Host ("Version     : {0} (numeric {1}, label {2})" -f $versions.Version, $versions.Numeric, $label)

# --- publish output ----------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($AppSourceDir)) {
    $AppSourceDir = Find-AppSourceDir -RootPath $root -Label $label
}
$AppSourceDir = (Resolve-Path -LiteralPath $AppSourceDir).Path
$appExe = Join-Path $AppSourceDir 'L-Mechrevo.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw ("published executable not found: {0}" -f $appExe) }
$appExeItem = Get-Item -LiteralPath $appExe

# R2: the shipped app is framework-dependent on purpose - no .NET runtime DLLs are bundled,
# the installer detects/downloads the .NET Desktop Runtime 10 (x64) instead. Assert that here
# so a self-contained or single-file publish cannot silently slip back into the package.
$appDll = Join-Path $AppSourceDir 'L-Mechrevo.dll'
$appRuntimeConfig = Join-Path $AppSourceDir 'L-Mechrevo.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $appDll) -or -not (Test-Path -LiteralPath $appRuntimeConfig)) {
    throw ("app source is not a framework-dependent publish (missing L-Mechrevo.dll / L-Mechrevo.runtimeconfig.json): {0}. Publish with --self-contained false -p:PublishSingleFile=false." -f $AppSourceDir)
}
$appMeasure = Get-ChildItem -LiteralPath $AppSourceDir -Recurse -File | Measure-Object -Property Length -Sum
Write-Host ("AppSource   : {0}" -f $AppSourceDir)
Write-Host ("App payload : {0} files, {1:N1} MB (framework-dependent; needs .NET Desktop Runtime 10 x64)" -f $appMeasure.Count, ($appMeasure.Sum / 1MB))

# --- docs --------------------------------------------------------------------
foreach ($doc in @((Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.txt'), (Join-Path $root $notesName), (Join-Path $root $guideName))) {
    if (-not (Test-Path -LiteralPath $doc)) { throw ("required shipped document missing: {0}" -f $doc) }
}

# --- GCU payload accounting --------------------------------------------------
# N6: exactly one payload tree is packaged (release\GCU-only). Assert-GcuPayloadDirs hard-fails
# the build when it is missing, so a package can never be produced without its payload.
$bundle = Assert-GcuPayloadDirs -Root $root
Write-Host ("GCU payload bundle: single ({0})" -f ($bundle | ForEach-Object { $_.RepoPayload }) -join ', ')
$payloads = foreach ($entry in $bundle) {
    [pscustomobject]@{ Key = $entry.Key; Dir = (Join-Path $root $entry.RepoPayload) }
}
$payloadReport = foreach ($payload in $payloads) {
    $measure = Get-ChildItem -LiteralPath $payload.Dir -Recurse -File | Measure-Object -Property Length -Sum
    [pscustomobject]@{ Key = $payload.Key; Dir = $payload.Dir; Files = $measure.Count; Bytes = $measure.Sum }
}
Write-Host 'GCU payloads:'
$payloadReport | ForEach-Object { Write-Host ("  {0,-9} {1,5} files  {2,10:N1} MB  {3}" -f $_.Key, $_.Files, ($_.Bytes / 1MB), $_.Dir) }
$totalPayloadBytes = ($payloadReport | Measure-Object -Property Bytes -Sum).Sum

# --- output dir --------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root 'artifacts\run4-installer' }
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$logFile = Join-Path $OutputDir 'iscc-compile.log'

# --- compiler ----------------------------------------------------------------
$iscc = Resolve-Iscc -Explicit $IsccPath
if (-not $iscc -or $ProvisionCompiler) {
    Install-Iscc
    $iscc = Resolve-Iscc -Explicit $IsccPath
}
if (-not $iscc) { throw 'could not locate or provision ISCC.exe' }
$isccDir = Split-Path -Parent $iscc
if (-not (Test-Path -LiteralPath (Join-Path $isccDir 'Languages\ChineseSimplified.isl'))) {
    Write-Host 'ChineseSimplified.isl missing next to ISCC; fetching...'
    $islUrl = 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl'
    Invoke-WebRequest -Uri $islUrl -OutFile (Join-Path $isccDir 'Languages\ChineseSimplified.isl') -UseBasicParsing -TimeoutSec 120
}
Write-Host ("ISCC        : {0}" -f $iscc)

Ensure-Utf8Bom -Path $issPath

# --- compile -----------------------------------------------------------------
$defineArgs = @(
    ('/DAppVersion={0}' -f $versions.Version),
    ('/DAppLabel={0}' -f $label),
    ('/DAppVersionNumeric={0}' -f $versions.Numeric),
    ('/DRepoRoot={0}' -f $root),
    ('/DAppSourceDir={0}' -f $AppSourceDir),
    ('/O{0}' -f $OutputDir)
)
Write-Host ("== ISCC {0} {1}" -f ($defineArgs -join ' '), $issPath)
$savedEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $iscc @defineArgs $issPath 2>&1 | Tee-Object -FilePath $logFile
$isccExit = $LASTEXITCODE
$ErrorActionPreference = $savedEap
Write-Host ("ISCC exit code: {0}" -f $isccExit)
if ($isccExit -ne 0) { throw ("ISCC failed with exit code {0}; see {1}" -f $isccExit, $logFile) }

# --- artifact ----------------------------------------------------------------
$setupExe = Join-Path $OutputDir ('L-Mechrevo-{0}-setup.exe' -f $label)
if (-not (Test-Path -LiteralPath $setupExe)) { throw ("installer was not produced: {0}" -f $setupExe) }
$setupItem = Get-Item -LiteralPath $setupExe
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupExe).Hash

$summary = [ordered]@{
    GeneratedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    AppVersion      = $versions.Version
    AppLabel        = $label
    AppSourceDir    = $AppSourceDir
    AppExeBytes     = $appExeItem.Length
    AppFiles        = $appMeasure.Count
    AppPayloadBytes = $appMeasure.Sum
    RuntimeDependency = 'Microsoft.WindowsDesktop.App 10.x (x64)'
    Installer       = $setupItem.FullName
    InstallerBytes  = $setupItem.Length
    InstallerSHA256 = $hash
    PayloadTotalBytes = $totalPayloadBytes
    PayloadTrees    = @($payloadReport | ForEach-Object { $_.Key })
    Iscc            = $iscc
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDir 'build-summary.json') -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('L-Mechrevo installer payload accounting')
$lines.Add('=====================================')
$lines.Add(('GeneratedUtc : {0}' -f $summary.GeneratedUtc))
$lines.Add(('AppVersion   : {0}' -f $versions.Version))
$lines.Add(('AppSourceDir : {0}' -f $AppSourceDir))
$lines.Add(('App payload  : {0} files, {1} bytes (framework-dependent; .NET Desktop Runtime 10 x64 required)' -f $appMeasure.Count, $appMeasure.Sum))
$lines.Add('')
$lines.Add('GCU payload trees bundled (each staged under {app}\GCU\payload\):')
foreach ($row in $payloadReport) {
    $lines.Add(('  {0,-9} files={1,-5} bytes={2,-12} dir={3}' -f $row.Key, $row.Files, $row.Bytes, $row.Dir))
}
$lines.Add(('  TOTAL     bytes={0}' -f $totalPayloadBytes))
$lines.Add('')
$lines.Add(('Installer    : {0}' -f $setupItem.FullName))
$lines.Add(('Size bytes   : {0}' -f $setupItem.Length))
$lines.Add(('SHA256       : {0}' -f $hash))
Set-Content -LiteralPath (Join-Path $OutputDir 'payload-staging.txt') -Value $lines -Encoding UTF8

Write-Host ''
Write-Host 'DONE.'
Write-Host ("  Installer : {0}" -f $setupItem.FullName)
Write-Host ("  Size      : {0:N1} MB ({1} bytes)" -f ($setupItem.Length / 1MB), $setupItem.Length)
Write-Host ("  SHA256    : {0}" -f $hash)
Write-Host ("  Log       : {0}" -f $logFile)
Write-Host ("  Evidence  : {0}" -f $OutputDir)
