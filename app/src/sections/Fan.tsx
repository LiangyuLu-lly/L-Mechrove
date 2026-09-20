import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setFanBoost, setFanCurve } from "../lib/api"
import type { FanCurveType } from "../lib/types"

const FAN_OPTIONS = [
  { value: "CPU", label: "CPU" },
  { value: "GPU", label: "GPU" },
] as const

const BOOST_OPTIONS = [
  { value: "off", label: "关" },
  { value: "on", label: "加速" },
] as const

const DEFAULT_DUTIES: number[] = [
  0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
]

async function swallow(run: () => Promise<unknown>): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

export function Fan() {
  const [ty, setTy] = useState<FanCurveType>("CPU")
  const [duties, setDuties] = useState<number[]>(DEFAULT_DUTIES)
  const [boost, setBoost] = useState<"off" | "on">("off")

  async function commit(nextTy: FanCurveType, nextDuties: readonly number[]): Promise<void> {
    await swallow(() => setFanCurve("curve", nextTy, nextDuties))
  }

  async function onType(next: FanCurveType): Promise<void> {
    setTy(next)
    await commit(next, duties)
  }

  async function onDuty(index: number, raw: string): Promise<void> {
    const parsed = Number.parseInt(raw, 10)
    const clamped = Number.isFinite(parsed)
      ? Math.min(100, Math.max(0, parsed))
      : 0
    const next = duties.map((duty, i) => (i === index ? clamped : duty))
    setDuties(next)
    await commit(ty, next)
  }

  async function onBoost(next: "off" | "on"): Promise<void> {
    setBoost(next)
    await swallow(() => setFanBoost(next === "on"))
  }

  return (
    <Row name="风扇">
      <Segmented value={ty} options={FAN_OPTIONS} onChange={onType} />
      <Segmented value={boost} options={BOOST_OPTIONS} onChange={onBoost} />
      <div className="fan-duties">
        {duties.map((duty, index) => (
          <input
            key={index}
            className="fan-duties__cell"
            type="number"
            min={0}
            max={100}
            value={duty}
            aria-label={`T${index}`}
            onChange={(event) => {
              void onDuty(index, event.target.value)
            }}
          />
        ))}
      </div>
    </Row>
  )
}
