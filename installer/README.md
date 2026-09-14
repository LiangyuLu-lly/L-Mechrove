# L-Mechrevo installer (Inno Setup)

Per-machine Windows installer for L-Mechrevo, built with Inno Setup 6.7.x. It ships the
self-contained single-file app plus all four vendor GCU payload trees and installs the one
that matches the machine's GPU generation.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
```

What the build script does:

1. Reads `<Version>` / `<AssemblyVersion>` from `src\MechrevoLiteWin\MechrevoLite.csproj`
   (currently `0.289.0-beta13`) and derives the release label (`beta13`).
2. Picks the publish directory: `dist\<label>\` if it holds `L-Mechrevo.exe`, otherwise the
   newest `dist\*` directory that does. Override with `-AppSourceDir <dir>`.
3. Validates the four shipped documents and the four GCU payload trees, then records their
   file counts and sizes.
4. Ensures an `ISCC.exe` compiler exists. If none is found it downloads the Inno Setup 6.7.3
   installer, unpacks it with `innounp` into `%TEMP%\ulw\tools\innosetup\`, and fetches the
   official `ChineseSimplified.isl`. No system-wide Inno Setup install is performed.
5. Compiles `installer\L-Mechrevo.iss` and writes the installer, compile log, hash and payload
   accounting into `artifacts\run4-installer\`.

Useful switches: `-OutputDir <dir>`, `-IsccPath <ISCC.exe>`, `-ProvisionCompiler`, `-AppSourceDir <dir>`.

The `.iss` can also be compiled directly when `ISCC.exe` is on `PATH`; its `#ifndef` defaults
target `dist\beta13` and the current version. `Build-Installer.ps1` is the supported path
because it injects the real version and source directory as `/D` defines.

## What it installs where

| Destination | Contents |
|---|---|
| `{autopf}\L-Mechrevo` (`%ProgramFiles%\L-Mechrevo`) | `L-Mechrevo.exe` (self-contained, no .NET download), `LICENSE.txt`, `THIRD_PARTY_NOTICES.txt`, `更新日志.txt`, `用前必看.txt` |
| `{app}\GCU` | `Install-Gcu.ps1`, `Uninstall-Gcu.ps1`, `Select-GcuPayload.ps1` |
| `{app}\GCU\payload\50` | 50-series payload from `release\GCU-only` (276 files, 85,663,993 B) |
| `{app}\GCU\payload\40-51751` | 40-series `AiStoneService` payload from `release\GCU-40-51751` (75 files, 56,136,429 B) |
| `{app}\GCU\payload\40-51749` | 40-series `UniwillService` payload from `release\GCU-40-51749` (73 files, 39,266,036 B) |
| `{app}\GCU\payload\common` | shared `UWACPIDriver` from `release\GCU-common` (4 files, 60,019 B) |
| `{app}\GCU\<ServiceDir>` | the selected payload copied here at install time (`AiStoneService` or `UniwillService`) |
| `{app}\GCU\UWACPIDriver` | the driver copied here at install time |
| `%ProgramData%\L-Mechrevo\logs` | `gcu-install-*.log` / `gcu-uninstall-*.log` |

Shortcuts: Start Menu group `L-Mechrevo` (app, 使用前必看, 更新日志, 开源许可, Uninstall) and an
optional desktop icon (unchecked task). The uninstaller is registered in Add/Remove Programs.

All four GCU payload trees are bundled. The installer's GCU step selects exactly one at install
time, so every bundled payload is reachable and none is silently dropped.

## GCU selection rule

`installer\Select-GcuPayload.ps1` maps hardware to a payload. Install-Gcu.ps1 invokes it and
copies the result. Signals, strongest first:

| Signal | 50-series | 40-series |
|---|---|---|
| GPU marketing name (`Win32_VideoController` / `Win32_PnPEntity`) | `RTX 50[5-9]x` | `RTX 40[5-9]x` |
| NVIDIA PCI device id high byte | `0x2B/0x2C/0x2D/0x2E/0x2F` (Blackwell) | `0x26/0x27/0x28` (Ada) |
| `HKLM\SOFTWARE\OEM\...\ItemSupport` `BIOS_PROJECT_ID` | `PH6*` | `PH4*` |

Decision: any 50-series evidence -> `50` (`release\GCU-only`, `AiStoneService`). Otherwise ->
`40-51751`. Unknown hardware also falls back to `40-51751`.

**40-series sub-variant:** no machine signal in this repo distinguishes the vendor's
5.17.49.19 (`UniwillService`) build from the newer 5.17.51.34 (`AiStoneService`) build for the
same 40-series hardware; they are two console versions, and `AiStoneService` matches the
50-series naming. So 40-series defaults to the newer `40-51751` / `AiStoneService` payload.
`40-51749` stays bundled and selectable via the override below.

Verify the rule on any machine:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Select-GcuPayload.ps1 -SelfTest
```

The GCU step (`Install-Gcu.ps1`) copies the payload to `%ProgramFiles%\L-Mechrevo\GCU`, verifies
Authenticode on `GCUBridge.exe`, `MyControlCenter\GCUService.exe` and `UWACPIDriver.sys`, runs
`pnputil /add-driver ... /install`, registers + starts the `GCUBridge` service, and adds the
inbound block rule `L-Mechrevo - Block remote GCU MQTT` (TCP 13688). It is idempotent: an existing
healthy install is detected and only re-verified; otherwise files are repaired and the service
re-registered. `Uninstall-Gcu.ps1` stops/deletes the service, removes the firewall rule and
(best effort) removes the staged `uwacpidriver.inf` from the driver store.

## Silent install / uninstall

```powershell
# silent install (per-machine; triggers UAC)
L-Mechrevo-beta13-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# silent install with the 40-series override
L-Mechrevo-beta13-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /GCUVARIANT=40-51749

# silent uninstall
"%ProgramFiles%\L-Mechrevo\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Inno flags: `/SILENT` (no prompts, shows progress), `/VERYSILENT` (no UI), `/SUPPRESSMSGBOXES`,
`/NORESTART`, `/DIR="..."`, `/LOG="..."`, `/LANG=chinesesimplified|english`. The GCU step runs
hidden and never prompts; its log is under `%ProgramData%\L-Mechrevo\logs`.

## Notes and limitations

- No .NET runtime download and no internet access are required: the app is published
  self-contained single-file and every GCU payload is bundled.
- Inno Setup produces an EXE; it does not emit MSI. Use `/VERYSILENT` for unattended deployment.
- Compression is `lzma2/max` with solid compression; total bundled source is ~401 MB
  (app 220,082,336 B + GCU 181,126,477 B + docs/scripts). The beta13 installer is ~123 MB.
  Pass `/DCompression=lzma2/ultra64` to ISCC for a smaller/faster-to-ship build at the cost of
  compile time.
- The published `L-Mechrevo.exe` staged here is unsigned. Sign the app and/or the installer with
  the project certificate before distribution. The bundled vendor GCU binaries and the Microsoft
  driver are already Authenticode-signed; redistribution terms are in `release\GCU-only\NOTICE.txt`.
- Keep `installer\L-Mechrevo.iss` UTF-8 **with BOM** (the build script enforces this) so the
  Chinese file names compile correctly.
