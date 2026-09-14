# GPU Overclock Physical Application Design

## Goal

Make custom GPU core and memory offsets report success only after NVIDIA's
driver reports that the requested offsets are active. A GCU MQTT status echo
may persist profile metadata but is not evidence that an offset was applied.

## Evidence

On the RTX 5080 Laptop GPU, the application log recorded
`NVAPI_INVALID_USER_PRIVILEGE` for the direct write, followed by GCU status
echoes for `GpuCoreClockOffsetOC=500`. A later NVAPI capability read reported
`current=0`. The current `GpuOverclockFallbackTests` encodes this false success
as an expected result.

## Scope

- Cover NVIDIA core and memory offsets from the custom profile page.
- Keep the main process `asInvoker` for normal startup and tray behavior.
- Use a per-user, elevated helper only when a direct write requires elevation.
- Require driver readback for every successful non-zero or reset offset.
- Keep GCU messages only for OEM profile persistence and capability telemetry.
- Bound requests by the actual driver range; core remains capped at +/-500 MHz
  and memory uses the driver-reported range rather than the current artificial
  +/-500 MHz limit.

## Architecture

```text
CustomModeForm
  -> MechrevoService.SetCustomDetail
     -> NVIDIA direct write + NVAPI readback
     -> if access denied: elevated helper + NVAPI readback
     -> optional GCU profile persistence
     -> success only when driver offset == requested offset
```

The elevated helper runs the same executable with a private `--gpu-oc-helper`
action before normal UI startup. It owns no windows, tray icon, MQTT connection,
or single-instance state. The normal process exchanges a bounded request and a
result over a named pipe restricted with `PipeOptions.CurrentUserOnly`. The
helper accepts repeated requests for the running application session and exits
after an idle timeout.

## Interfaces

`GpuOverclockApplyRequest` contains nullable core and memory offsets plus an
optional enabled state. `GpuOverclockApplyResult` contains `Success`, the
driver-read core and memory offsets, and a failure description. The helper must
reject malformed values and values outside the NVAPI range.

`MechrevoHw.ApplyDirectGpuOverclockAsync` first uses the existing in-process
`IGpuOverclockControl`; if its write capability is denied, it delegates to the
elevated helper. It always refreshes and returns driver readback, not cached GCU
state.

`MechrevoService.SetCustomDetail` may publish GCU fields for profile storage,
but GPU-field confirmation is based solely on the driver result. It must return
false and retain the actual readback when elevation is declined or the driver
rejects the request.

## Error Handling

- UAC cancellation: show an unapplied status and preserve readback values.
- Driver write rejection: return the NVAPI failure reason; never fall back to a
  GCU echo as success.
- Helper timeout or malformed response: return false, log the failure, and do
  not alter saved direct-profile values.
- No compatible NVIDIA driver range: disable the relevant control and explain
  that the hardware/driver does not expose a writable offset.

## Risks

- Some OEM driver builds reject writes even from an elevated process. Mitigation:
  the helper returns the driver error and the UI never claims success.
- An elevated helper must not become an unrestricted local privilege bridge.
  Mitigation: use a random pipe name, `CurrentUserOnly`, bounded integer
  messages, actual driver-range validation, and an idle shutdown.
- GCU profile persistence might itself reset an applied offset. Mitigation:
  write GCU metadata before the final direct write, then verify the final NVAPI
  readback after it.

## Verification

- Replace the existing false-success fallback test with a failure assertion.
- Add tests proving GCU status echo does not satisfy GPU-field confirmation.
- Add tests proving helper-confirmed driver readback satisfies confirmation.
- Run the full test suite and strict analyzer build.
- On a supported machine, manually verify the log contains requested and actual
  NVAPI offsets after applying +500 core and the selected memory offset.
