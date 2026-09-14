# GPU Hot Switching and Lighting Recovery Design

**Status:** Proposed implementation specification; architectural direction approved in conversation, pending review of this written specification before code changes.

## Goal

Make GPU mode switching reflect the hardware's real capabilities and current state, rather than assuming every RTX 50-series laptop behaves the same. On supported systems, allow a mixed-to-iGPU transition without a reboot. Preserve a deterministic reboot path when a hot switch cannot complete. Restore the user's saved lighting effect after temporary battery-driven lighting suspension.

This work also fixes the Custom performance button's missing selected state.

## Evidence and Constraints

The supplied official ControlCenter build derives iGPU hot-switch eligibility from these registry values:

| Source | Required value |
| --- | --- |
| `ItemSupport\\IsNvGpu` | enabled |
| `ItemSupport\\iGPUModeOnlySupport` | enabled |
| `ItemSupport\\APVersionCheck` | greater than 23 |
| `MySetting\\GpuConfig\\GpuHotSwapSwitchSupport` | enabled |
| `MySetting\\GpuConfig\\lgpuHotSwapSwitchStatus` | enabled |

The official build transitions iGPU-only mode through the existing GCU `IGPU_ONLY_CONNECT_RB_*` commands, then polls GCU state. It does not establish that the undocumented `GPU_HOTSWAP_ON` and `GPU_HOTSWAP_OFF` commands are safe to send from this application. This implementation will not use those commands.

The current application has a stale-configuration restart rule that can request a reboot based on a saved GPU mode instead of the hardware's current mode. Its generic Mechrevo path also has no active dGPU-process inspection or cleanup implementation. These are separate problems and will be corrected separately.

## Scope

Included:

- Capability detection from the same registry fields used by the official console.
- Manual iGPU hot switching on systems that advertise the capability.
- A visible, confirmed fallback to reboot switching when a hot switch is unavailable or fails.
- An opt-in automatic unplug-to-iGPU path for capable systems.
- Safe dGPU application discovery and user-mediated cleanup before a requested switch.
- Accurate re-read of GPU state after cancel, failure, and success.
- Restoration of saved keyboard effects after a temporary power-policy suspension.
- Custom performance-mode selected-state correction.
- Unit coverage for capability gating, switch policy, process filtering, and lighting recovery policy.

Excluded:

- Treating all RTX 50-series devices as hot-switch capable.
- Sending unverified GCU protocol commands.
- Silently terminating processes, services, session-zero processes, or protected system processes.
- Changing direct-MUX reboot semantics for hardware that does not advertise iGPU hot switching.
- Claims that a code-only test proves the behavior of every physical machine.

## Capability Contract

`MechrevoDeviceCapabilities` will load both the existing `ItemSupport` branch and the matching `MySetting\\GpuConfig` branch for each supported OEM registry root and registry view.

It will expose one computed `GpuHotSwap` capability. It is true only when all five evidence fields above are true. Missing, malformed, or unavailable values produce `false` and keep the existing reboot-required behavior.

This is intentionally more conservative than model-name or GPU-generation detection. A device with an RTX 50-series GPU but no OEM support flag remains reboot-only.

## GPU Switch Policy

The UI will derive a switch plan from a fresh hardware status read, requested target, and `GpuHotSwap` capability. Saved application configuration is not a source of truth for whether a transition can be hot switched.

| Current mode | Requested mode | Advertised hot switch | Result |
| --- | --- | --- | --- |
| Mixed/standard | iGPU only | yes | Preflight, send normal iGPU GCU command, poll and verify |
| Mixed/standard | iGPU only | no | Explain that a reboot is required and offer reboot switch |
| Any | dGPU direct | any | Existing reboot-required path |
| iGPU only | mixed/standard | any | Existing GCU path, verify state |
| Any | auto | supported and enabled | Apply policy after current-state refresh |

The hot-switch attempt will use a bounded poll/retry loop based on the official console's behavior. It must re-read the final GCU status before reporting success. If status reports that switching is currently unavailable, or the timeout expires, the UI updates to the actual state and offers the reboot path instead of leaving a speculative selection visible.

The reboot path will save the requested target and, after user confirmation, send the existing GCU restart command. That command is handled by the GCU service and is expected to restart the system; this application will not also issue a competing Windows restart. If the command cannot be sent or verified, the UI reports the failure and refreshes the visible state from hardware. Canceling the prompt also refreshes the visible state from hardware.

## dGPU Process Safety

Before a manual hot switch to iGPU-only, the application will query the NVIDIA API for processes actively using the internal dGPU. This discovery API is read-only.

When no applications are found, switching proceeds normally. When applications are found, the UI shows the identified applications and presents these choices:

1. Cancel and leave the system unchanged.
2. Close applications and switch. The application first asks each process with a window to close normally, waits briefly, then shows the remaining list before any forced termination.
3. Use reboot switching instead.

Forced termination is only available after the user sees the remaining process list and confirms it in that same operation. It excludes this process, session-zero processes, known system-critical processes, and any process that cannot safely be inspected. Access-denied and close failures are reported as switch blockers, not silently ignored.

The automatic unplug-to-iGPU option is disabled by default. It appears only when `GpuHotSwap` is true. Enabling automatic forced cleanup requires a separate warning that unsaved work in dGPU applications can be lost. Without that extra opt-in, an unplug event with active dGPU applications posts a notification and leaves the mode unchanged.

## UI Behavior

- The Custom performance button uses the same activated visual state as the other performance buttons whenever the visual mode is `PerformanceManual`.
- The GPU mode panel shows hot-switch availability only for advertised-capable devices; unsupported devices retain the existing restart-oriented experience.
- During a switch, controls are disabled and show a progress state. Completion, cancel, failure, and timeout all refresh the displayed hardware state.
- The automatic battery option is a normal checkbox or toggle in the GPU panel, not an implicit behavior change.
- Process cleanup is never a background operation with no visible user action.

## Lighting Recovery Behavior

Temporary lighting suspension caused by battery or idle power policy will be tracked independently of an explicit user choice to turn lighting off. On AC return or wake:

- If the application temporarily suspended keyboard lighting and the user still has keyboard lighting enabled, explicitly restore keyboard power even if cached hardware status still says it is on, then force a restart of the saved effect.
- If the user explicitly disabled keyboard lighting, preserve that disabled state.
- Restore external lighting through its existing saved-settings path.
- Clear the temporary-suspension marker only after the restore attempt completes, so a transient reconnect failure can retry.

This avoids trusting a stale keyboard-power readback after a temporary power-off command, while still preserving an intentionally disabled keyboard effect.

## Tests and Verification

New or expanded unit tests will cover:

- Each `GpuHotSwap` gate, including missing registry values, AP version 23, non-NVIDIA, and disabled status.
- Switch-plan decisions using current hardware mode rather than persisted configuration.
- Custom mode visual selection.
- Process candidate filtering, including current process, session zero, critical names, and inaccessible records.
- Temporary lighting suspension and restore behavior, including preserving a user-disabled state.

Build and test verification will use `-p:GITHUB_ACTIONS=true` and isolated output paths so the user's running L-Mechrevo process is not signaled or overwritten. Hardware-facing validation remains a manual test matrix across both supplied official-console families and at least one hot-switch-capable machine:

| Scenario | Expected result |
| --- | --- |
| Supported mixed to iGPU with no dGPU apps | Completes without reboot and state is re-read |
| Supported mixed to iGPU with dGPU app | User sees app list; cancel leaves mode unchanged |
| Failed or blocked hot switch | Actual state remains visible; reboot fallback offered |
| Unsupported machine | No hot-switch claim; reboot path remains available |
| Automatic unplug, cleanup disabled | Active dGPU app prevents silent switch |
| Battery lighting off then AC restore | Saved animated effect restarts |
| User explicitly turns lighting off | AC restore does not turn it on |

## Risks and Mitigations

| Risk | Failure mode | Mitigation |
| --- | --- | --- |
| Incorrect capability inference | A machine receives an unsupported hot-switch command and becomes stuck in an inconsistent GPU state. | Require every official registry gate, use only established GCU commands, time-bound polling, and retain a visible reboot fallback. |
| Process cleanup loses user work | A dGPU-rendering application has unsaved data when it is terminated. | Discovery is read-only; normal close happens first; force termination requires same-operation confirmation after showing the remaining process list; automatic force cleanup is separately disabled by default. |
| Stale UI state | A canceled restart or failed GCU command leaves the UI claiming a mode that hardware did not enter. | Refresh GCU status on every terminal path and derive visual state from that readback. |
| Lighting restoration violates user intent | A battery-to-AC restore turns on lighting that the user deliberately disabled. | Track temporary power suspension separately from persistent user setting and restore only when the saved keyboard-power setting is enabled. |
| Registry variation across OEM builds | A compatible device stores capability fields under a legacy root or a value is malformed. | Probe the established root aliases and both registry views; malformed or missing data safely evaluates to unsupported. |

## Implementation Boundaries

The implementation will preserve existing direct GPU switching and GCU communications unless a targeted change is required by this specification. It will not change official-console isolation, overclocking, liquid-cooling behavior, or release packaging in this change set.
