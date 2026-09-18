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
  #define AppVersion "0.289.0-beta18"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.289.0.0"
#endif
#ifndef AppLabel
  #define AppLabel "beta18"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef AppSourceDir
  #define AppSourceDir RepoRoot + "\dist\beta18"
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif

#define AppExeName "L-Mechrevo.exe"

; --- GCU payload mode (T22 / gate G0) ---------------------------------------
; Single-payload mode ships only release\GCU-only for every supported dGPU
; generation. Gated on G0: real 30-series AND 40-series hardware must prove the
; 1.2.0.0 payload serves them before this define may be turned on. Off by default
; keeps all four trees bundled.
#ifdef SingleGcuPayload
  #define GcuModeArgs " -SinglePayload"
#else
  #define GcuModeArgs ""
#endif

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
chinesesimplified.RuntimeRequired=本程序需要 .NET 桌面运行时 10（x64），当前未安装。{break}{break}是否现在从 Microsoft 官方地址自动下载并安装？
english.RuntimeRequired=This app needs the .NET Desktop Runtime 10 (x64), which is not installed.{break}{break}Download and install it now from Microsoft?
chinesesimplified.RuntimeFailed=自动下载或安装 .NET 桌面运行时失败，无法继续。{break}{break}将打开官方下载页面，请手动安装后重新运行本安装程序。
english.RuntimeFailed=Automatic download/install of the .NET Desktop Runtime failed.{break}{break}The official download page will open; install it manually and run setup again.
chinesesimplified.RuntimeDeclined=缺少 .NET 桌面运行时 10（x64），安装无法继续。{break}{break}将打开官方下载页面。
english.RuntimeDeclined=.NET Desktop Runtime 10 (x64) is missing; setup cannot continue.{break}{break}The official download page will open.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; --- application (framework-dependent publish: the whole publish folder, not a single file) ---
; The app is NOT self-contained; it needs the .NET Desktop Runtime 10 (x64), which [Code]
; verifies (and can download) before any file is copied. Staging the entire folder is what
; makes a framework-dependent app runnable; shipping only the .exe would produce a broken install.
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; --- shipped documents ---
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\更新日志.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\用前必看.txt"; DestDir: "{app}"; Flags: ignoreversion
; --- GCU installer scripts ---
Source: "Select-GcuPayload.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Install-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
Source: "Uninstall-Gcu.ps1"; DestDir: "{app}\GCU"; Flags: ignoreversion
; --- GCU vendor payloads ------------------------------------------------------
; Single-payload (G0): release\GCU-only serves 30/40/50 with the 1.2.0.0 payload.
; Multi-payload (default while G0 is unpassed): all four trees, selector picks one.
#ifdef SingleGcuPayload
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
; 50-series  : release\GCU-only        (AiStoneService + UWACPIDriver + scripts) -> payload\50
; 40-series  : release\GCU-40-51751    (AiStoneService variant)                  -> payload\40-51751
; 40-series  : release\GCU-40-51749    (UniwillService variant)                  -> payload\40-51749
; shared     : release\GCU-common      (UWACPIDriver, byte-identical across gens) -> payload\common
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-40-51749\*"; DestDir: "{app}\GCU\payload\40-51749"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-40-51751\*"; DestDir: "{app}\GCU\payload\40-51751"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-common\*"; DestDir: "{app}\GCU\payload\common"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

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
; 40-series variant with /GCUVARIANT=40-51749 (multi-payload only; retired once G0 enables single-payload). Silent: no UI, log under ProgramData.
; N5: the installer is the single place elevation is obtained. Install-Gcu.ps1 creates the
; highest-privileges autostart task, grants the app's directory ACLs and the ACPIDriver access
; while this process is already elevated, so the app never needs to elevate at runtime.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Install-Gcu.ps1"" -StagingRoot ""{app}\GCU"" -TargetDir ""{app}\GCU"" -Variant ""{param:GCUVARIANT|Auto}""{#GcuModeArgs} -InstallerVersion ""{#AppVersionNumeric}"" -LogDir ""{commonappdata}\L-Mechrevo\logs"" -AppExe ""{app}\{#AppExeName}"""; StatusMsg: "{cm:GcuStatus}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop/remove the GCU service + firewall rule + staged UWACPI driver BEFORE files are deleted.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Uninstall-Gcu.ps1"" -TargetDir ""{app}\GCU"" -LogDir ""{commonappdata}\L-Mechrevo\logs"""; Flags: runhidden waituntilterminated; RunOnceId: "GcuUninstall"

[UninstallDelete]
; Logs written outside {app} during install/uninstall.
Type: filesandordirs; Name: "{commonappdata}\L-Mechrevo\logs"
; Runtime copies made by Install-Gcu.ps1 that Inno never registered (T23): without these the
; selected payload dir and the driver copy survive uninstall and leak onto disk.
Type: filesandordirs; Name: "{app}\GCU\AiStoneService"
Type: filesandordirs; Name: "{app}\GCU\UniwillService"
Type: filesandordirs; Name: "{app}\GCU\UWACPIDriver"

[Code]
// ============================================================================
//  .NET Desktop Runtime 10 (x64) detection + in-installer download (R2).
//
//  The app is framework-dependent: no runtime DLLs are shipped, so the runtime
//  must already be installed or be obtained here. We read the runtime's own
//  registry manifest instead of invoking `dotnet --list-runtimes`, because the
//  installer must not assume `dotnet` is on PATH.
//
//  No offline fallback exists by design: the owner forbade bundling a runtime
//  installer. The in-installer download is the only automatic path; if it fails
//  the browser opens at the official page (interactive) or setup exits non-zero
//  (silent).
// ============================================================================
const
  DotNetDesktopSharedFxKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  // Versioned Microsoft blob URL is immutable, so the pinned SHA256 stays valid.
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe';
  DotNetRuntimeSha256 = '0B907E9312867172A4EB82F4B5AB3F7C2D25E27D8349546D77EEB5D5B8CBECAB9EBEBFED189B13E9669DC578D892756C548366DA539D47B3DDAC5EFCB7AE72FE';
  DotNetRuntimeFileName = 'windowsdesktop-runtime-10.0.12-win-x64.exe';
  DotNetDownloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0';

function MajorVersionOf(const Value: String): Integer;
var
  Separator: Integer;
begin
  Separator := Pos('.', Value);
  if Separator > 0 then
    Result := StrToIntDef(Copy(Value, 1, Separator - 1), 0)
  else
    Result := StrToIntDef(Value, 0);
end;

function IsDesktopRuntime10Installed: Boolean;
var
  Names: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  // HKLM64: the .NET installer writes the 64-bit view; a 32-bit setup reading plain
  // HKLM would see WOW6432Node and always conclude "missing".
  if RegGetSubkeyNames(HKLM64, DotNetDesktopSharedFxKey, Names) then
    for Index := 0 to GetArrayLength(Names) - 1 do
      if MajorVersionOf(Names[Index]) = 10 then
      begin
        Result := True;
        Exit;
      end;
end;

function TryInstallDesktopRuntime: Boolean;
var
  ExitCode: Integer;
  Installer: String;
begin
  Result := False;
  // DownloadTemporaryFile returns Int64 and RAISES on failure (bad hash, network, TLS), so it
  // must be wrapped - a bare "if not ..." would not even compile.
  try
    DownloadTemporaryFile(DotNetRuntimeUrl, DotNetRuntimeFileName, DotNetRuntimeSha256, nil);
  except
    Log('DownloadTemporaryFile failed: ' + GetExceptionMessage);
    Exit;
  end;
  Installer := ExpandConstant('{tmp}\') + DotNetRuntimeFileName;
  if not Exec(Installer, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then
  begin
    Log('Could not launch the runtime installer: ' + Installer);
    Exit;
  end;
  Log(Format('Desktop runtime installer exit code: %d', [ExitCode]));
  // 0 = installed, 1638 = a newer version is already present, 3010 = installed + reboot pending
  Result := (ExitCode = 0) or (ExitCode = 1638) or (ExitCode = 3010);
end;

function OpenRuntimeDownloadPage: Boolean;
var
  ExitCode: Integer;
begin
  Result := ShellExec('open', DotNetDownloadPage, '', '', SW_SHOWNORMAL, ewNoWait, ExitCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Answer: Integer;
begin
  Result := '';
  if IsDesktopRuntime10Installed then
    Exit;

  if WizardSilent then
  begin
    // Unattended: try to install it; on failure fail the setup instead of opening a browser
    // nobody is looking at. The non-empty result aborts with a non-zero exit code.
    if (not TryInstallDesktopRuntime) or (not IsDesktopRuntime10Installed) then
    begin
      Result := 'The .NET Desktop Runtime 10 (x64) is required and could not be installed automatically.';
      Exit;
    end;
    Exit;
  end;

  Answer := MsgBox(ExpandConstant('{cm:RuntimeRequired}'), mbConfirmation, MB_YESNO);
  if Answer <> IDYES then
  begin
    OpenRuntimeDownloadPage;
    Result := ExpandConstant('{cm:RuntimeDeclined}');
    Exit;
  end;

  if (not TryInstallDesktopRuntime) or (not IsDesktopRuntime10Installed) then
  begin
    OpenRuntimeDownloadPage;
    Result := ExpandConstant('{cm:RuntimeFailed}');
  end;
end;
