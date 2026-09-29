 # L-Mechrevo installer (Inno Setup)

Per-machine Windows installer for L-Mechrevo, built with Inno Setup 6.7.x. It ships the
**framework-dependent** app plus exactly one vendor GCU payload tree (`release\GCU-only`, the
newest payload, which serves 30/40/50-series machines; see "Single payload (N6)" below). The app
needs the **.NET Desktop Runtime 10 (x64)**; the installer detects it, can download/install it,
and falls back to the browser (see ".NET Desktop Runtime requirement" below).

## Build

The installer consumes a **framework-dependent** publish under `dist\<label>\`. Produce it with:

```powershell
dotnet publish src\MechrevoLiteWin\MechrevoLite.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=false -p:PublishReadyToRun=false -o dist\beta20
```

then build the package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
```

What the build script does:

1. Reads `<Version>` / `<AssemblyVersion>` from `src\MechrevoLiteWin\MechrevoLite.csproj`
   (currently `0.289.0-beta20`) and derives the release label (`beta20`).
2. Picks the publish directory: `dist\<label>\` if it holds `L-Mechrevo.exe`, otherwise the
   newest `dist\*` directory that does. Override with `-AppSourceDir <dir>`.
3. **Asserts the app source is framework-dependent** (`L-Mechrevo.dll` +
   `L-Mechrevo.runtimeconfig.json` present) so a self-contained/single-file publish cannot
   silently slip back into the package; then validates the four shipped documents and the single
   GCU payload tree (`release\GCU-only`), recording file counts and sizes.
4. Ensures an `ISCC.exe` compiler exists. If none is found it downloads the Inno Setup 6.7.3
   installer, unpacks it with `innounp` into `%TEMP%\ulw\tools\innosetup\`, and fetches the
   official `ChineseSimplified.isl`. No system-wide Inno Setup install is performed.
5. Compiles `installer\L-Mechrevo.iss` and writes the installer, compile log, hash and payload
   accounting into `artifacts\run4-installer\` (or `-OutputDir`).

Useful switches: `-OutputDir <dir>`, `-IsccPath <ISCC.exe>`, `-ProvisionCompiler`, `-AppSourceDir <dir>`.

The `.iss` can also be compiled directly when `ISCC.exe` is on `PATH`; its `#ifndef` defaults
target `dist\beta20` and the current version. `Build-Installer.ps1` is the supported path
because it injects the real version and source directory as `/D` defines.

## .NET Desktop Runtime requirement

The packaged app is **framework-dependent** (`SelfContained=false`), so no .NET runtime DLLs are
bundled. Before any file is copied, `[Code]`'s `PrepareToInstall` checks the runtime's own
registry manifest:

```
HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App  ->  a "10.x" subkey
```

A registry read is used instead of `dotnet --list-runtimes` because the installer must not assume
`dotnet` is on `PATH`; `HKLM64` is used because a 32-bit setup reading plain `HKLM` would see
`WOW6432Node` and always conclude "missing".

If the runtime is missing:

| Mode | Behaviour |
|---|---|
| Interactive | Prompts; on **Yes** downloads the pinned Microsoft installer (`DownloadTemporaryFile` with a SHA-256), installs it silently (`/install /quiet /norestart`), then **re-checks** before continuing. On download/install failure, or on **No**, it opens `https://dotnet.microsoft.com/download/dotnet/10.0` and aborts. |
| `/SILENT` / `/VERYSILENT` | Attempts the download+install automatically; on failure it **aborts non-zero** instead of opening a browser. |

No offline fallback exists: a runtime installer is deliberately **not** bundled.

**Measured app payload (win-x64, Release):** self-contained single-file `219,601,876 B` →
framework-dependent `34,725,107 B` (67 files). `PublishReadyToRun` was measured at
`42,781,915 B` (adds ~8 MB for startup speed) and is **off**; `PublishSingleFile` is **off** for
the framework-dependent build (Inno already ships a folder, and FD+single-file is rejected with
`NETSDK1151` because the referenced `Probe.exe` is self-contained).

## What it installs where

| Destination | Contents |
|---|---|
| `{autopf}\L-Mechrevo` (`%ProgramFiles%\L-Mechrevo`) | the whole framework-dependent publish folder (`L-Mechrevo.exe`, `L-Mechrevo.dll`, `*.deps.json`, `*.runtimeconfig.json`, dependency DLLs; `*.pdb` excluded), `LICENSE.txt`, `THIRD_PARTY_NOTICES.txt`, `更新日志.txt`, `用前必看.txt` |
| `{app}\GCU` | `Install-Gcu.ps1`, `Uninstall-Gcu.ps1`, `Select-GcuPayload.ps1` |
| `{app}\GCU\payload\50` | the newest payload from `release\GCU-only` (276 files, 85,663,993 B) |
| `{app}\GCU\<ServiceDir>` | the payload copied here at install time (`AiStoneService`) |
| `{app}\GCU\UWACPIDriver` | the driver copied here at install time (from `GCU-only\UWACPIDriver`) |
| `%ProgramData%\L-Mechrevo\logs` | `gcu-install-*.log` / `gcu-uninstall-*.log` |

Shortcuts: Start Menu group `L-Mechrevo` (app, 使用前必看, 更新日志, 开源许可, Uninstall) and an
optional desktop icon (unchecked task). The uninstaller is registered in Add/Remove Programs.

**Exactly one GCU payload tree is bundled** (N6, owner decision A): `release\GCU-only`, the newest
payload and the superset. It serves 30/40/50, carries its own `UWACPIDriver`, and its
`UserFanTables` holds all 24 per-model chassis dirs + the 23 flat files (the retired 40-series
payloads carry zero per-model dirs). `release\GCU-common` is **not** staged: it is byte-identical to
`GCU-only\UWACPIDriver`, so staging it would only duplicate the driver. The 40-series trees stay on
disk untouched as the material for the visible fallback.

## Privileges are acquired once, at install time (N5)

The app is a **permanently-elevated** app by owner decision, and the installer is the single place
elevation is obtained. `Install-Gcu.ps1` (run by the elevated installer) therefore:

| Artefact | What the installer does |
|---|---|
| Autostart task `LMechrevo_<SID>` | `Register-ScheduledTask -RunLevel Highest -Force`, action = the app exe with **no arguments**, LogonTrigger + ConsoleConnect trigger. Created before the app ever runs, so the app never needs to elevate to create it. |
| Install dir `{app}` and `{app}\GCU` | `Modify` for the installing user only (read/write/delete for its own files). Not a broad principal, not the widest right. |
| Config/log dir `%AppData%\MechrevoLite` | same `Modify` grant, so config and log writes cannot fail on permissions. |
| `%SystemRoot%\System32\drivers\UWACPIDriver.sys` | `ReadAndExecute` for the installing user, making the `\\.\ACPIDriver` access explicit and idempotent. Running elevated already covers it; no EC/firmware write is added. |

`Uninstall-Gcu.ps1` removes the task (`Unregister-ScheduledTask`) before anything else, so no
boot-time elevation entry point survives an uninstall. The app-side path in `Helpers\Startup.cs` is a
**no-op when the task already exists** and only repairs a missing task as a degraded fallback,
surfacing a tray balloon if it cannot.

## GCU selection rule

`installer\Select-GcuPayload.ps1` is a **single-payload resolver**: it detects and logs the dGPU
generation and always returns `release\GCU-only`. Signals (axis 2 only):

| Signal | 30-series | 40-series | 50-series |
|---|---|---|---|
| GPU marketing name (`Win32_VideoController` / `Win32_PnPEntity`) | `RTX 30[5-9]x` | `RTX 40[5-9]x` | `RTX 50[5-9]x` |
| NVIDIA PCI device id high byte | `0x22/0x24/0x25` (Ampere) | `0x26/0x27/0x28` (Ada) | `0x2B/0x2C/0x2D/0x2E/0x2F` (Blackwell) |

Decision: every machine -> `release\GCU-only` (`AiStoneService`). Detection is logged, not a
gate: an **undeterminable** generation (for example GTX 10 / RTX 20 or iGPU-only) still installs
the shipped payload with the reason `installing the shipped GCU payload (no generation selection)`,
and it **never falls back** to a 40-series payload. The post-install self-check below is the gate.
`BIOS_PROJECT_ID` is deliberately **not** consulted: axis 1 (platform code) must not decide axis 2
(dGPU generation).

**Retired:** `/GCUVARIANT=` no longer has any effect - there is no second payload to select. This is
a user-visible contract change.

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
L-Mechrevo-beta20-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# /GCUVARIANT= is retired: only one payload ships, so the flag has no effect.

# silent uninstall
"%ProgramFiles%\L-Mechrevo\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Inno flags: `/SILENT` (no prompts, shows progress), `/VERYSILENT` (no UI), `/SUPPRESSMSGBOXES`,
`/NORESTART`, `/DIR="..."`, `/LOG="..."`, `/LANG=chinesesimplified|english`. The GCU step runs
hidden and never prompts; its log is under `%ProgramData%\L-Mechrevo\logs`.

## Single payload (N6) and the self-check that replaces G0

The installer ships only `release\GCU-only` (the newest `AiStoneService` payload) and serves every
supported dGPU generation (30/40/50) with it. The old **G0** gate is **superseded** (owner): the
newest GCU is backward compatible to 30-series, the vendor ships one GCU/console for all 24 platform
codes, and `release\GCU-only\...\UserFanTables` carries all 24 per-model chassis dirs + the 23 flat
files while the 40-series payloads carry zero per-model dirs - so the newest payload is the superset.
Owner-side 30/40 real-machine testing is now **confirmatory**, not a precondition.

Build (no switch needed - single payload is the only mode):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
```

- The `[Files]` list stages only `release\GCU-only\*`; `Build-Installer.ps1` validates that tree and
  **hard-fails the build** if it is missing.
- `Select-GcuPayload.ps1` decides the dGPU generation from the GPU marketing name or the NVIDIA PCI
  device-id high byte only. `BIOS_PROJECT_ID` is axis 1 (platform code) and is never consulted. An
  undeterminable generation is logged and still installs the shipped payload; it never falls back.
- **Breaking, user-visible:** `/GCUVARIANT=` no longer has any effect.

### The safety net: post-install self-check + visible fallback

`Install-Gcu.ps1` runs a four-invariant self-check after install (`single-service`,
`single-13688-owner`, `item-support`, `service-ready`). On failure it writes a `failed` status file,
logs `FATAL: post-install verification failed` with the failing check, and exits non-zero.

It then prints `FALLBACK:` guidance naming the exact payload to fetch (`release\GCU-40-51751` or
`release\GCU-40-51749`) and where it lives. **Honest limitation:** these fallback trees are not
hosted on the update server (`stats.l-mechrevo.cn` only serves the app installer), so there is no
one-click cloud download - the fallback is "fetch this tree from the source repo and install it by
hand", and the message says so.

## Notes and limitations

- The app is framework-dependent, so the .NET Desktop Runtime 10 (x64) is required; the installer
  checks for it and can download it (see ".NET Desktop Runtime requirement" above). No runtime
  installer is bundled. The GCU payload is bundled, so the GCU step itself needs no network.
- Inno Setup produces an EXE; it does not emit MSI. Use `/VERYSILENT` for unattended deployment.
- Compression is `lzma2/max` with solid compression; total bundled source is ~118 MB
  (beta20: app 32,553,915 B in 28 files + GCU 85,663,993 B + docs/scripts), down from ~216 MB when
  all four GCU trees were bundled and ~401 MB when the app was self-contained. The installer size
  is measured by the build, not assumed (beta20: 48,597,058 B, see `payload-staging.txt`).
  Pass `/DCompression=lzma2/ultra64` to ISCC for a smaller/faster-to-ship build at the cost of
  compile time.
- The published `L-Mechrevo.exe` staged here is unsigned. Sign the app and/or the installer with
  the project certificate before distribution. The bundled vendor GCU binaries and the Microsoft
  driver are already Authenticode-signed; redistribution terms are in `release\GCU-only\NOTICE.txt`.
- Keep `installer\L-Mechrevo.iss` UTF-8 **with BOM** (the build script enforces this) so the
  Chinese file names compile correctly.
