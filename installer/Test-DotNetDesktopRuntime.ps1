#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
#
# Testable mirror of the installer's [Code] runtime detection. The Inno Pascal in
# L-Mechrevo.iss cannot be unit-tested directly, so the decision lives here as a small
# predicate that the tests and the installer smoke check both exercise.
#
# Field bug this exists to prevent: the beta18 installer did not detect an already-installed
# .NET 10 runtime, so users who HAD it were blocked. Two independent defects:
#   1. RegGetSubkeyNames reads SUBKEY names, but the .NET installer records installed versions
#      as VALUE names (e.g. a value named "10.0.8"). The key has no subkeys, so the loop never
#      ran and detection always returned False.
#   2. The key was read from HKLM64 only. On the affected machine the 64-bit view of
#      ...\x64\sharedfx\Microsoft.WindowsDesktop.App is EMPTY; the populated key is in the
#      32-bit view. Both views must be consulted.
#
# Exit 0 + "DETECTED <version>" when a 10.x Desktop Runtime is present; exit 1 + "MISSING" otherwise.
<#
.SYNOPSIS
    Detect the .NET Desktop Runtime 10.x (x64) that the framework-dependent app requires.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Test-DotNetDesktopRuntime.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\Test-DotNetDesktopRuntime.ps1 `
        -KeyPath 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\NoSuchFramework'
#>
[CmdletBinding()]
param(
    [string]$KeyPath = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    [int]$MajorVersion = 10,
    [string]$SharedFrameworkRoot = "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App",
    # Tests use this to exercise the registry decision in isolation; the installer always keeps
    # the directory fallback enabled.
    [switch]$NoDirectoryFallback
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-InstalledMajorVersions {
    # Reads the VALUE names of the key in BOTH registry views. The .NET installer writes the
    # versions as value names (REG_DWORD 1), not as subkeys, and which view is populated differs
    # per machine - so neither the API nor the view may be assumed.
    param([string]$Path)
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($view in @('Registry64', 'Registry32')) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                [Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $key = $base.OpenSubKey($Path)
            if ($null -eq $key) { continue }
            foreach ($name in $key.GetValueNames()) {
                if (-not [string]::IsNullOrWhiteSpace($name)) { $found.Add($name) }
            }
            $key.Dispose()
            $base.Dispose()
        }
        catch {
            Write-Verbose ("registry view {0} unreadable: {1}" -f $view, $_.Exception.Message)
        }
    }
    return $found
}

function Get-InstalledSharedFrameworkVersions {
    # Independent corroboration: the shared framework on disk. The installer must not rely on
    # `dotnet` being on PATH, but a directory check is a fine fallback.
    param([string]$Root)
    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
}

$versions = @(Get-InstalledMajorVersions -Path $KeyPath)
$match = $versions | Where-Object { $_ -match ('^{0}\.' -f $MajorVersion) } | Sort-Object -Descending | Select-Object -First 1

if (-not $match) {
    $onDisk = if ($NoDirectoryFallback) { @() } else {
        @(Get-InstalledSharedFrameworkVersions -Root $SharedFrameworkRoot) |
            Where-Object { $_ -match ('^{0}\.' -f $MajorVersion) } | Sort-Object -Descending | Select-Object -First 1
    }
    if ($onDisk) {
        Write-Output ("DETECTED {0} {1} (from the shared framework directory; registry key '{2}' had no {3}.x value)" -f `
            'Microsoft.WindowsDesktop.App', $onDisk, $KeyPath, $MajorVersion)
        exit 0
    }
    Write-Output ("MISSING Microsoft.WindowsDesktop.App {0}.x (x64); registry key '{1}' values: {2}" -f `
        $MajorVersion, $KeyPath, $(if ($versions.Count -gt 0) { $versions -join ', ' } else { '(none)' }))
    exit 1
}

Write-Output ("DETECTED Microsoft.WindowsDesktop.App {0} (x64)" -f $match)
exit 0
