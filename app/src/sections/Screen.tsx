import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { Slider } from "../components/Slider"
import {
  setAutoRefreshRate,
  setBrightness,
  setCalibration,
  setDisplayHz,
  setLocalDimming,
  setOverdrive,
} from "../lib/api"

const CALIB_DEFAULT = "COLOR_CALIBRATION_ON_DEFAULT"
const CALIB_SRGB = "COLOR_CALIBRATION_ON_SRGB"

export type ScreenProps = {
  readonly hzList: readonly string[]
  readonly dcHzSeen?: boolean
  readonly colorCalibration?: boolean
  readonly overdrive?: boolean
  readonly localDimming?: boolean
  readonly onHostError?: (message: string) => void
}

function reportHostError(
  onHostError: ((message: string) => void) | undefined,
  error: unknown,
): void {
  onHostError?.(error instanceof Error ? error.message : String(error))
}

export function Screen({
  hzList,
  dcHzSeen = false,
  colorCalibration = false,
  overdrive = false,
  localDimming = false,
  onHostError,
}: ScreenProps) {
  const [hz, setHz] = useState(hzList[0] ?? "60")
  const [brightness, setBright] = useState(70)
  const [autoHz, setAutoHz] = useState(false)
  const [calib, setCalib] = useState(CALIB_DEFAULT)
  const [odOn, setOdOn] = useState(false)
  const [ldOn, setLdOn] = useState(false)
  const options = hzList.map((value) => ({
    value,
    label: value,
  }))

  async function onHz(next: string): Promise<void> {
    setHz(next)
    try {
      await setDisplayHz(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onAutoHz(next: boolean): Promise<void> {
    setAutoHz(next)
    try {
      await setAutoRefreshRate(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onBrightness(next: number): Promise<void> {
    setBright(next)
    try {
      await setBrightness(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onCalib(next: string): Promise<void> {
    setCalib(next)
    try {
      await setCalibration(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onOverdrive(next: boolean): Promise<void> {
    setOdOn(next)
    try {
      await setOverdrive(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  async function onLocalDimming(next: boolean): Promise<void> {
    setLdOn(next)
    try {
      await setLocalDimming(next)
    } catch (error) {
      reportHostError(onHostError, error)
    }
  }

  return (
    <Row name="屏幕">
      {colorCalibration ? (
        <select
          className="calib-combo"
          aria-label="屏幕校色"
          value={calib}
          onChange={(event) => {
            void onCalib(event.currentTarget.value)
          }}
        >
          <option value={CALIB_DEFAULT}>默认</option>
          <option value={CALIB_SRGB}>sRGB</option>
        </select>
      ) : null}
      {dcHzSeen ? (
        <label className="auto-hz">
          <input
            type="checkbox"
            aria-label="自动刷新率"
            checked={autoHz}
            onChange={(event) => {
              void onAutoHz(event.target.checked)
            }}
          />
          自动刷新率
        </label>
      ) : null}
      {options.length > 0 ? (
        <Segmented value={hz} options={options} onChange={onHz} />
      ) : null}
      <Slider
        value={brightness}
        min={10}
        max={100}
        label="亮度"
        onChange={onBrightness}
      />
      <span className="row__value">{brightness}%</span>
      {overdrive ? (
        <label className="auto-hz">
          <input
            type="checkbox"
            aria-label="过驱动"
            checked={odOn}
            onChange={(event) => {
              void onOverdrive(event.target.checked)
            }}
          />
          过驱动
        </label>
      ) : null}
      {localDimming ? (
        <label className="auto-hz">
          <input
            type="checkbox"
            aria-label="局部调光"
            checked={ldOn}
            onChange={(event) => {
              void onLocalDimming(event.target.checked)
            }}
          />
          局部调光
        </label>
      ) : null}
    </Row>
  )
}
