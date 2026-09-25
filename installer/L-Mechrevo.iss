; ============================================================================
;  L-Mechrevo installer (Inno Setup 6.7.x)
;
;  Per-machine install into {autopf}\L-Mechrevo, Simplified Chinese + English,
;  Start Menu + optional desktop shortcuts, uninstall entry, and silent GCU
;  vendor-payload installation (see installer\README.md).
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
  #define AppVersion "0.289.0-beta19"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.289.0.0"
#endif
#ifndef AppLabel
  #define AppLabel "beta19"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef AppSourceDir
  #define AppSourceDir RepoRoot + "\dist\beta19"
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
; Restart Manager's "close applications" dialog cannot stop a tray L-Mechrevo or a
; Highest autostart task that relaunches it, so Retry never unblocks. We stop the
; task and the process in PrepareToInstall instead, and skip the dialog.
CloseApplications=no
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
chinesesimplified.GcuStatus=正在安装 GCU...
english.GcuStatus=Installing GCU...
chinesesimplified.LaunchApp=运行 {#AppName}
english.LaunchApp=Launch {#AppName}
chinesesimplified.RuntimeRequired=本程序需要 .NET 桌面运行时 10（x64），当前未安装。{break}{break}是否现在从 Microsoft 官方地址自动下载并安装？
english.RuntimeRequired=This app needs the .NET Desktop Runtime 10 (x64), which is not installed.{break}{break}Download and install it now from Microsoft?
chinesesimplified.RuntimeFailed=自动下载或安装 .NET 桌面运行时失败，无法继续。{break}{break}将打开官方下载页面，请手动安装后重新运行本安装程序。
english.RuntimeFailed=Automatic download/install of the .NET Desktop Runtime failed.{break}{break}The official download page will open; install it manually and run setup again.
chinesesimplified.RuntimeDeclined=缺少 .NET 桌面运行时 10（x64），安装无法继续。{break}{break}将打开官方下载页面。
english.RuntimeDeclined=.NET Desktop Runtime 10 (x64) is missing; setup cannot continue.{break}{break}The official download page will open.
chinesesimplified.UninstallingOld=检测到已安装的 L-Mechrevo，将先卸载旧版本再安装新版本。
english.UninstallingOld=An existing L-Mechrevo install was found. Setup will uninstall it first, then install this version.
chinesesimplified.OldVersionUninstallMissing=找不到已安装 L-Mechrevo 的卸载程序，安装已中止。请先手动卸载旧版本后再试。
english.OldVersionUninstallMissing=The existing L-Mechrevo uninstaller was not found. Setup aborted. Uninstall the old version manually and try again.
chinesesimplified.OldVersionUninstallFailed=无法卸载已安装的 L-Mechrevo，安装已中止。请先手动卸载旧版本后再试。
english.OldVersionUninstallFailed=Could not uninstall the existing L-Mechrevo. Setup aborted. Uninstall the old version manually and try again.

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
; --- GCU vendor payload (N6: exactly one tree) --------------------------------
; release\GCU-only is the newest payload and the superset: it serves 30/40/50, carries its own
; UWACPIDriver, and its UserFanTables holds all 24 per-model chassis dirs + the 23 flat files
; (the retired 40-series payloads carry zero per-model dirs). The shared driver tree is NOT
; staged either: it is byte-identical to GCU-only\UWACPIDriver, so staging it would duplicate it.
; The 40-series trees stay on disk untouched as the material for the visible fallback.
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\使用前必看"; Filename: "{app}\用前必看.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"
Name: "{group}\开源许可 (GPL-3.0)"; Filename: "{app}\LICENSE.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; GCU payload install: runs AFTER files are copied. One shipped payload; no generation picker.
; /GCUVARIANT is retired and ignored. Silent: no UI, log under ProgramData.
; N5: the installer is the single place elevation is obtained. Install-Gcu.ps1 creates the
; highest-privileges autostart task, grants the app's directory ACLs and the ACPIDriver access
; while this process is already elevated, so the app never needs to elevate at runtime.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Install-Gcu.ps1"" -StagingRoot ""{app}\GCU"" -TargetDir ""{app}\GCU"" -Variant Auto -InstallerVersion ""{#AppVersionNumeric}"" -LogDir ""{commonappdata}\L-Mechrevo\logs"" -AppExe ""{app}\{#AppExeName}"""; StatusMsg: "{cm:GcuStatus}"; Flags: runhidden waituntilterminated
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
  Text: String;
  Separator: Integer;
begin
  Text := Value;
  if (Length(Text) > 0) and ((Text[1] = 'v') or (Text[1] = 'V')) then
    Delete(Text, 1, 1);
  Separator := Pos('.', Text);
  if Separator > 0 then
    Result := StrToIntDef(Copy(Text, 1, Separator - 1), 0)
  else
    Result := StrToIntDef(Text, 0);
end;

function NamesIncludeDesktopRuntime10(const Names: TArrayOfString): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to GetArrayLength(Names) - 1 do
    if MajorVersionOf(Names[Index]) = 10 then
    begin
      Result := True;
      Exit;
    end;
end;

function HasDesktopRuntime10InView(const RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
begin
  Result := False;
  // Value names are the common layout (e.g. a DWORD named "10.0.8"). Some host/SDK layouts
  // instead write version SUBKEYS. Older 10.x builds also prefix "v". Accept any of those.
  if RegGetValueNames(RootKey, DotNetDesktopSharedFxKey, Names) then
    if NamesIncludeDesktopRuntime10(Names) then
    begin
      Result := True;
      Exit;
    end;
  if RegGetSubkeyNames(RootKey, DotNetDesktopSharedFxKey, Names) then
    Result := NamesIncludeDesktopRuntime10(Names);
end;

function SharedFrameworkRootHasV10(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Root) then
    Exit;
  if FindFirst(AddBackslash(Root) + '*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and 16) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') and
           (MajorVersionOf(FindRec.Name) = 10) then
          Result := True;
      until (not FindNext(FindRec)) or Result;
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasDesktopRuntime10OnDisk: Boolean;
var
  DotnetRoot: String;
begin
  Result :=
    SharedFrameworkRootHasV10(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    SharedFrameworkRootHasV10(ExpandConstant('{pf32}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    SharedFrameworkRootHasV10(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App'));
  if Result then
    Exit;
  DotnetRoot := GetEnv('DOTNET_ROOT');
  if DotnetRoot <> '' then
    Result := SharedFrameworkRootHasV10(AddBackslash(DotnetRoot) + 'shared\Microsoft.WindowsDesktop.App');
  if Result then
    Exit;
  DotnetRoot := GetEnv('ProgramW6432');
  if DotnetRoot <> '' then
    Result := SharedFrameworkRootHasV10(DotnetRoot + '\dotnet\shared\Microsoft.WindowsDesktop.App');
end;

function IsDesktopRuntime10Installed: Boolean;
begin
  // BOTH registry views: which one is populated differs per machine. On the affected machine the
  // 64-bit view of this key is EMPTY and the populated key is in the 32-bit view, so reading only
  // HKLM64 also returned False. Neither the API nor the view may be assumed.
  Result := HasDesktopRuntime10InView(HKLM64) or HasDesktopRuntime10InView(HKLM32) or HasDesktopRuntime10OnDisk;
end;

function TryInstallDesktopRuntime: Boolean;
var
  ExitCode: Integer;
  Installer: String;
begin
  Result := False;
  // DownloadTemporaryFile returns Int64 and RAISES on failure (bad hash, network, TLS), so it
  // must be wrapped - a bare "if not ..." would not even compile. The exception message carries
  // the concrete reason (HTTP status, TLS/proxy error, redirect handling, or a SHA-256 mismatch),
  // so it is logged verbatim rather than as a bare "failed".
  Log('Downloading ' + DotNetRuntimeUrl);
  Log('Expected SHA256: ' + DotNetRuntimeSha256);
  try
    DownloadTemporaryFile(DotNetRuntimeUrl, DotNetRuntimeFileName, DotNetRuntimeSha256, nil);
  except
    Log('DownloadTemporaryFile FAILED for ' + DotNetRuntimeUrl);
    Log('  reason: ' + GetExceptionMessage);
    Log('  (a SHA-256 mismatch, an HTTP status, a TLS/proxy error and a redirect problem all');
    Log('   surface here; the pinned hash is for the immutable versioned blob URL)');
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
  if not Result then
    Log(Format('Desktop runtime installer reported failure (exit %d); see the installer log above', [ExitCode]));
end;

function OpenRuntimeDownloadPage: Boolean;
var
  ExitCode: Integer;
begin
  Result := ShellExec('open', DotNetDownloadPage, '', '', SW_SHOWNORMAL, ewNoWait, ExitCode);
end;

function GetUninstallString: String;
var
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1';
  Result := '';
  if not RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', Result) then
    if not RegQueryStringValue(HKLM32, UninstallKey, 'UninstallString', Result) then
      RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Result);
end;

function UnInstallOldVersion: String;
var
  Uninstaller: String;
  ExitCode: Integer;
  KillCode: Integer;
begin
  Result := '';
  Uninstaller := RemoveQuotes(GetUninstallString);
  if Uninstaller = '' then
    Exit;

  if not FileExists(Uninstaller) then
  begin
    Result := ExpandConstant('{cm:OldVersionUninstallMissing}');
    Exit;
  end;

  if not WizardSilent then
    MsgBox(ExpandConstant('{cm:UninstallingOld}'), mbInformation, MB_OK);

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM L-Mechrevo.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, KillCode);

  if not Exec(Uninstaller, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := ExpandConstant('{cm:OldVersionUninstallFailed}');
    Exit;
  end;

  if (ExitCode <> 0) and (ExitCode <> 3010) then
    Result := ExpandConstant('{cm:OldVersionUninstallFailed}');
end;

procedure StopLockedAppProcesses;
var
  ResultCode: Integer;
begin
  // Highest autostart (LMechrevo / LMechrevo_<SID>) restarts the exe after taskkill
  // (RestartCount=3). Disable first, then kill, then wait so file locks drop.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like ''LMechrevo*'' } | ForEach-Object { Stop-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue; Disable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM L-Mechrevo.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Answer: Integer;
begin
  StopLockedAppProcesses;
  Result := UnInstallOldVersion;
  if Result <> '' then
    Exit;

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

function NeedRestart(): Boolean;
begin
  // Suggest a reboot after GCU/driver install. The finished page lets the user postpone
  // (Yes now / No later). Silent installs still honor /NORESTART.
  Result := True;
end;
