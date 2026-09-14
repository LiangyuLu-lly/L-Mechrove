@echo off
reg add "HKLM\OEM\GamingCenter2" /v UninstallCalibration /t REG_DWORD /d 0 /f
reg add "HKLM\OEM\GamingCenter2\MySetting\DisplayFeatures" /v ColorCalibration /t REG_DWORD /d 1 /f
reg query "HKLM\OEM\GamingCenter2" /v UninstallCalibration
reg query "HKLM\OEM\GamingCenter2\MySetting\DisplayFeatures"
