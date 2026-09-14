$ErrorActionPreference = 'Continue'
$k1 = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('OEM\GamingCenter2')
$k1.SetValue('UninstallCalibration', 0, 'DWord')
$k1.Close()
$k2 = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('OEM\GamingCenter2\MySetting\DisplayFeatures')
$k2.SetValue('ColorCalibration', 1, 'DWord')
$k2.Close()
"v1=" + [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\OEM\GamingCenter2', 'UninstallCalibration', 'missing')
"v2=" + [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\OEM\GamingCenter2\MySetting\DisplayFeatures', 'ColorCalibration', 'missing')
