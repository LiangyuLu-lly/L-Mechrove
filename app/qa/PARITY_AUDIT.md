# Parity audit — C# console → Rust/Tauri port

Scope: every user-visible surface of the C# console (`src/MechrevoLiteWin`) against the Tauri app (`app/`).
Rule: a row is PASS only with a test name or a measured artifact. Anything a unit test cannot reach is listed
as CANNOT-VERIFY with its cheapest falsifier — never as done.

## Automated gate (this commit)

| Gate | Result |
|---|---|
| `bun test` | 201 pass, 0 fail |
| `cargo test` (vcvars64) | exit 0 |
| `bun run build` | clean; dist carries no `react-scan` / `react-grab` / `unpkg` |
| 420×529 browser | `scrollWidth == clientWidth == 420`, no horizontal overflow, 6 footer keys one row at 46×36 |

## Ported and proven by test

| Surface | Evidence |
|---|---|
| Performance modes: 静音模式→office, 平衡模式→gaming, gated 静音狂暴, 狂暴, 自定义 | `host_modes`, `scenario_s1_office` |
| ProfileIndex on the wire as a JSON number 0..=3 | `host_modes` parametrized |
| Lighting: ItemSupport-only channels, KeyboardType catalogs, per-channel power, light/speed/colour | `host_lighting`, `scenario_s2_g16_lighting` |
| Lighting battery cutoff + idle sleep (app-side; firmware timer never sent) | `host_lighting`, `host_lighting_idle` |
| GPU routes incl. AUTO and the dGPU prelude/tail, N16 per-action `SetToWMIEC` | `host_gpu` |
| Switches: five unconditional Windows keys incl. 息屏, PowerLight brightness, deep-sleep seconds, post-change confirmations | `host_switches` |
| Display: Hz string, auto-refresh gate, calibration-off file name, restart frame | `host_display` |
| Liquid cooling: pump/fan gears, connect family, MAC, water light | `host_lc` |
| Mode extras: EXTREME, profile OSD string, real restore with the curve table name | `host_modes`, `host_mode_detail` |
| Fan curve normalisation matches the vendor algorithm | `host_fan` |
| Charge limit writes only 0x7B9/0x7D0 | `scenario_s3_charge_limit` |
| Battery protection publishes; full-charge exists | `host_battery` |
| Update: 2-key check DTO, sha256-gated install, open page, notes/progress | `host_updates` |
| Autostart task, shell personalisation, vendor isolation (no process kill) | `host_startup`, `host_shell`, `host_isolation` |
| Real snapshot computes the capability surface; inbound MQTT parsed; reconnect; shutdown notice | `host_snapshot`, `host_item_support`, `host_mqtt_inbound`, `host_reconnect`, `host_real_transport` |
| Dev fixture is opt-in; empty stays the fail-closed default | `host_fake_dev` |
| UI: telemetry row, GPU labels, screen gating, switch checkboxes, lighting default-open, custom window, donate, update dialog | the section tests + the 420px measurement |

## Cannot be verified without the machine

| Claim | Cheapest falsifier |
|---|---|
| The GCU honours the requested QoS | one publish, watch the acknowledgement |
| Reconnect survives a dropped session | stop the vendor service, confirm a re-handshake and not a second client |
| The firmware keeps four independent custom slots | write slot 2, read the echo |
| Lights actually go dark on battery / idle | look at the light bar with the option on |
| 息屏 blanks the panel without sleeping | panel blank, machine still awake |
| WMI brightness reaches the panel | move the slider, watch the panel |
| The charge limit persists across a reboot | set 80, reboot, read the level |
| Overlay game-only and z-order over an exclusive-fullscreen game | run one |
| The HID keyboard path on a device that has one | plug the machine's own keyboard |
| Authenticode on the update package | `signtool`; the port checks sha256 only |
| Live sensor telemetry (LHM) | inject a sensor seam; live numbers stay absent |
| Live CCD advanced-colour matches the panel HDR/ACM switch | turn HDR on, calibration refused; HDR off, calibration proceeds; ACM-on also refused (`IsAdvancedColorEnabled`). Unit tests cover the three branches via an injected probe and never open live display config. |

## Known deviations, deliberate

- No EC lighting. Charge limit is the only EC write, restricted to 0x7B9/0x7D0.
- The vendor's process-termination paths (its own kill + 2s re-kill guard and the elevated GPU-app killer) are
  not ported; isolation never kills a process.
- The 24-character manual-model override is replaced by the served-machine rule.
- The GPU overclock elevation row is not shown: an MQTT-only host has no elevation to request.
- `hw_display.rs` still reports unavailable on eleven arms (non-Windows fallbacks and the sub-commands whose
  device handle is not implemented). This file was NOT audited arm-by-arm; it is the first item for the next
  session, not an accepted debt.
- Usage telemetry is off (the vendor ships it on by default).

## Resolved by the display-defect session

The display audit's three open items are closed by test. C# HDR is **not** an MQTT field.

1. **HDR guard on the real path — resolved.** C# reads advanced colour from `ScreenCCD.GetHDRStatus`
   (`Display/ScreenCCD.cs:11`) via `MechrevoService.GetAdvancedColorState` / `IsHdrEnabled`
   (`Hardware/MechrevoService.cs:956-970`), injected as `_readHdrEnabled = IsAdvancedColorEnabled`
   (`MechrevoService.cs:118`) and applied in `SetColorCalibration` (`MechrevoService.cs:752`).
   There is no MQTT topic or payload field. The Real arm now calls the same user32 CCD APIs
   (`GetDisplayConfigBufferSizes` / `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` packet
   types 15 then 9) behind a `cfg(windows)` seam with an injected probe. Refuse when the query
   reports on; proceed when it reports off; fail-closed with an unavailable reason when the query
   cannot run (non-Windows, API failure, or a test binary with no probe). Evidence:
   `real_calibration_refuses_when_advanced_color_on`,
   `real_calibration_proceeds_when_advanced_color_off`,
   `real_calibration_refuses_when_advanced_color_query_unavailable`,
   `real_inbound_does_not_invent_mqtt_hdr_and_calibration_is_fail_closed`. Fake still uses
   `hdr_on` (`hdr_on_blocks_color_calibration_without_publish`). Live CCD vs the panel switch is
   not a unit-test claim — see the cannot-verify table.
2. **`set_calibration` OFF sends the current mode's file name.** C# `SetColorCalibration` off-path
   publishes `ColorCalibrationFileName(currentMode)` (`MechrevoService.cs:778-779, 986-992`).
   Current mode is `MechrevoHw.ColorCalibrationMode` from Setting/Status FirstField
   `CurrentColorCalibration` / `ColorCalibrationMode` / `ColorCalibration` (`MechrevoHw.cs:1938-1944`),
   else the last ON_* write. Evidence: `set_calibration_off_sends_file_name_of_current_mode`,
   `set_calibration_off_uses_inbound_color_calibration_mode`,
   `apply_inbound_setting_status_sets_color_calibration_mode`. Falsifier: switch to sRGB and back,
   compare the panel ICC name.
3. **CI brightness seam is locked.** Integration tests compile the library without `cfg(test)`.
   `apply_real_brightness` now returns `RealUnavailable` when no injected sink is installed and the
   process is a rustc test binary under `deps`. Evidence:
   `real_brightness_refuses_unless_injected_sink_is_installed` (all platforms) plus the existing
   serialized `BRIGHTNESS_SEAM` test `real_set_brightness_runs_wmi_on_sta_thread_not_tokio_worker`.
   Falsifier: WMI brightness still needs a look at the panel.

