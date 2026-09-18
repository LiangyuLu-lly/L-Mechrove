#Requires -Version 5.1
# ASCII-only by design (Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM).
# Uninstall counterpart for Install-Gcu.ps1: stops/deletes the GCUBridge service,
# removes the firewall rule it added, and (best effort) removes the UWACPI driver
# staged into the Windows driver store. Safe to run when nothing is installed.
<#
.SYNOPSIS
    Remove everything Install-Gcu.ps1 installed for L-Mechrevo's GCU.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Uninstall-Gcu.ps1 `
        -TargetDir "C:\Program Files\L-Mechrevo\GCU"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [string]$LogDir,
    [switch]$KeepDriver,
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:RuleName = 'L-Mechrevo - Block remote GCU MQTT'
# Every bundled vendor payload (50-series and both 40-series variants) registers the same
# GCUBridge name, so the legacy registration shares it; iterate the list so a name added by an
# older vendor install.bat is still removed.
$script:ServiceName = 'GCUBridge'
$script:LegacyServiceNames = @('GCUBridge')
$script:LogFile = $null

function Write-Log {
    param([string]$Message)
    $line = ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message)
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
    Write-Host $line
}

function Remove-GcuService {
    foreach ($name in $script:LegacyServiceNames) {
        Remove-SingleGcuService -Name $name
    }
}

function Remove-SingleGcuService {
    param([string]$Name)
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Log ("  {0} service not present" -f $Name)
        return
    }
    Write-Log ("  stopping {0} (status={1})" -f $Name, $service.Status)
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    foreach ($processName in @('GCUService', 'GCUBridge')) {
        foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
            Write-Log ("  stopping leftover process {0} (pid {1})" -f $processName, $process.Id)
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    & sc.exe delete $Name | Out-Null
    $waited = 0
    while ((Get-Service -Name $Name -ErrorAction SilentlyContinue) -and $waited -lt 20) {
        Start-Sleep -Milliseconds 500
        $waited++
    }
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        Write-Log ("  WARNING: service {0} still exists after delete attempt (may clear after reboot)" -f $Name)
    }
    else {
        Write-Log ("  service {0} deleted" -f $Name)
    }
}

function Remove-FirewallRule {
    $cmd = Get-Command -Name Remove-NetFirewallRule -ErrorAction SilentlyContinue
    if ($cmd) {
        $existing = Get-NetFirewallRule -DisplayName $script:RuleName -ErrorAction SilentlyContinue
        if ($existing) {
            Remove-NetFirewallRule -DisplayName $script:RuleName -ErrorAction SilentlyContinue
            Write-Log '  firewall block rule removed'
        }
        else {
            Write-Log '  firewall block rule not present'
        }
        return
    }
    & netsh.exe advfirewall firewall delete rule ("name=" + $script:RuleName) | Out-Null
    Write-Log '  firewall rule removed via netsh (best effort)'
}

function Remove-UwacpiDriver {
    $text = ((& pnputil.exe /enum-drivers 2>&1) | Out-String)
    $blocks = [regex]::Matches($text, '(?ms)Published Name:\s*(oem\d+\.inf).*?(?=Published Name:|\z)')
    $published = $null
    foreach ($block in $blocks) {
        if ($block.Value -match '(?i)Original Name:\s*uwacpidriver\.inf') {
            $published = $block.Groups[1].Value
            break
        }
    }
    if (-not $published) {
        Write-Log '  UWACPIDriver not found in the driver store (nothing to remove)'
        return
    }
    Write-Log ("  pnputil /delete-driver {0} /uninstall /force" -f $published)
    $output = & pnputil.exe /delete-driver $published /uninstall /force 2>&1
    $output | ForEach-Object { Write-Log ('    ' + [string]$_) }
    if ($LASTEXITCODE -ne 0) {
        Write-Log ("  WARNING: driver removal returned {0} (may require reboot)" -f $LASTEXITCODE)
    }
}

function Remove-AutostartTask {
    # N5: the installer created a highest-privileges autostart task; uninstall must remove it so no
    # boot-time elevation entry point is left behind. Match the app's naming (LMechrevo_<SID>) and
    # also sweep the legacy names the app used to register.
    $sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
    $names = @('LMechrevo_' + $sid, 'LMechrevo', 'LMechrevoCharge', 'LMechrevo_' + $sid + 'Charge')
    foreach ($name in $names) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) {
            Write-Log ("  autostart task not present: {0}" -f $name)
            continue
        }
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Write-Log ("  WARNING: autostart task {0} still exists after removal" -f $name)
        }
        else {
            Write-Log ("  autostart task removed: {0}" -f $name)
        }
    }
}

function Remove-InstallMarker {
    $key = 'HKLM:\SOFTWARE\L-Mechrevo'
    if (Test-Path -LiteralPath $key) {
        foreach ($name in @('GcuVariant', 'GcuServiceDir', 'GcuServiceExe', 'GcuInstalledUtc')) {
            Remove-ItemProperty -LiteralPath $key -Name $name -ErrorAction SilentlyContinue
        }
        Write-Log '  install marker values removed'
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        $LogDir = Join-Path $env:ProgramData 'L-Mechrevo\logs'
    }
    if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    $script:LogFile = Join-Path $LogDir ('gcu-uninstall-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

    Write-Log '=== L-Mechrevo GCU payload uninstall ==='
    Write-Log ("TargetDir  = {0}" -f $TargetDir)
    Write-Log ("KeepDriver = {0}" -f [bool]$KeepDriver)
    Write-Log ("DryRun     = {0}" -f [bool]$DryRun)

    if ($DryRun) {
        Write-Log 'DRY RUN: no system changes made'
        exit 0
    }

    # N5: remove the installer-created highest-privileges autostart task first, so no boot-time
    # elevation entry point survives the uninstall.
    Remove-AutostartTask
    Remove-GcuService
    Remove-FirewallRule
    if ($KeepDriver) {
        Write-Log '  -KeepDriver set; leaving UWACPIDriver installed'
    }
    else {
        Remove-UwacpiDriver
    }
    Remove-InstallMarker
    Write-Log 'GCU uninstall OK'
    exit 0
}
catch {
    if ($script:LogFile) {
        Write-Log ('FATAL: ' + $_.Exception.Message)
        Write-Log ($_.ScriptStackTrace)
    }
    else {
        Write-Error $_
    }
    exit 1
}
