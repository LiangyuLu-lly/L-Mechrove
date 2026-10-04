; ============================================================================
;  L-Mechrevo installer (Inno Setup 6.7.x)
;
;  Per-machine install into {autopf}\L-Mechrevo, Simplified Chinese + English,
;  Start Menu + optional desktop shortcuts, uninstall entry, and silent GCU
;  vendor-payload installation (see installer\README.md).
;
;  Upgrades are OVERLAY installs (beta21): the previous version is no longer
;  uninstalled first - that deleted the GCU service's user state (modes, fan
;  curves) on every update. Same version = repair; an older installer refuses
;  to downgrade. /RELAUNCH=1 (used by the in-app updater together with
;  /VERYSILENT) starts the app again after a silent install.
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
  #define AppVersion "0.290.6"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.290.6.0"
#endif
#ifndef AppLabel
  #define AppLabel "0.290.6"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef AppSourceDir
  #define AppSourceDir RepoRoot + "\dist\0.290.6"
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
; %n is the line break in custom messages. The break constant used before is only valid in
; multi-string registry values and showed up literally in these dialogs.
chinesesimplified.GcuStatus=正在安装 GCU...
english.GcuStatus=Installing GCU...
chinesesimplified.LaunchApp=运行 {#AppName}
english.LaunchApp=Launch {#AppName}
chinesesimplified.RuntimeRequired=本程序需要 .NET 桌面运行时 10（x64），当前未安装。%n%n是否现在从 Microsoft 官方地址自动下载并安装？
english.RuntimeRequired=This app needs the .NET Desktop Runtime 10 (x64), which is not installed.%n%nDownload and install it now from Microsoft?
chinesesimplified.RuntimeFailed=自动下载或安装 .NET 桌面运行时失败，无法继续。%n%n将打开官方下载页面，请手动安装后重新运行本安装程序。
english.RuntimeFailed=Automatic download/install of the .NET Desktop Runtime failed.%n%nThe official download page will open; install it manually and run setup again.
chinesesimplified.RuntimeDeclined=缺少 .NET 桌面运行时 10（x64），安装无法继续。%n%n将打开官方下载页面。
english.RuntimeDeclined=.NET Desktop Runtime 10 (x64) is missing; setup cannot continue.%n%nThe official download page will open.
chinesesimplified.SameVersionRepair=已安装相同版本 {#AppVersion}。%n%n是否重新安装以修复？你的设置、性能模式与风扇曲线都会保留。
english.SameVersionRepair=Version {#AppVersion} is already installed.%n%nReinstall it to repair? Your settings, performance modes and fan curves are kept.
chinesesimplified.DowngradeBlocked=已安装更新的版本 %1，不能用较旧的 {#AppVersion} 覆盖安装。%n%n如需回退，请先在「设置 - 应用」中卸载当前版本。
english.DowngradeBlocked=A newer version (%1) is already installed; {#AppVersion} cannot be installed over it.%n%nTo roll back, uninstall the current version first.

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
; --- GCU vendor payloads (Install-Gcu.ps1 installs the one that matches this machine) ----------
; release\GCU-only: the newest payload for 30/40/50 (and any undeterminable generation). It carries
; its own UWACPIDriver, and its UserFanTables holds all 24 per-model chassis dirs + the 23 flat files.
; release\GCU-1020: the GamingCenterU 1.1.0.49 legacy service (GCUBridge 1.0.1.4 + GCUService
; 1.0.2.47 + ACPIDriver) for GTX 10 / GTX 16 / RTX 20 - the service those machines shipped with.
; The retired 40-series trees and the shared driver tree are not staged.
Source: "{#RepoRoot}\release\GCU-only\*"; DestDir: "{app}\GCU\payload\50"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\release\GCU-1020\*"; DestDir: "{app}\GCU\payload\1020"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\使用前必看"; Filename: "{app}\用前必看.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"
Name: "{group}\开源许可 (GPL-3.0)"; Filename: "{app}\LICENSE.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; GCU payload install: runs AFTER files are copied. The payload is chosen by GPU generation inside
; Install-Gcu.ps1; /GCUVARIANT is retired and ignored. Silent: no UI, log under ProgramData.
; N5: the installer is the single place elevation is obtained. Install-Gcu.ps1 creates the
; highest-privileges autostart task, re-enables the app's scheduled tasks and grants the config/log
; directory ACLs while this process is already elevated.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Install-Gcu.ps1"" -StagingRoot ""{app}\GCU"" -TargetDir ""{app}\GCU"" -Variant Auto -InstallerVersion ""{#AppVersionNumeric}"" -LogDir ""{commonappdata}\L-Mechrevo\logs"" -AppExe ""{app}\{#AppExeName}"""; StatusMsg: "{cm:GcuStatus}"; Flags: runhidden waituntilterminated
; Interactive install: the finish-page checkbox (postinstall runs as the original, non-elevated
; user; the app then elevates itself through its Highest autostart task without a UAC prompt).
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; Silent in-app update (/VERYSILENT /RELAUNCH=1): start the app again for the user who started setup.
Filename: "{app}\{#AppExeName}"; Parameters: "--after-update"; Flags: nowait runasoriginaluser; Check: ShouldRelaunchAfterSilentInstall

[UninstallRun]
; Stop/remove the GCU service + firewall rule + staged ACPI driver BEFORE files are deleted.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\GCU\Uninstall-Gcu.ps1"" -TargetDir ""{app}\GCU"" -LogDir ""{commonappdata}\L-Mechrevo\logs"""; Flags: runhidden waituntilterminated; RunOnceId: "GcuUninstall"

[UninstallDelete]
; Logs written outside {app} during install/uninstall.
Type: filesandordirs; Name: "{commonappdata}\L-Mechrevo\logs"
; Runtime copies made by Install-Gcu.ps1 that Inno never registered (T23): without these the
; selected payload dir and the driver copy survive uninstall and leak onto disk.
Type: filesandordirs; Name: "{app}\GCU\AiStoneService"
Type: filesandordirs; Name: "{app}\GCU\UniwillService"
Type: filesandordirs; Name: "{app}\GCU\UWACPIDriver"
Type: filesandordirs; Name: "{app}\GCU\ACPIDriver"
Type: filesandordirs; Name: "{app}\GCU\state-backup"
Type: files; Name: "{app}\GCU\gcu-registry-*.reg"

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
  // Versioned Microsoft blob URL is immutable, so the pinned SHA-256 stays valid.
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe';
  // SHA-256 (64 hex digits). The value here used to be the file's SHA-512 (128 digits) taken from
  // Microsoft's release metadata, so every download failed verification and fell back to the browser.
  DotNetRuntimeSha256 = '55a67d8476cde95a9cc43a95803b4f54446e7c9251ed22aa6421d4908174ae84';
  DotNetRuntimeFileName = 'windowsdesktop-runtime-10.0.12-win-x64.exe';
  DotNetDownloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  AppRegistryKey = 'SOFTWARE\L-Mechrevo';

var
  TasksDisabledBySetup: Boolean;

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

// ============================================================================
//  Version ordering for upgrade / repair / downgrade decisions.
//  "0.289.0-beta21" -> base 0.289.0, pre-release beta 21. A final release (no suffix) sorts
//  after every beta of the same base. AppVersionNumeric cannot be used: all 0.289.0 betas share it.
// ============================================================================
function NextNumber(const Text: String; var Index: Integer): Integer;
var
  Start: Integer;
begin
  Result := 0;
  while (Index <= Length(Text)) and not ((Text[Index] >= '0') and (Text[Index] <= '9')) do
    Index := Index + 1;
  Start := Index;
  while (Index <= Length(Text)) and (Text[Index] >= '0') and (Text[Index] <= '9') do
    Index := Index + 1;
  if Index > Start then
    Result := StrToIntDef(Copy(Text, Start, Index - Start), 0);
end;

function AppVersionKey(const Version: String): Int64;
var
  Base, Suffix: String;
  Dash, Index, Major, Minor, Patch, Beta: Integer;
  Key: Int64;
begin
  Dash := Pos('-', Version);
  if Dash > 0 then
  begin
    Base := Copy(Version, 1, Dash - 1);
    Suffix := Copy(Version, Dash + 1, Length(Version));
  end
  else
  begin
    Base := Version;
    Suffix := '';
  end;
  Index := 1;
  Major := NextNumber(Base, Index);
  Minor := NextNumber(Base, Index);
  Patch := NextNumber(Base, Index);
  if Suffix = '' then
    Beta := 9999
  else
  begin
    Index := 1;
    Beta := NextNumber(Suffix, Index);
  end;
  Key := Major;
  Key := Key * 1000 + Minor;
  Key := Key * 1000 + Patch;
  Key := Key * 10000 + Beta;
  Result := Key;
end;

function GetInstalledAppVersion: String;
var
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1';
  Result := '';
  if not RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM32, UninstallKey, 'DisplayVersion', Result) then
      RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result);
end;

function InitializeSetup: Boolean;
var
  Installed: String;
  InstalledKey, IncomingKey: Int64;
begin
  Result := True;
  Installed := GetInstalledAppVersion;
  if Installed = '' then
    Exit;
  InstalledKey := AppVersionKey(Installed);
  IncomingKey := AppVersionKey('{#AppVersion}');
  Log(Format('Installed version %s, this installer %s', [Installed, '{#AppVersion}']));
  if InstalledKey > IncomingKey then
  begin
    // Never overwrite a newer install with older binaries (silent: fail with a non-zero exit code).
    // CustomMessage returns the raw text; FmtMessage fills %1 and turns %n into line breaks.
    if not WizardSilent then
      MsgBox(FmtMessage(CustomMessage('DowngradeBlocked'), [Installed]), mbError, MB_OK);
    Log('Refusing to downgrade: ' + Installed + ' is newer than {#AppVersion}');
    Result := False;
    Exit;
  end;
  if (InstalledKey = IncomingKey) and not WizardSilent then
    Result := MsgBox(FmtMessage(CustomMessage('SameVersionRepair'), ['']), mbConfirmation, MB_YESNO) = IDYES;
end;

procedure WaitForUpdatingApp;
var
  Pid, ResultCode: Integer;
begin
  // In-app update: the app passes its PID and exits on its own (config flushed, tray removed).
  // Wait for that instead of killing it mid-shutdown. Only a positive number is accepted, so the
  // parameter can never inject anything into the command line.
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid <= 0 then
    Exit;
  Log(Format('Waiting for the updating app (pid %d) to exit', [Pid]));
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Format('-NoProfile -WindowStyle Hidden -Command "Wait-Process -Id %d -Timeout 20 -ErrorAction SilentlyContinue"', [Pid]),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopLockedAppProcesses;
var
  ResultCode: Integer;
begin
  WaitForUpdatingApp;
  // Highest autostart (LMechrevo / LMechrevo_<SID>) restarts the exe after taskkill
  // (RestartCount=3). Disable first, then kill, then wait so file locks drop.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like ''LMechrevo*'' } | ForEach-Object { Stop-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue; Disable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  TasksDisabledBySetup := True;
  // No /T: every helper is itself an L-Mechrevo.exe (matched by /IM), and a process tree kill would
  // also take down this setup when the app started it (in-app update).
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM L-Mechrevo.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

procedure EnableLMechrevoTasks;
var
  ResultCode: Integer;
begin
  // Undo StopLockedAppProcesses on every exit path (success, failed runtime check, cancel):
  // a disabled LMechrevoCharge task silently stops the boot-time charge limit.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like ''LMechrevo*'' -and $_.State -eq ''Disabled'' } | ForEach-Object { Enable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue | Out-Null }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Answer: Integer;
begin
  // Overlay install: stop the running app and its tasks so files can be replaced, then check the
  // runtime. The previous version is deliberately NOT uninstalled first.
  StopLockedAppProcesses;

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

function ShouldRelaunchAfterSilentInstall: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function NeedRestart(): Boolean;
var
  Flag: Cardinal;
begin
  // Offer a reboot only when Install-Gcu.ps1 recorded a real reason (a driver install that asked
  // for one, or locked official-console components). The value is consumed here so a later
  // install does not ask again. Silent installs still honor /NORESTART.
  Result := False;
  if RegQueryDWordValue(HKLM64, AppRegistryKey, 'RebootRequired', Flag) and (Flag <> 0) then
  begin
    RegDeleteValue(HKLM64, AppRegistryKey, 'RebootRequired');
    Result := True;
  end;
end;

procedure DeinitializeSetup;
begin
  if TasksDisabledBySetup then
    EnableLMechrevoTasks;
end;
