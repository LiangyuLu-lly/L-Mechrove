import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setLcFan, setLcPump } from "../lib/api"

const PUMP_OPTIONS = [
  { value: "0", label: "低" },
  { value: "1", label: "中" },
  { value: "2", label: "高" },
] as const

const FAN_OPTIONS = [
  { value: "0", label: "0" },
  { value: "1", label: "1" },
  { value: "2", label: "2" },
  { value: "3", label: "3" },
  { value: "4", label: "自动" },
] as const

type PumpIndex = (typeof PUMP_OPTIONS)[number]["value"]
type FanIndex = (typeof FAN_OPTIONS)[number]["value"]

export type LiquidCoolingProps = {
  readonly liquidCooling: boolean
}

export function LiquidCooling({ liquidCooling }: LiquidCoolingProps) {
  if (!liquidCooling) {
    return null
  }
  return (
    <Collapse name="液冷">
      <PumpRow />
      <FanRow />
    </Collapse>
  )
}

function PumpRow() {
  const [index, setIndex] = useState<PumpIndex>("1")

  async function onChange(next: PumpIndex): Promise<void> {
    setIndex(next)
    try {
      await setLcPump(Number(next))
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name="水泵">
      <Segmented value={index} options={PUMP_OPTIONS} onChange={onChange} />
    </Row>
  )
}

function FanRow() {
  const [index, setIndex] = useState<FanIndex>("4")

  async function onChange(next: FanIndex): Promise<void> {
    setIndex(next)
    try {
      await setLcFan(Number(next))
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name="风扇">
      <Segmented value={index} options={FAN_OPTIONS} onChange={onChange} />
    </Row>
  )
}
