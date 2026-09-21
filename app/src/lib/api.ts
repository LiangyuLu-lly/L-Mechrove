import { invoke } from "@tauri-apps/api/core"
import type {
  FanCurveType,
  HwSnapshot,
  PerformanceMode,
  UpdateDto,
} from "./types"

export const VITE_FALLBACK_SNAPSHOT: HwSnapshot = {
  mqtt: "Disconnected",
  lighting: {
    keyboard: true,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 100,
  gpuActions: [],
  writeAllowed: true,
  hzList: ["60", "165"],
  offeredSwitches: ["touchpad"],
  liquidCooling: false,
  hdrOn: false,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
}

export async function hwSnapshot(): Promise<HwSnapshot> {
  try {
    return await invoke<HwSnapshot>("hw_snapshot")
  } catch (error) {
    if (error instanceof Error) {
      return VITE_FALLBACK_SNAPSHOT
    }
    throw error
  }
}

export function setPerformanceMode(mode: PerformanceMode): Promise<void> {
  return invoke("set_performance_mode", { mode })
}

export function setChargeLimit(percent: number): Promise<number> {
  return invoke("set_charge_limit", { percent })
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

export function setOfficialIsolation(on: boolean): Promise<void> {
  return invoke("set_official_isolation", { isolate: on })
}
