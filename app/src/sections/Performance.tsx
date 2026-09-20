import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setPerformanceMode } from "../lib/api"
import { PerformanceWattage } from "./PerformanceWattage"

const PERFORMANCE_OPTIONS = [
  { value: "silentTurbo", label: "静音" },
  { value: "office", label: "办公" },
  { value: "turbo", label: "狂暴" },
  { value: "custom", label: "自定义" },
] as const

type UiPerformanceMode = (typeof PERFORMANCE_OPTIONS)[number]["value"]

export type PerformanceProps = {
  readonly tccAdjustable?: boolean
  readonly ocSettings?: boolean
}

export function Performance({
  tccAdjustable = false,
  ocSettings = false,
}: PerformanceProps) {
  const [mode, setMode] = useState<UiPerformanceMode>("office")

  async function onChange(next: UiPerformanceMode): Promise<void> {
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
        <Segmented value={mode} options={PERFORMANCE_OPTIONS} onChange={onChange} />
      </Row>
      <PerformanceWattage
        tccAdjustable={tccAdjustable}
        ocSettings={ocSettings}
        customMode={mode === "custom"}
      />
    </>
  )
}
