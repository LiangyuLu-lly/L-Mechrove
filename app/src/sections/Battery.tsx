import { useState } from "react"
import { Row } from "../components/Row"
import { Slider } from "../components/Slider"
import { setChargeFull, setChargeLimit } from "../lib/api"
import "./Battery.css"

export type BatteryProps = {
  readonly percent: number
  readonly health?: string
  readonly chargeStatus?: string
  readonly chargeFullOffered?: boolean
  readonly onHostError?: (message: string) => void
}

function reportHostError(
  onHostError: ((message: string) => void) | undefined,
  error: unknown,
): void {
  onHostError?.(error instanceof Error ? error.message : String(error))
}

export function Battery({
  percent,
  health,
  chargeStatus,
  chargeFullOffered = true,
  onHostError,
}: BatteryProps) {
  const [value, setValue] = useState(percent)

  async function onChange(next: number): Promise<void> {
    setValue(next)
    try {
      await setChargeLimit(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onFullCharge(): Promise<void> {
    try {
      await setChargeFull()
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  return (
    <Row name="电池" status={health}>
      {chargeStatus !== undefined ? (
        <span className="battery__charge">{chargeStatus}</span>
      ) : null}
      <Slider value={value} min={40} max={100} step={1} onChange={onChange} />
      <span className="row__value">{value}%</span>
      {chargeFullOffered ? (
        <button
          type="button"
          className="battery__full"
          onClick={() => {
            void onFullCharge()
          }}
        >
          充满电
        </button>
      ) : null}
    </Row>
  )
}
