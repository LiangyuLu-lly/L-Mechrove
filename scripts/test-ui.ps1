param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $repoRoot "artifacts\ui-audit-$stamp"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

$project = Join-Path $repoRoot 'src\MechrevoLiteWin\MechrevoLite.csproj'
dotnet build $project -c $Configuration -p:GITHUB_ACTIONS=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $repoRoot "src\MechrevoLiteWin\bin\x64\$Configuration\net10.0-windows10.0.19041.0\L-Mechrevo.exe"
$process = Start-Process -FilePath $exe -ArgumentList @('--ui-audit', $OutputPath) -WindowStyle Hidden -PassThru -Wait

$reportPath = Join-Path $OutputPath 'ui-audit.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    throw "UI audit did not create $reportPath"
}

$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Write-Host "UI screenshots: $($report.ScreenshotCount)"
Write-Host "UI issues:      $($report.IssueCount)"
Write-Host "UI report:      $reportPath"

if ($process.ExitCode -ne 0 -or $report.IssueCount -ne 0) {
    $report.Issues | Format-Table Form, Viewport, Kind, Control, Detail -Wrap
    exit 1
}

exit 0
