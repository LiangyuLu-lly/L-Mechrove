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
