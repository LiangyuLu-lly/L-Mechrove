; ============================================================================
;  L-Mechrevo installer (Inno Setup 6.7.x)
;
;  Per-machine install into {autopf}\L-Mechrevo, Simplified Chinese + English,
;  Start Menu + optional desktop shortcuts, uninstall entry, and generation-matched
;  silent GCU vendor-payload installation (see installer\README.md).
;
;  Build with installer\Build-Installer.ps1, which reads the version from
;  src\MechrevoLiteWin\MechrevoLite.csproj and passes /DAppVersion, /DAppLabel,
;  /DAppVersionNumeric, /DRepoRoot and /DAppSourceDir. The #ifndef defaults below
;  let the script compile standalone against the current release tree.
; ============================================================================

#ifndef AppName
  #define AppName "L-Mechrevo"
#endif
#ifndef AppPublisher
  #define AppPublisher "L-Mechrevo contributors"
#endif
#ifndef AppVersion
  #define AppVersion "0.289.0-beta14"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.289.0.0"
#endif
#ifndef AppLabel
  #define AppLabel "beta14"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef AppSourceDir
  #define AppSourceDir RepoRoot + "\dist\beta14"
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif

#define AppExeName "L-Mechrevo.exe"

[Setup]
AppId={{8F4E2C71-9B3A-4D6E-A1C2-7E5B9D0F3A64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 L-Mechrevo contributors. GPL-3.0-only.
DefaultDirName={autopf}\L-Mechrevo
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppLabel}-setup
Compression={#Compression}
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
WizardSizePercent=120
DisableWelcomePage=no
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\src\MechrevoLiteWin\favicon.ico
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersionNumeric}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersionNumeric}
ShowLanguageDialog=auto

[Languages]
; Simplified Chinese first so it is the default selection.
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.GcuStatus=正在安装 GCU 硬件服务（按显卡代际自动选择组件）...
english.GcuStatus=Installing the GCU hardware service (payload selected by GPU generation)...
chinesesimplified.LaunchApp=运行 {#AppName}
english.LaunchApp=Launch {#AppName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; --- application (self-contained single-file publish; no .NET download needed) ---
Source: "{#AppSourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; --- shipped documents ---
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\更新日志.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\用前必看.txt"; DestDir: "{app}"; Flags: ignoreversion
; --- GCU installer scripts ---
Source: "Select-GcuPayload.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Install-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Uninstall-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
; --- GCU vendor payloads (all four trees bundled; selection rule picks one) ---
; 50-series  : release\GCU-only        (AiStoneService + UWACPIDriver + scripts) -> payload\50
; 40-series  : release\GCU-40-51751    (AiStoneService variant)                  -> payload\40-51751
; 40-series  : release\GCU-40-51749    (UniwillService variant)                  -> payload\40-51749
; shared     : release\GCU-common      (UWACPIDriver, byte-identical across gens) -> payload\common
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-40-51749\*"; DestDir: "{app}\GCU\payload\40-51749"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-40-51751\*"; DestDir: "{app}\GCU\payload\40-51751"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-common\*"; DestDir: "{app}\GCU\payload\common"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\使用前必看"; Filename: "{app}\用前必看.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"
Name: "{group}\开源许可 (GPL-3.0)"; Filename: "{app}\LICENSE.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; GCU payload install: runs AFTER files are copied, selects the payload by GPU generation
; (50-series -> payload\50; otherwise -> payload\40-51751 by default). Override the
; 40-series variant with /GCUVARIANT=40-51749. Silent: no UI, log under ProgramData.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Install-Gcu.ps1"" -StagingRoot ""{app}\GCU"" -TargetDir ""{app}\GCU"" -Variant ""{param:GCUVARIANT|Auto}"" -LogDir ""{commonappdata}\L-Mechrevo\logs"""; StatusMsg: "{cm:GcuStatus}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop/remove the GCU service + firewall rule + staged UWACPI driver BEFORE files are deleted.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Uninstall-Gcu.ps1"" -TargetDir ""{app}\GCU"" -LogDir ""{commonappdata}\L-Mechrevo\logs"""; Flags: runhidden waituntilterminated; RunOnceId: "GcuUninstall"

[UninstallDelete]
; Logs written outside {app} during install/uninstall.
Type: filesandordirs; Name: "{commonappdata}\L-Mechrevo\logs"
