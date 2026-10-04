[CmdletBinding()]
param([string]$InstallerPath, [switch]$ValidateOnly)

$ErrorActionPreference = 'Stop'
$expectedHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
try {
    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        $downloadDir = Join-Path ([IO.Path]::GetTempPath()) ('L-Mechrevo-PawnIO-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadDir | Out-Null
        $InstallerPath = Join-Path $downloadDir 'PawnIO_setup.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' -OutFile $InstallerPath -TimeoutSec 120
    }
    $InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
    if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'PawnIO 2.2.0 SHA256 mismatch; installation refused.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne 'Valid') { throw ('PawnIO digital signature is not valid: ' + $signature.Status) }
    if ($ValidateOnly) { Write-Output 'PawnIO installer hash and signature verified.'; exit 0 }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges required.' }
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'"
    if ($driver -and $driver.State -eq 'Running') { Write-Output 'PawnIO is already running.'; exit 0 }
    $process = Start-Process -FilePath $InstallerPath -ArgumentList '-install','-silent' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -eq 3010) { Write-Output 'PawnIO installed; restart required.'; exit 3010 }
    if ($process.ExitCode -ne 0) { throw ('PawnIO installer exit code: ' + $process.ExitCode) }
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'"
    if (-not $driver -or $driver.State -ne 'Running') { throw 'PawnIO installation returned success, but the driver is not running.' }
    Write-Output 'PawnIO installed and running.'
    exit 0
} catch { Write-Error $_; exit 1 }
