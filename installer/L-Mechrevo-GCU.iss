; ============================================================================
;  L-Mechrevo GCU-only installer
;
;  Repairs/reinstalls the vendor GCU payload into {autopf}\L-Mechrevo\GCU.
;  No app binaries, no .NET runtime check. If L-Mechrevo.exe is already there,
;  autostart is registered; otherwise it is skipped.
; ============================================================================

#ifndef AppName
  #define AppName "L-Mechrevo GCU"
#endif
#ifndef AppPublisher
  #define AppPublisher "L-Mechrevo contributors"
#endif
#ifndef AppVersion
  #define AppVersion "0.289.0-beta20"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.289.0.0"
#endif
#ifndef AppLabel
  #define AppLabel "beta20"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif

[Setup]
AppId={{8F4E2C71-9B3A-4D6E-A1C2-7E5B9D0F3A65}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 L-Mechrevo contributors. GPL-3.0-only.
DefaultDirName={autopf}\L-Mechrevo
DefaultGroupName=L-Mechrevo
DisableProgramGroupPage=yes
DisableDirPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=L-Mechrevo-GCU-setup
Compression={#Compression}
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=yes
CloseApplications=no
RestartApplications=no
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\src\MechrevoLiteWin\favicon.ico
UninstallDisplayName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersionNumeric}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersionNumeric}
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.GcuStatus=正在安装 GCU...
english.GcuStatus=Installing GCU...

[Files]
Source: "Select-GcuPayload.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Install-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Uninstall-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Install-Gcu.ps1"" -StagingRoot ""{app}\GCU"" -TargetDir ""{app}\GCU"" -Variant Auto -InstallerVersion ""{#AppVersionNumeric}"" -LogDir ""{commonappdata}\L-Mechrevo\logs"" -AppExe ""{app}\L-Mechrevo.exe"""; StatusMsg: "{cm:GcuStatus}"; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Uninstall-Gcu.ps1"" -TargetDir ""{app}\GCU"" -LogDir ""{commonappdata}\L-Mechrevo\logs"""; Flags: runhidden waituntilterminated; RunOnceId: "GcuOnlyUninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\GCU\AiStoneService"
Type: filesandordirs; Name: "{app}\GCU\UniwillService"
Type: filesandordirs; Name: "{app}\GCU\UWACPIDriver"
Type: filesandordirs; Name: "{app}\GCU\payload"

[Code]
function NeedRestart(): Boolean;
begin
  Result := True;
end;
