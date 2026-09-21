import { invoke } from "@tauri-apps/api/core"
import {
  EMPTY_HUD_SNAPSHOT,
  mergeOverlayPrefs,
  type OverlayPersistPrefs,
} from "../hud/hudOverlay"
import type {
  FanCurveType,
  HwSnapshot,
  PerformanceMode,
  UpdateDto,
} from "./types"

export const DISARMED_SNAPSHOT: HwSnapshot = {
  ...EMPTY_HUD_SNAPSHOT,
  customProfileOffered: false,
}

export async function hwSnapshot(): Promise<HwSnapshot> {
  return invoke<HwSnapshot>("hw_snapshot")
}

export function setPerformanceMode(mode: PerformanceMode): Promise<void> {
  return invoke("set_performance_mode", { mode })
}

export function setChargeLimit(percent: number): Promise<number> {
  return invoke("set_charge_limit", { percent })
}

export function setChargeFull(): Promise<number> {
  return invoke("set_charge_full")
}

export function setGpuRoute(action: string): Promise<void> {
  return invoke("set_gpu_route", { action })
}

export type LightEffectParams = {
  readonly light?: string
  readonly speed?: string
  readonly color?: string
}

export function setLightEffect(
  channel: string,
  effect: string,
  params?: LightEffectParams,
): Promise<void> {
  return invoke("set_light_effect", {
    channel,
    effect,
    ...(params?.light !== undefined ? { light: params.light } : {}),
    ...(params?.speed !== undefined ? { speed: params.speed } : {}),
    ...(params?.color !== undefined ? { color: params.color } : {}),
  })
}

export function setLightPower(channel: string, on: boolean): Promise<void> {
  return invoke("set_light_power", { channel, on })
}

export function setDisplayHz(hz: string): Promise<void> {
  return invoke("set_display_hz", { hz })
}

export function setAutoRefreshRate(on: boolean): Promise<void> {
  return invoke("set_auto_refresh_rate", { on })
}

export function setBrightness(percent: number): Promise<void> {
  return invoke("set_brightness", { percent })
}

export function setCalibration(mode: string): Promise<void> {
  return invoke("set_calibration", { mode })
}

export function setOverdrive(on: boolean): Promise<void> {
  return invoke("set_overdrive", { on })
}

export function setLocalDimming(on: boolean): Promise<void> {
  return invoke("set_local_dimming", { on })
}

export function setFanCurve(
  name: string,
  ty: FanCurveType,
  duties: readonly number[],
): Promise<void> {
  return invoke("set_fan_curve", { name, ty, duties })
}

export function setFanBoost(on: boolean): Promise<void> {
  return invoke("set_fan_boost", { on })
}

export function setCustomDetail(field: string, value: string): Promise<void> {
  return invoke("set_custom_detail", { field, value })
}

export function setMonitorOff(): Promise<void> {
  return invoke("set_monitor_off")
}

export function setQuickSwitch(key: string, on: boolean): Promise<void> {
  return invoke("set_quick_switch", { key, on })
}

export function setLcPump(index: number): Promise<void> {
  return invoke("set_lc_pump", { index })
}

export function setLcFan(index: number): Promise<void> {
  return invoke("set_lc_fan", { index })
}

export function setLcConnect(): Promise<void> {
  return invoke("set_lc_connect")
}

export function setLcDisconnect(): Promise<void> {
  return invoke("set_lc_disconnect")
}

export function updatesCheck(): Promise<UpdateDto> {
  return invoke("updates_check")
}

export function overlaySet(on: boolean): Promise<void> {
  return invoke("overlay_set", { on })
}

export function diagnosticsExport(): Promise<void> {
  return invoke("diagnostics_export")
}

export function appQuit(): Promise<void> {
  return invoke("app_quit")
}

export function updatesInstall(): Promise<void> {
  return invoke("updates_install")
}

export function updatesOpenPage(): Promise<void> {
  return invoke("updates_open_page")
}

export type OverlayPrefs = OverlayPersistPrefs
export { overlayPrefs, subscribeOverlayPrefs } from "../hud/hudOverlay"

export function overlayUpdate(prefs: OverlayPrefs): Promise<void> {
  mergeOverlayPrefs(prefs)
  return invoke("overlay_update", { prefs })
}

export function setThemeMode(mode: "night" | "day"): Promise<void> {
  return invoke("set_theme_mode", { mode })
}

export function setUiLanguage(code: string): Promise<void> {
  return invoke("set_ui_language", { code })
}

export function setProjectId(id: string): Promise<void> {
  return invoke("set_project_id", { id })
}

export type LightingPolicy = {
  readonly offOnBattery: boolean
  readonly idleSeconds: number
}

export function setLightingPolicy(policy: LightingPolicy): Promise<void> {
  return invoke("set_lighting_policy", {
    offOnBattery: policy.offOnBattery,
    idleSeconds: policy.idleSeconds,
  })
}

export function openCustomModeWindow(): Promise<void> {
  return invoke("open_custom_mode_window")
}

export function closeCustomModeWindow(): Promise<void> {
  return invoke("close_custom_mode_window")
}
