import { assertNever } from "../lib/assertNever"
import type { OverlayModeName, HudTelemetry } from "./hudTelemetry"

export const EMPTY_HUD_SNAPSHOT: HudTelemetry = {
  mqtt: "Disconnected",
  lighting: {
    keyboard: false,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 0,
  gpuActions: [],
  writeAllowed: false,
  hzList: [],
  offeredSwitches: [],
  liquidCooling: false,
  hdrOn: false,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
  colorCalibration: false,
  keyboardHidUnavailable: false,
  lightingOffOnBattery: false,
  lightingIdleSeconds: 0,
  modelReason: "",
  projectId: "",
  ocRequiresElevation: false,
  themeMode: "night",
  releaseLabel: "",
  batteryHealth: "",
  chargeStatus: "",
  chargeFullOffered: true,
  overdrive: false,
  localDimming: false,
  customProfileOffered: true,
  lcConnection: "none",
  fanCurveTableName: "M4T1",
  updateAvailable: false,
}

export const HUD_BLOCK_TOGGLES = [
  { key: "showTemp", label: "温度" },
  { key: "showFans", label: "风扇" },
  { key: "showPower", label: "功耗" },
  { key: "showUsage", label: "占用" },
  { key: "showRam", label: "内存" },
  { key: "showBattery", label: "电池" },
  { key: "names", label: "名称" },
] as const

export type HudBlockKey = (typeof HUD_BLOCK_TOGGLES)[number]["key"]

export type OverlayHostGate = {
  readonly gameOnly: boolean
  readonly is_game: boolean | undefined
  readonly displayOff: boolean
  readonly display_off: boolean | undefined
}

export type OverlayPersistPrefs = {
  readonly mode?: OverlayModeName
  readonly scalePercent?: number
  readonly gameOnly?: boolean
  readonly displayOff?: boolean
  readonly showTemp?: boolean
  readonly showFans?: boolean
  readonly showPower?: boolean
  readonly showUsage?: boolean
  readonly showRam?: boolean
  readonly showBattery?: boolean
  readonly names?: boolean
}

const OVERLAY_PREFS_KEY = "lmechrevo.overlay.prefs"
const OVERLAY_BOOL_KEYS = [
  "gameOnly",
  "displayOff",
  "showTemp",
  "showFans",
  "showPower",
  "showUsage",
  "showRam",
  "showBattery",
  "names",
] as const

function overlayModeFromUnknown(value: unknown): OverlayModeName | undefined {
  if (
    value === "light" ||
    value === "default" ||
    value === "full" ||
    value === "complete"
  ) {
    return value
  }
  return undefined
}

function overlayPrefsFromUnknown(value: unknown): OverlayPersistPrefs {
  if (typeof value !== "object" || value === null) {
    return {}
  }
  const rec = Object.fromEntries(Object.entries(value))
  const mode = overlayModeFromUnknown(rec["mode"])
  const scale = rec["scalePercent"]
  const prefs: OverlayPersistPrefs = {
    ...(mode !== undefined ? { mode } : {}),
    ...(typeof scale === "number" && Number.isFinite(scale)
      ? { scalePercent: scale }
      : {}),
  }
  for (const key of OVERLAY_BOOL_KEYS) {
    const flag = rec[key]
    if (typeof flag === "boolean") {
      Object.assign(prefs, { [key]: flag })
    }
  }
  return prefs
}

function withOverlayStorage(run: () => void): void {
  try {
    run()
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

export function overlayPrefs(): OverlayPersistPrefs {
  try {
    const raw = window.localStorage.getItem(OVERLAY_PREFS_KEY)
    if (raw === null) {
      return {}
    }
    const parsed: unknown = JSON.parse(raw)
    return overlayPrefsFromUnknown(parsed)
  } catch (error) {
    if (error instanceof Error) {
      return {}
    }
    throw error
  }
}

export function subscribeOverlayPrefs(listener: () => void): () => void {
  const onStorage = (event: StorageEvent): void => {
    if (event.key === OVERLAY_PREFS_KEY) {
      listener()
    }
  }
  window.addEventListener("storage", onStorage)
  return () => window.removeEventListener("storage", onStorage)
}

export function mergeOverlayPrefs(prefs: OverlayPersistPrefs): void {
  withOverlayStorage(() => {
    window.localStorage.setItem(
      OVERLAY_PREFS_KEY,
      JSON.stringify({ ...overlayPrefs(), ...prefs }),
    )
  })
}

export function overlayShouldShow(gate: OverlayHostGate): boolean {
  if (gate.displayOff && gate.display_off === true) {
    return false
  }
  if (gate.gameOnly && gate.is_game === false) {
    return false
  }
  return true
}

export function persistPrefsForBlock(
  key: HudBlockKey,
  on: boolean,
): OverlayPersistPrefs {
  switch (key) {
    case "showTemp":
      return { showTemp: on }
    case "showFans":
      return { showFans: on }
    case "showPower":
      return { showPower: on }
    case "showUsage":
      return { showUsage: on }
    case "showRam":
      return { showRam: on }
    case "showBattery":
      return { showBattery: on }
    case "names":
      return { names: on }
    default:
      return assertNever(key)
  }
}
