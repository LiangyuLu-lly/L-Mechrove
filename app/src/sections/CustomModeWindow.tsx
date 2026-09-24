import { useEffect, useState } from "react"
import { Toast } from "../components/Toast"
import { hwSnapshot } from "../lib/api"
import type { PerformanceMode } from "../lib/types"
import { FanCurve } from "./FanCurve"
import { PerformanceCustom } from "./PerformanceCustom"
import { tableNameForProfile } from "./customModeFields"
import "../App.css"
import "./CustomModeWindow.css"

const EMPTY_FAN_DUTIES = [
  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
] as const

const MODE_TITLES: Readonly<Record<PerformanceMode, string>> = {
  office: "静音性能模式",
  gaming: "平衡性能模式",
  turbo: "狂暴性能模式",
  silentTurbo: "静音狂暴性能模式",
  custom: "自定义性能模式",
}

export type CustomModeWindowProps = {
  readonly mode?: PerformanceMode
  readonly ocSettings?: boolean
  readonly duties?: readonly number[]
  readonly gpuDuties?: readonly number[]
  readonly tableName?: string
  readonly powerWallVerdict?: string
}

export function CustomModeWindow({
  mode = "custom",
  ocSettings: ocSettingsProp,
  duties,
  gpuDuties,
  tableName,
  powerWallVerdict,
}: CustomModeWindowProps) {
  const [ocSettings, setOcSettings] = useState(ocSettingsProp ?? false)
  const [curveOpen, setCurveOpen] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [hostTableName, setHostTableName] = useState(tableName)
  const [cpuDuties, setCpuDuties] = useState<readonly number[]>(
    duties ?? EMPTY_FAN_DUTIES,
  )
  const [gpuCurveDuties, setGpuCurveDuties] = useState<readonly number[]>(
    gpuDuties ?? EMPTY_FAN_DUTIES,
  )

  useEffect(() => {
    document.documentElement.dataset.theme = "night"
    document.title = MODE_TITLES[mode]
  }, [mode])

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
        if (snap.fanCurveTableName.length > 0) {
          setHostTableName(snap.fanCurveTableName)
        }
      })
      .catch((error: unknown) => {
        setToast(error instanceof Error ? error.message : String(error))
      })
    return () => {
      cancelled = true
    }
  }, [ocSettingsProp])

  return (
    <div className="shell">
      <div className="shell__body">
        <h1 className="row__name">{MODE_TITLES[mode]}</h1>
        <div className="firmware-slot-banner" role="note">
          <p>固件槽可写性：未验证。</p>
          <p>
            写入静音、平衡、狂暴前需要本机测试：把非默认风扇曲线写入该模式，重启，再读回。
          </p>
          <p>未通过前，只有自定义会下发设置。</p>
        </div>
        <PerformanceCustom
          ocSettings={ocSettings}
          tableName={hostTableName ?? tableNameForProfile("0")}
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
            onHostError={setToast}
          />
        ) : null}
      </div>
      <Toast
        message={toast}
        onDismiss={() => {
          setToast(null)
        }}
      />
    </div>
  )
}
