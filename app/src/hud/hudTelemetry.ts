import type { HwSnapshot } from "../lib/types"

export type HudLineId = "cpu" | "gpu"

export type HudLine = {
  readonly id: HudLineId
  readonly label: "CPU" | "GPU"
  readonly tempC: string
  readonly rpm: string
  readonly watt: string
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

export function hudLinesFromSnapshot(snapshot: HwSnapshot): readonly HudLine[] {
  return [
    {
      id: "cpu",
      label: "CPU",
      tempC: formatTempC(snapshot.cpuTempC),
      rpm: formatRpm(snapshot.cpuRpm),
      watt: formatWatt(snapshot.cpuWatt),
    },
    {
      id: "gpu",
      label: "GPU",
      tempC: formatTempC(snapshot.gpuTempC),
      rpm: formatRpm(snapshot.gpuRpm),
      watt: formatWatt(snapshot.gpuWatt),
    },
  ]
}
