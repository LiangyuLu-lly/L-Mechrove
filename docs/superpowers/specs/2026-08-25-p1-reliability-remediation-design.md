# P1 Reliability Remediation Design

## Goal

Make GCU liquid-cooling controls report success only after a fresh device status confirms the requested pump or fan profile, and restore solution-wide Release builds by excluding generated probe artifacts.

## Evidence

- `MechrevoHw` parses `BT_LC/Status` into `LcPumpControl`, `LcFanControl`, and `LcStatusVersion`.
- `MechrevoService.SwitchLcPump` and `SwitchLcFan` currently return `true` immediately after publishing `LC_PumpCtrl` or `LC_FanCtrl`.
- `Settings` treats that return value as an accepted selection, so a rejected command remains selected in the UI.
- `src/Probe/Probe.csproj` has no exclusion for `artifacts\**`; generated `AssemblyInfo` files under that directory are compiled by SDK default item discovery.

## Scope

1. Add fresh-status confirmation for GCU pump and fan profile changes.
2. Query `BT_LC/Status` after a command has not produced an immediate status update.
3. Treat a fresh but mismatched status as failure and let the existing UI rollback path restore the prior selection.
4. Change liquid-cooling UI copy so it says a profile is confirmed only after service confirmation.
5. Exclude `src/Probe/artifacts/**` from compile, embedded-resource, and content item discovery.
6. Add regression tests for accepted and rejected fresh liquid-cooling state.

## Non-goals

- No direct BLE protocol rewrite.
- No claim that every lighting mode can be confirmed: available GCU status fields only reliably encode pump and fan profile fields for this batch.
- No changes to Windows Firewall, GCU services, AppX installation, scheduled tasks, or process termination.
- No release signing attempt without a supplied signing certificate.
- No version-number change or distributable package generation.

## Design

`MechrevoService` will capture `MechrevoHw.LcStatusVersion` before publishing a control command. It will accept a result only when a later `BT_LC/Status` message both advances that version and matches the requested `LcPumpControl` or `LcFanControl` value.

The command path uses a short initial wait because some GCU versions emit an unsolicited status response. If no fresh response arrives, it requests `GETSTATUS` and waits again. If the first fresh response has an unexpected profile, it performs one status-only query to eliminate an in-flight pre-command response, then evaluates the next fresh response. It never retries the physical profile write.

```text
UI selection
  -> SwitchLcPump / SwitchLcFan
  -> publish control command
  -> fresh BT_LC/Status?
       -> expected profile: return true, UI marks confirmed
       -> other profile: one GETSTATUS confirmation, then return false if still mismatched
       -> no response: GETSTATUS, then return false after bounded retries
```

The service remains the single state-authority boundary. `Settings` must not persist a GCU selection before the service returns confirmed success.

## Error Handling

- Publish exceptions remain logged and return `false`.
- An unavailable or non-controllable liquid-cooling capability returns `false` without publishing.
- A stale status snapshot cannot satisfy a command because its status-version value predates the command. A single in-flight older response arriving after the command is filtered by one additional status-only query before failure is reported.
- No status response ends as a visible failure rather than an incorrect success message.

## Verification

- New tests must first fail against the current publish-only implementation.
- Tests will inject a real `BT_LC/Status` parser update through `MechrevoHw.HandleMessage`; they will not assert a mocked callback in isolation.
- Run the liquid-cooling test class, the whole test project, the main Release build, and the full solution Release build.
- Real water-cooling hardware remains required to prove firmware behavior after the software confirmation boundary is corrected.

## Risk

- Some GCU firmware versions may be slow or omit `LC_PumpCtrl`/`LC_FanCtrl` in status replies. The application will show failure instead of pretending success; the user can retry after a later status refresh.
- A broad retry loop could resend a physical command unintentionally. This design retries only status reads, not the profile write.
- Excluding `artifacts/**` fixes build input selection but retains the files on disk for historical investigation; no evidence is deleted.
