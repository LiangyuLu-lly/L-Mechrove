# L-Mechrevo protected publish pipeline (keep this file ASCII-only; PS5.1 reads scripts as ANSI)
# Usage: powershell -ExecutionPolicy Bypass -File publish-obfuscated.ps1 -CertificatePath release.pfx
# Development-only unsigned verification: add -AllowUnsigned
param(
    [string]$CertificatePath = $env:LMECHREVO_SIGN_CERT,
    [string]$CertificatePassword = $env:LMECHREVO_SIGN_PASSWORD,
    [string]$OutputDirectory,
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\MechrevoLiteWin"
$projectFile = Join-Path $proj "MechrevoLite.csproj"
$tfm = "net10.0-windows10.0.19041.0"
$binDir = Join-Path $proj ("bin\x64\Release\" + $tfm)
$objDir = Join-Path $proj ("obj\x64\Release\" + $tfm)
$releaseDir = Join-Path $root "release"

[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$versionNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText))
{
    throw "project Version is missing"
}
$packageVersion = $versionNode.InnerText.Trim()
$cleanVersion = ($packageVersion -split '\+')[0]
$separator = $cleanVersion.IndexOf('-')
$releaseLabel = if ($separator -ge 0) { $cleanVersion.Substring($separator + 1) } else { $cleanVersion }
if ($releaseLabel.StartsWith("beta.", [System.StringComparison]::OrdinalIgnoreCase))
{
    $releaseLabel = "beta" + $releaseLabel.Substring(5)
}
$outDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    Join-Path $root (Join-Path "dist" $releaseLabel)
}
else
{
    if ([System.IO.Path]::IsPathRooted($OutputDirectory))
    {
        [System.IO.Path]::GetFullPath($OutputDirectory)
    }
    else
    {
        [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    }
}
if (Test-Path -LiteralPath $outDir)
{
    throw "release output directory already exists: $outDir"
}

# Keep this script ASCII-only while still requiring Chinese user-facing filenames.
$guideFileName = (-join @([char]0x7528, [char]0x524D, [char]0x5FC5, [char]0x770B)) + ".txt"
$notesFileName = (-join @([char]0x66F4, [char]0x65B0, [char]0x65E5, [char]0x5FD7)) + ".txt"
$guideSource = Join-Path $root $guideFileName
$notesSource = Join-Path $root $notesFileName
foreach ($requiredFile in @($guideSource, $notesSource))
{
    if (-not (Test-Path -LiteralPath $requiredFile)) { throw "required release document is missing: $requiredFile" }
}
$notesText = Get-Content -LiteralPath $notesSource -Raw
if ($notesText -notmatch ("(?m)^" + [regex]::Escape($releaseLabel) + "\s"))
{
    throw "release notes do not contain the current release label: $releaseLabel"
}

if ([string]::IsNullOrWhiteSpace($CertificatePath) -and -not $AllowUnsigned)
{
    throw "A code-signing PFX is required. Set LMECHREVO_SIGN_CERT or pass -CertificatePath. Use -AllowUnsigned only for local verification."
}
if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and -not (Test-Path -LiteralPath $CertificatePath))
{
    throw "signing certificate not found: $CertificatePath"
}
$artifactLabel = if ([string]::IsNullOrWhiteSpace($CertificatePath)) { "$releaseLabel-unsigned" } else { $releaseLabel }

Write-Host "== 1/7 stop running app =="
Get-Process -Name "L-Mechrevo" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Host "== 2/7 rebuild main assembly and RID publish cache =="
Push-Location $proj
try
{
dotnet build -c Release -t:Rebuild -v q
if ($LASTEXITCODE -ne 0) { throw "main assembly build failed" }
dotnet build -c Release -r win-x64 -p:SelfContained=true -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "== 3/7 obfuscate =="
Remove-Item (Join-Path $proj "obj\obfuscated") -Recurse -Force -ErrorAction SilentlyContinue
obfuscar.console (Join-Path $proj "obfuscar.xml")
if ($LASTEXITCODE -ne 0) { throw "obfuscar failed" }

Write-Host "== 4/7 overwrite dlls + clear R2R cache =="
$obf = Join-Path $proj "obj\obfuscated\L-Mechrevo.dll"
Copy-Item $obf (Join-Path $binDir "L-Mechrevo.dll") -Force
Copy-Item $obf (Join-Path $objDir "L-Mechrevo.dll") -Force
Copy-Item $obf (Join-Path $objDir "win-x64\L-Mechrevo.dll") -Force
Copy-Item $obf (Join-Path $binDir "win-x64\L-Mechrevo.dll") -Force
# R2R cache must be cleared or publish reuses stale output (RID obj dir was the beta2 root cause)
Remove-Item (Join-Path $objDir "win-x64\R2R") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "== 5/7 single-file self-contained publish =="
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
dotnet publish -c Release -r win-x64 -p:SelfContained=true --no-build -o $outDir -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0)
{
    Write-Warning "ReadyToRun publish failed; retrying the same self-contained single-file build without optional R2R precompilation."
    Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    dotnet publish -c Release -r win-x64 -p:SelfContained=true --no-build -o $outDir -p:PublishSingleFile=true -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw "publish failed with and without ReadyToRun" }
}

Write-Host "== 6/7 sign and add release documents =="
$exe = Join-Path $outDir "L-Mechrevo.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "published executable is missing: $exe" }
if ([string]::IsNullOrWhiteSpace($CertificatePath))
{
    Write-Warning "Unsigned development artifact requested. Do not distribute this build."
}
else
{
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath, $CertificatePassword, $flags)
    if (-not $cert.HasPrivateKey) { throw "the signing certificate has no private key" }
    $sig = Set-AuthenticodeSignature -LiteralPath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    if ($sig.Status -ne "Valid") { throw ("signing failed: " + $sig.StatusMessage) }
}
Copy-Item (Join-Path $root "LICENSE") (Join-Path $outDir "LICENSE.txt") -Force
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.txt") $outDir -Force
Copy-Item $guideSource (Join-Path $outDir $guideFileName) -Force
Copy-Item $notesSource (Join-Path $outDir $notesFileName) -Force
$archive = Join-Path $outDir ("L-Mechrevo-" + $artifactLabel + ".zip")
$archiveFiles = @(
    $exe,
    (Join-Path $outDir $guideFileName),
    (Join-Path $outDir $notesFileName),
    (Join-Path $outDir "LICENSE.txt"),
    (Join-Path $outDir "THIRD_PARTY_NOTICES.txt")
)
Compress-Archive -LiteralPath $archiveFiles -DestinationPath $archive -Force

if (-not [string]::IsNullOrWhiteSpace($CertificatePath))
{
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    Copy-Item $exe (Join-Path $releaseDir ("L-Mechrevo-" + $artifactLabel + ".exe")) -Force
    Copy-Item $archive (Join-Path $releaseDir ("L-Mechrevo-" + $artifactLabel + ".zip")) -Force
}
}
finally
{
    Write-Host "== 7/7 restore dev build (non-RID rebuild keeps main dll and zh-CN satellite same-generation) =="
    dotnet build -c Release -t:Rebuild -v q
    $restoreExitCode = $LASTEXITCODE
    Pop-Location
    if ($restoreExitCode -ne 0) { throw "development build restore failed" }
}

Write-Host ""
Write-Host "DONE. Artifacts:"
Get-ChildItem $outDir -Filter "*.exe" | ForEach-Object { Write-Host ("  " + $_.FullName + "  (" + [math]::Round($_.Length/1MB, 1) + " MB)") }
Get-ChildItem $outDir -Filter "*.zip" | ForEach-Object { Write-Host ("  " + $_.FullName + "  (" + [math]::Round($_.Length/1MB, 1) + " MB)") }
Write-Host "NOTE: obj\obfuscated\Mapping.txt is the rename map - do NOT distribute it."
