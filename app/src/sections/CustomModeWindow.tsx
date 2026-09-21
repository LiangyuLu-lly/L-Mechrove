import { useEffect, useState } from "react"
import { hwSnapshot } from "../lib/api"
import { FanCurve } from "./FanCurve"
import { PerformanceCustom } from "./PerformanceCustom"
import { tableNameForProfile } from "./customModeFields"
import "../App.css"
import "./CustomModeWindow.css"

const EMPTY_FAN_DUTIES = [
  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
] as const

export type CustomModeWindowProps = {
  readonly ocSettings?: boolean
  readonly duties?: readonly number[]
  readonly gpuDuties?: readonly number[]
  readonly tableName?: string
  readonly powerWallVerdict?: string
}

export function CustomModeWindow({
  ocSettings: ocSettingsProp,
  duties,
  gpuDuties,
  tableName,
  powerWallVerdict,
}: CustomModeWindowProps) {
  const [ocSettings, setOcSettings] = useState(ocSettingsProp ?? false)
  const [curveOpen, setCurveOpen] = useState(false)
  const [cpuDuties, setCpuDuties] = useState<readonly number[]>(
    duties ?? EMPTY_FAN_DUTIES,
  )
  const [gpuCurveDuties, setGpuCurveDuties] = useState<readonly number[]>(
    gpuDuties ?? EMPTY_FAN_DUTIES,
  )

  useEffect(() => {
    document.documentElement.dataset.theme = "night"
    document.title = "自定义性能模式"
  }, [])

  useEffect(() => {
    if (ocSettingsProp !== undefined) {
      setOcSettings(ocSettingsProp)
      return
    }
    let cancelled = false
    void hwSnapshot()
      .then((snap) => {
        if (cancelled || snap === undefined || snap === null) {
          return
        }
        if (typeof snap.ocSettings === "boolean") {
          setOcSettings(snap.ocSettings)
        }
      })
      .catch((error: unknown) => {
        if (error instanceof Error) {
          return
        }
        throw error
      })
    return () => {
      cancelled = true
    }
  }, [ocSettingsProp])

  return (
    <div className="shell">
      <div className="shell__body">
        <h1 className="row__name">自定义性能模式</h1>
        <PerformanceCustom
          ocSettings={ocSettings}
          tableName={tableName ?? tableNameForProfile("0")}
          powerWallVerdict={powerWallVerdict}
          onOpenFanCurve={() => {
            setCurveOpen(true)
          }}
        />
        {curveOpen ? (
          <FanCurve
            cpuDuties={cpuDuties}
            gpuDuties={gpuCurveDuties}
            onCpuChange={setCpuDuties}
            onGpuChange={setGpuCurveDuties}
          />
        ) : null}
      </div>
    </div>
  )
}
