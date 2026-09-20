import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setGpuRoute } from "../lib/api"

const GPU_LABELS: Record<string, string> = {
  DGPU_DIRECT_CONNECT_TOGGLE_ON: "独显",
  DGPU_DIRECT_CONNECT_TOGGLE_OFF: "混合",
  DGPU_DIRECT_CONNECT_TOGGLE_IGPU: "核显",
  IGPU_ONLY_CONNECT_RB_ON: "仅核显",
  IGPU_ONLY_CONNECT_RB_OFF: "核显关",
  GPU_HOTSWAP_ON: "热切开",
  GPU_HOTSWAP_OFF: "热切关",
  DGPU_DIRECT_CONNECT_RESTART: "重启",
}

const AUTO_ACTION = "IGPU_ONLY_CONNECT_RB_AUTO"

export type GpuProps = {
  readonly actions: readonly string[]
}

export function Gpu({ actions }: GpuProps) {
  const options = actions
    .filter((action) => action !== AUTO_ACTION)
    .map((value) => ({
      value,
      label: GPU_LABELS[value] ?? value,
    }))
  const [selected, setSelected] = useState(options[0]?.value ?? "")

  if (options.length === 0) {
    return <Row name="显卡模式" status="—" />
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
    <Row name="显卡模式">
      <Segmented value={selected} options={options} onChange={onChange} />
    </Row>
  )
}
