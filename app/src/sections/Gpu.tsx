import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setGpuRoute } from "../lib/api"

const GPU_LABELS: Record<string, string> = {
  igpu: "集显",
  standard: "标准",
  dgpu: "直连",
  DGPU_DIRECT_CONNECT_TOGGLE_IGPU: "集显",
  DGPU_DIRECT_CONNECT_TOGGLE_OFF: "标准",
  DGPU_DIRECT_CONNECT_TOGGLE_ON: "直连",
  IGPU_ONLY_CONNECT_RB_ON: "集显",
  IGPU_ONLY_CONNECT_RB_OFF: "标准",
  GPU_HOTSWAP_ON: "热切开",
  GPU_HOTSWAP_OFF: "热切关",
  DGPU_DIRECT_CONNECT_RESTART: "重启",
}

const HIDDEN_ACTIONS = new Set(["auto", "IGPU_ONLY_CONNECT_RB_AUTO"])
const DGPU_ACTIONS = new Set(["dgpu", "DGPU_DIRECT_CONNECT_TOGGLE_ON"])

export type GpuProps = {
  readonly actions: readonly string[]
}

export function Gpu({ actions }: GpuProps) {
  const options = actions
    .filter((action) => !HIDDEN_ACTIONS.has(action))
    .map((value) => ({
      value,
      label: GPU_LABELS[value] ?? value,
    }))
  const [selected, setSelected] = useState(options[0]?.value ?? "")

  if (options.length === 0) {
    return null
  }

  async function onChange(next: string): Promise<void> {
    setSelected(next)
    try {
      await setGpuRoute(next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name="显卡模式" status={DGPU_ACTIONS.has(selected) ? "重启生效" : undefined}>
      <Segmented value={selected} options={options} onChange={onChange} />
    </Row>
  )
}
