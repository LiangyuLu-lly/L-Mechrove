import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setPerformanceMode } from "../lib/api"
import type { PerformanceMode } from "../lib/types"
import { PerformanceWattage } from "./PerformanceWattage"

const ALWAYS_OPTIONS = [
  { value: "office", label: "静音模式" },
  { value: "gaming", label: "平衡模式" },
] as const

const SILENT_TURBO_OPTION = {
  value: "silentTurbo",
  label: "静音狂暴",
} as const

const TAIL_OPTIONS = [
  { value: "turbo", label: "狂暴" },
  { value: "custom", label: "自定义" },
] as const

export type PerformanceProps = {
  readonly tccAdjustable?: boolean
  readonly ocSettings?: boolean
  readonly silentTurbo?: boolean
}

function performanceOptions(silentTurbo: boolean) {
  if (silentTurbo) {
    return [...ALWAYS_OPTIONS, SILENT_TURBO_OPTION, ...TAIL_OPTIONS]
  }
  return [...ALWAYS_OPTIONS, ...TAIL_OPTIONS]
}

export function Performance({
  tccAdjustable = false,
  ocSettings = false,
  silentTurbo = false,
}: PerformanceProps) {
  const [mode, setMode] = useState<PerformanceMode>("office")

  async function onChange(next: PerformanceMode): Promise<void> {
    setMode(next)
    try {
      await setPerformanceMode(next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <>
      <Row name="性能模式">
        <Segmented
          value={mode}
          options={performanceOptions(silentTurbo)}
          onChange={onChange}
        />
      </Row>
      <PerformanceWattage
        tccAdjustable={tccAdjustable}
        ocSettings={ocSettings}
        customMode={mode === "custom"}
      />
    </>
  )
}
