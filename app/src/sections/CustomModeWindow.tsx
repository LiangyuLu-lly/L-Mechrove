import { useEffect, useState } from "react"
import { FanCurve } from "./FanCurve"
import { PerformanceCustom } from "./PerformanceCustom"
import "../App.css"

const EMPTY_FAN_DUTIES = [
  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
] as const

export type CustomModeWindowProps = {
  readonly ocSettings?: boolean
  readonly duties?: readonly number[]
}

export function CustomModeWindow({
  ocSettings = true,
  duties,
}: CustomModeWindowProps) {
  const [curveOpen, setCurveOpen] = useState(false)
  const [curveDuties, setCurveDuties] = useState<readonly number[]>(
    duties ?? EMPTY_FAN_DUTIES,
  )

  useEffect(() => {
    document.documentElement.dataset.theme = "night"
    document.title = "自定义性能模式"
  }, [])

  return (
    <div className="shell">
      <div className="shell__body">
        <h1 className="row__name">自定义性能模式</h1>
        <PerformanceCustom
          ocSettings={ocSettings}
          onOpenFanCurve={() => {
            setCurveOpen(true)
          }}
        />
        {curveOpen ? (
          <FanCurve
            type="CPU"
            duties={curveDuties}
            onChange={setCurveDuties}
          />
        ) : null}
      </div>
    </div>
  )
}
