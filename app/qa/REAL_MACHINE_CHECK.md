# Real-machine check — slot-4 single-client capture

Two commands replace walking the UI and eyeballing packets against `crates/_golden/mqtt/*.json`.

The cargo tests always drive `Backend` methods against **FakeBroker**. They never open TCP, never construct `Backend::real`, never kill a process, and never touch slot 5 or EC lighting.

Live honouring of those packets still needs the machine. The dump/diff pair only proves **wire shape**.

## Commands

From the repo root, with the same `vcvars64` environment used for `cargo test`:

```
cargo test --manifest-path app/src-tauri/Cargo.toml --test transcript_dump -- --nocapture
cargo test --manifest-path app/src-tauri/Cargo.toml --test transcript_diff -- --nocapture
```

`transcript_dump` writes a sorted `topic<TAB>canonical-json` file to `app/src-tauri/target/slot4_transcript.tsv`. Two walks of the same build must be byte-identical.

`transcript_diff` asserts that transcript contains the golden entry for each walk step. Keys and value types are compared strictly. Temperatures, RPM, and charge level are presence-only.

## Operator steps (live slot 4)

Do these **before** trusting a live console. The cargo tests themselves stay on Fake.

1. Quit the vendor console **normally** from its own UI. Never kill the process, never task-kill. This port has no isolation command.
2. Confirm the vendor UI process is gone (`ControlCenterX` / vendor executable not listed). If it is still running, wait; do not kill it.
3. Start this app so it owns **slot 4** (`UWPClient_4`) alone. Do not start a second client. Do not use slot 5.
4. On the running app, walk the same command surface the dump drives: office mode, custom slot 2, a lighting power change, a Windows personalization switch (dark theme), charge limit 80, overlay toggle, then quit so shutdown publishes `System_OFF`.
5. Run `transcript_dump` then `transcript_diff` (Fake shape oracle). Paste the result below.
6. Do **not** treat a Fake GREEN as proof that the GCU acked, that firmware stored the slot, or that the panel went dark.

Paste:

```
transcript_dump: ...
transcript_diff: ...
slot-4 owner: this app / vendor / unknown
```

## Walk the dump actually performs (Fake)

Through `Backend` methods, not raw publishes:

| Step | Entry point | Expected on the wire |
|---|---|---|
| Handshake | `start` | `handshake_order.json` |
| Office | `set_performance_mode("office")` | `operating_office.json` + `lchwoc_office.json` |
| Custom slot 2 | `set_custom_detail("ProfileIndex","2")` then custom mode | `operating_custom.json` + `lchwoc_custom.json` |
| Lighting power | `set_light_power("lightbar", false)` | `function=SetPower`, `powerstatus` number |
| Windows switch | `set_quick_switch("darktheme", true)` | no MQTT (shell). Touchpad ON is the MQTT stand-in. |
| Charge limit | `set_charge_limit(80)` | EC 0x7B9/0x7D0 only; no MQTT |
| Overlay | `overlay_update` | no MQTT |
| N16 GPU | `set_gpu_route(IGPU_ONLY_ON)` | `SetToWMIEC` string `"OK"` |
| Auto-Hz | `set_auto_refresh_rate(true)` | `gpu_dc_hz_on.json`, `Enable` bool |
| Deep-sleep | `set_quick_switch("deepsleep", true)` | `Secs` JSON string |
| Shutdown | `shutdown` | `System/Control` `System_OFF` |

## Audit rows this can promote

These leave "eyeball the packets" and become **verified (wire shape)** when dump+diff are GREEN:

- Performance mode actions (office / custom) and `ProfileIndex` as a JSON number 0..=3
- Custom slot **send path** (slot 2 on the wire)
- Lighting `SetPower` shape (`function` string, `powerstatus` number)
- N16 GPU per-action `SetToWMIEC`
- Auto-refresh `GPU_DC_HZ` `Enable` bool
- Deep-sleep `Secs` as a JSON string
- Shutdown `System_OFF`
- Handshake `System_ON` among the control publishes

## Audit rows this still cannot verify

Unchanged cheapest falsifiers from `PARITY_AUDIT.md`:

| Claim | Why dump/diff cannot close it |
|---|---|
| GCU honours requested QoS | Fake has no acknowledgement |
| Reconnect survives a dropped session | no live broker |
| Firmware keeps four independent custom slots | send path only; needs write slot 2, read the echo |
| Lights go dark on battery / idle | look at the light bar |
| 息屏 blanks the panel without sleeping | panel blank, machine still awake |
| WMI brightness reaches the panel | photons, not MQTT |
| Charge limit persists across reboot | set 80, reboot, read the level |
| Overlay game-only and z-order over exclusive-fullscreen | run a game |
| HID keyboard path | plug the machine's own keyboard |
| Authenticode on the update package | `signtool`; port checks sha256 only |
| Live LHM telemetry | live numbers stay absent |
| Live CCD advanced-colour vs the panel HDR/ACM switch | turn HDR on/off on the panel |

EC lighting is still not ported. Isolation still never kills a process.
