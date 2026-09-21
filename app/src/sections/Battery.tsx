import { useState } from "react"
import { Row } from "../components/Row"
import { Slider } from "../components/Slider"
import { setChargeLimit } from "../lib/api"

export type BatteryProps = {
  readonly percent: number
}

export function Battery({ percent }: BatteryProps) {
  const [value, setValue] = useState(percent)

  async function onChange(next: number): Promise<void> {
    setValue(next)
    try {
      await setChargeLimit(next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name="电池">
      <Slider value={value} min={40} max={100} step={1} onChange={onChange} />
      <span className="row__value">{value}%</span>
    </Row>
  )
}
