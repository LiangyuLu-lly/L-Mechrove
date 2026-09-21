import { assertNever } from "../lib/assertNever"
import type { HwSnapshot } from "../lib/types"

export const OVERLAY_MODES = ["light", "default", "full", "complete"] as const
export type OverlayModeName = (typeof OVERLAY_MODES)[number]

export const MIN_SCALE_PERCENT = 35
export const MAX_SCALE_PERCENT = 300
export const SCALE_STEP_PERCENT = 10

export type OverlayExtras = {
  readonly cpuUsage?: number
  readonly gpuUsage?: number
  readonly ramUsedGb?: number
  readonly vramUsedGb?: number
  readonly cpuName?: string
  readonly gpuName?: string
}

export type HudTelemetry = HwSnapshot & OverlayExtras

export type OverlayView = {
  readonly mode?: OverlayModeName
  readonly showTemp?: boolean
  readonly showFans?: boolean
  readonly showPower?: boolean
  readonly showUsage?: boolean
  readonly showRam?: boolean
  readonly showBattery?: boolean
  readonly names?: boolean
}

export type OverlayBlocks = {
  readonly temp: boolean
  readonly fans: boolean
  readonly power: boolean
  readonly usage: boolean
  readonly ram: boolean
  readonly battery: boolean
  readonly names: boolean
}

export type HudLineId = "cpu" | "gpu"

export type HudLine = {
  readonly id: HudLineId
  readonly label: "CPU" | "GPU"
  readonly name?: string
  readonly tempC: string
  readonly rpm?: string
  readonly watt: string
  readonly usage?: string
  readonly usagePct?: number
  readonly mem?: string
  readonly battery?: string
}

type LineSource = {
  readonly id: HudLineId
  readonly label: "CPU" | "GPU"
  readonly temp: number | undefined
  readonly rpm: number | undefined
  readonly watt: number | undefined
  readonly usage: number | undefined
  readonly memGb: number | undefined
  readonly name: string | undefined
  readonly battery: string | undefined
}

export function formatTempC(value: number | undefined): string {
  return value === undefined ? "—" : `${Math.round(value)}C`
}

export function formatRpm(value: number | undefined): string {
  return value === undefined ? "—" : `${Math.round(value)}rpm`
}

export function formatWatt(value: number | undefined): string {
  return value === undefined ? "—" : `${value.toFixed(1)}W`
}

export function formatUsage(value: number | undefined): string {
  return value === undefined ? "—" : `${Math.round(value)}%`
}

export function formatMemGb(value: number | undefined): string {
  return value === undefined ? "—" : `${value.toFixed(1)}GB`
}

export function formatBattery(percent: number | undefined): string {
  return percent === undefined ? "—" : `${Math.round(percent)}%`
}

export function clampScalePercent(raw: number): number {
  if (raw <= MIN_SCALE_PERCENT) {
    return MIN_SCALE_PERCENT
  }
  if (raw >= MAX_SCALE_PERCENT) {
    return MAX_SCALE_PERCENT
  }
  return raw
}

export function nextOverlayMode(mode: OverlayModeName): OverlayModeName {
  switch (mode) {
    case "light":
      return "default"
    case "default":
      return "full"
    case "full":
      return "complete"
    case "complete":
      return "light"
    default:
      return assertNever(mode)
  }
}

export function nextScalePercent(current: number, deltaY: number): number {
  const stepped =
    current + (deltaY < 0 ? SCALE_STEP_PERCENT : -SCALE_STEP_PERCENT)
  return clampScalePercent(stepped)
}

function overlayModePreset(mode: OverlayModeName): OverlayBlocks {
  switch (mode) {
    case "light":
      return {
        temp: true,
        fans: false,
        power: true,
        usage: false,
        ram: false,
        battery: false,
        names: false,
      }
    case "default":
      return {
        temp: true,
        fans: true,
        power: true,
        usage: false,
        ram: false,
        battery: false,
        names: false,
      }
    case "full":
      return {
        temp: true,
        fans: true,
        power: true,
        usage: true,
        ram: false,
        battery: false,
        names: false,
      }
    case "complete":
      return {
        temp: true,
        fans: true,
        power: true,
        usage: true,
        ram: true,
        battery: true,
        names: false,
      }
    default:
      return assertNever(mode)
  }
}

export function overlayBlocks(
  mode: OverlayModeName,
  view: OverlayView,
): OverlayBlocks {
  const preset = overlayModePreset(mode)
  return {
    temp: view.showTemp ?? preset.temp,
    fans: view.showFans ?? preset.fans,
    power: view.showPower ?? preset.power,
    usage: view.showUsage ?? preset.usage,
    ram: view.showRam ?? preset.ram,
    battery: view.showBattery ?? preset.battery,
    names: view.names ?? preset.names,
  }
}

function lineFrom(source: LineSource, blocks: OverlayBlocks): HudLine {
  const usagePct = source.usage === undefined ? 0 : Math.round(source.usage)
  return {
    id: source.id,
    label: source.label,
    tempC: blocks.temp ? formatTempC(source.temp) : "—",
    watt: blocks.power ? formatWatt(source.watt) : "—",
    ...(blocks.fans ? { rpm: formatRpm(source.rpm) } : {}),
    ...(blocks.usage ? { usage: formatUsage(source.usage), usagePct } : {}),
    ...(blocks.ram ? { mem: formatMemGb(source.memGb) } : {}),
    ...(blocks.names && source.name !== undefined && source.name.length > 0
      ? { name: source.name }
      : {}),
    ...(source.battery !== undefined ? { battery: source.battery } : {}),
  }
}

export function hudLinesFromSnapshot(
  snapshot: HudTelemetry,
  view: OverlayView = {},
): readonly HudLine[] {
  const mode = view.mode ?? "default"
  const blocks = overlayBlocks(mode, view)
  const battery = blocks.battery
    ? formatBattery(snapshot.chargePercent)
    : undefined

  return [
    lineFrom(
      {
        id: "cpu",
        label: "CPU",
        temp: snapshot.cpuTempC,
        rpm: snapshot.cpuRpm,
        watt: snapshot.cpuWatt,
        usage: snapshot.cpuUsage,
        memGb: snapshot.ramUsedGb,
        name: snapshot.cpuName,
        battery,
      },
      blocks,
    ),
    lineFrom(
      {
        id: "gpu",
        label: "GPU",
        temp: snapshot.gpuTempC,
        rpm: snapshot.gpuRpm,
        watt: snapshot.gpuWatt,
        usage: snapshot.gpuUsage,
        memGb: snapshot.vramUsedGb,
        name: snapshot.gpuName,
        battery: undefined,
      },
      blocks,
    ),
  ]
}
