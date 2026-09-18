#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
<#
.SYNOPSIS
    Generate the SHA256 identity manifest for a bundled GCU payload.

.DESCRIPTION
    Writes a tab-separated manifest with the columns:

        Variant | RelPath | Bytes | SHA256

    Variant is the payload key, RelPath is relative to the payload root, Bytes is the file
    length and SHA256 is the uppercase file hash. Identity is always the hash: the GCU service
    version line is not shared across payloads (release\GCU-only is 1.2.0.0 while the 40-series
    trees are 1.0.2.70), so a version number can neither identify nor order them.

    Default output is artifacts\gcu-payload-manifest.tsv for release\GCU-only (variant 50).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\New-GcuPayloadManifest.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\New-GcuPayloadManifest.ps1 `
        -Variant 40-51751 -PayloadRelPath release\GCU-40-51751 -OutputPath artifacts\manifest-40.tsv
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$OutputPath,
    [string]$Variant = '50',
    [string]$PayloadRelPath = 'release\GCU-only'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$payload = Join-Path $root $PayloadRelPath
if (-not (Test-Path -LiteralPath $payload)) {
    throw ("GCU payload directory missing: {0}" -f $payload)
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'artifacts\gcu-payload-manifest.tsv'
}
$outputDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$rows = foreach ($file in (Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)) {
    [pscustomobject]@{
        Variant = $Variant
        RelPath = $file.FullName.Substring($payload.Length + 1)
        Bytes   = $file.Length
        SHA256  = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}

# Export-Csv quotes and escapes fields, so paths with spaces stay parseable by Import-Csv.
$rows | Export-Csv -LiteralPath $OutputPath -Delimiter "`t" -NoTypeInformation -Encoding ASCII
Write-Host ("GCU payload manifest: {0} rows from {1} -> {2}" -f @($rows).Count, $payload, $OutputPath)
