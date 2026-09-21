import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { openCustomModeWindow, setPerformanceMode } from "../lib/api"
import type { PerformanceMode } from "../lib/types"

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
  readonly silentTurbo?: boolean
  readonly customProfileOffered?: boolean
  readonly onHostError?: (message: string) => void
}

function performanceOptions(silentTurbo: boolean, customProfileOffered: boolean) {
  const tail = customProfileOffered
    ? TAIL_OPTIONS
    : TAIL_OPTIONS.filter((option) => option.value !== "custom")
  if (silentTurbo) {
    return [...ALWAYS_OPTIONS, SILENT_TURBO_OPTION, ...tail]
  }
  return [...ALWAYS_OPTIONS, ...tail]
}

export function Performance({
  silentTurbo = false,
  customProfileOffered = true,
  onHostError,
}: PerformanceProps) {
  const [mode, setMode] = useState<PerformanceMode>("office")

  async function onChange(next: PerformanceMode): Promise<void> {
    setMode(next)
    try {
      await setPerformanceMode(next)
    } catch (error) {
      onHostError?.(error instanceof Error ? error.message : String(error))
    }
    // Opening the editor is the user's request; a failed mode switch must not
    // swallow it.
    if (next === "custom") {
      try {
        await openCustomModeWindow()
      } catch (error) {
        onHostError?.(error instanceof Error ? error.message : String(error))
      }
    }
  }

  return (
    <Row name="性能模式">
      <Segmented
        value={mode}
        options={performanceOptions(silentTurbo, customProfileOffered)}
        onChange={onChange}
      />
    </Row>
  )
}
