import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import {
  setCalibration,
  setCustomDetail,
  setLocalDimming,
  setMonitorOff,
  setOverdrive,
} from "../lib/api"

const THEME_OPTIONS = [
  { value: "night", label: "夜间" },
  { value: "day", label: "日间" },
  { value: "system", label: "跟随系统" },
] as const

type ThemeMode = (typeof THEME_OPTIONS)[number]["value"]

const CALIBRATION_OPTIONS = [
  { value: "COLOR_CALIBRATION_ON_DEFAULT", label: "默认" },
  { value: "COLOR_CALIBRATION_ON_SRGB", label: "sRGB" },
  { value: "COLOR_CALIBRATION_ON_P3", label: "P3" },
  { value: "COLOR_CALIBRATION_ON_ADOBERGB", label: "Adobe" },
] as const

type CalibrationMode = (typeof CALIBRATION_OPTIONS)[number]["value"]

const ON_OFF = [
  { value: "on", label: "开" },
  { value: "off", label: "关" },
] as const

type OnOff = (typeof ON_OFF)[number]["value"]

export type SettingsDialogProps = {
  readonly hdrOn: boolean
  readonly onClose: () => void
}

async function swallowHostError(run: () => Promise<unknown>): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

function applyTheme(mode: ThemeMode): void {
  if (mode === "system") {
    const dark = window.matchMedia("(prefers-color-scheme: dark)").matches
    document.documentElement.dataset.theme = dark ? "night" : "day"
    return
  }
  document.documentElement.dataset.theme = mode
}

export function SettingsDialog({ hdrOn, onClose }: SettingsDialogProps) {
  const [theme, setTheme] = useState<ThemeMode>("night")
  const [calibration, setCalibrationMode] = useState<CalibrationMode>(
    "COLOR_CALIBRATION_ON_DEFAULT",
  )
  const [overdrive, setOverdriveOn] = useState<OnOff>("off")
  const [localDimming, setLocalDimmingOn] = useState<OnOff>("off")

  function onTheme(next: ThemeMode): void {
    setTheme(next)
    applyTheme(next)
  }

  function onCalibration(next: CalibrationMode): void {
    setCalibrationMode(next)
    void swallowHostError(() => setCalibration(next))
  }

  function onOverdrive(next: OnOff): void {
    setOverdriveOn(next)
    void swallowHostError(() => setOverdrive(next === "on"))
  }

  function onLocalDimming(next: OnOff): void {
    setLocalDimmingOn(next)
    void swallowHostError(() => setLocalDimming(next === "on"))
  }

  return (
    <div
      className="settings-dialog"
      role="presentation"
      onClick={onClose}
    >
      <div
        className="settings-dialog__panel"
        role="dialog"
        aria-labelledby="settings-title"
        onClick={(event) => {
          event.stopPropagation()
        }}
      >
        <div className="settings-dialog__head">
          <h2 id="settings-title" className="settings-dialog__title">
            设置
          </h2>
          <button
            type="button"
            className="settings-dialog__close"
            aria-label="关闭设置"
            onClick={onClose}
          >
            <svg
              viewBox="0 0 16 16"
              width="16"
              height="16"
              aria-hidden="true"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.25"
            >
              <path d="M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6" />
            </svg>
          </button>
        </div>
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">外观</h3>
          <Row name="主题">
            <Segmented
              value={theme}
              options={THEME_OPTIONS}
              onChange={onTheme}
            />
          </Row>
        </section>
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">显示</h3>
          {hdrOn ? null : (
            <Row name="校色">
              <Segmented
                value={calibration}
                options={CALIBRATION_OPTIONS}
                onChange={onCalibration}
              />
            </Row>
          )}
          <Row name="响应加速">
            <Segmented
              value={overdrive}
              options={ON_OFF}
              onChange={onOverdrive}
            />
          </Row>
          <Row name="局部调光">
            <Segmented
              value={localDimming}
              options={ON_OFF}
              onChange={onLocalDimming}
            />
          </Row>
        </section>
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">系统</h3>
          <Row name="PL1">
            <input
              className="settings-dialog__number"
              type="number"
              min={15}
              max={150}
              defaultValue={45}
              aria-label="PL1"
              onBlur={(event) => {
                void swallowHostError(() =>
                  setCustomDetail("PL1", event.target.value),
                )
              }}
            />
          </Row>
          <Row name="熄屏">
            <button
              type="button"
              className="settings-dialog__action"
              onClick={() => {
                void swallowHostError(() => setMonitorOff())
              }}
            >
              立即熄屏
            </button>
          </Row>
          <Row name="官方控制台" status="未隔离" />
        </section>
      </div>
    </div>
  )
}
