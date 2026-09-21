import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setCustomDetail, setPerformanceMode } from "../lib/api"
import {
  BOOST_MODES,
  CUSTOM_PROFILES,
  ON_OFF,
  POWER_PLANS,
  RESTORE_CONFIRM,
  RESTORE_OPERATING_MODE_DETAIL,
  type OnOff,
} from "./customModeFields"
import { NumberField } from "./WattageField"

type ProfileIndex = (typeof CUSTOM_PROFILES)[number]["value"]

export type PerformanceCustomProps = {
  readonly ocSettings: boolean
  readonly tableName: string
  readonly onOpenFanCurve: () => void
  readonly powerWallVerdict?: string
}

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

export function PerformanceCustom({
  ocSettings,
  tableName,
  onOpenFanCurve,
  powerWallVerdict,
}: PerformanceCustomProps) {
  const [profile, setProfile] = useState<ProfileIndex>("0")
  const [powerPlan, setPowerPlan] =
    useState<(typeof POWER_PLANS)[number]["value"]>(
      "381b4222-f694-41f0-9685-ff5bb260df2e",
    )
  const [boostMode, setBoostMode] =
    useState<(typeof BOOST_MODES)[number]["value"]>("1")
  const [tccSwitch, setTccSwitch] = useState<OnOff>("0")
  const [dbSwitch, setDbSwitch] = useState<OnOff>("0")
  const [fanSwitch, setFanSwitch] = useState<OnOff>("0")
  const [ocSwitch, setOcSwitch] = useState<OnOff>("0")

  async function onProfile(next: ProfileIndex): Promise<void> {
    setProfile(next)
    await swallow(async () => {
      await setCustomDetail("ProfileIndex", next)
      await setPerformanceMode("custom")
    })
  }

  async function onRestore(): Promise<void> {
    if (!window.confirm(RESTORE_CONFIRM)) {
      return
    }
    await swallow(() =>
      setCustomDetail(RESTORE_OPERATING_MODE_DETAIL, tableName),
    )
  }

  const verdictWarn = powerWallVerdict?.includes("未生效") === true

  return (
    <>
      {powerWallVerdict !== undefined ? (
        <p
          className={
            verdictWarn
              ? "custom-mode__verdict custom-mode__verdict--warn"
              : "custom-mode__verdict"
          }
          role="status"
          aria-label="功耗墙实测"
        >
          {powerWallVerdict}
        </p>
      ) : null}
      <Row name="自定义档">
        <Segmented value={profile} options={CUSTOM_PROFILES} onChange={onProfile} />
      </Row>
      <Collapse name="功耗" defaultOpen>
        <Row name="电源计划">
          <select
            className="custom-mode__select"
            aria-label="电源计划"
            value={powerPlan}
            onChange={(event) => {
              const next = event.currentTarget.value
              const match = POWER_PLANS.find((plan) => plan.value === next)
              if (match === undefined) {
                return
              }
              setPowerPlan(match.value)
              void swallow(() => setCustomDetail("PowerPlan", match.value))
            }}
          >
            {POWER_PLANS.map((plan) => (
              <option key={plan.value} value={plan.value}>
                {plan.label}
              </option>
            ))}
          </select>
        </Row>
        <Row name="睿频模式">
          <select
            className="custom-mode__select"
            aria-label="睿频模式"
            value={boostMode}
            onChange={(event) => {
              const next = event.currentTarget.value
              const match = BOOST_MODES.find((mode) => mode.value === next)
              if (match === undefined) {
                return
              }
              setBoostMode(match.value)
              void swallow(() => setCustomDetail("BoostMode", match.value))
            }}
          >
            {BOOST_MODES.map((mode) => (
              <option key={mode.value} value={mode.value}>
                {mode.label}
              </option>
            ))}
          </select>
        </Row>
        <NumberField
          name="CPU 功耗墙 PL1 (W)"
          field="PL1"
          ariaLabel="CPU 功耗墙 PL1 (W)"
        />
        <NumberField
          name="CPU 功耗墙 PL2 (W)"
          field="PL2"
          ariaLabel="CPU 功耗墙 PL2 (W)"
        />
        <NumberField
          name="CPU 瞬时功耗墙 PL4 (W)"
          field="PL4"
          ariaLabel="CPU 瞬时功耗墙 PL4 (W)"
        />
        <NumberField
          name="GPU TGP 目标 (W)"
          field="GpuConfigurableTGPTarget"
          ariaLabel="GPU TGP 目标 (W)"
        />
        <Row name="GPU 动态加速">
          <Segmented
            value={dbSwitch}
            options={ON_OFF}
            onChange={(next) => {
              setDbSwitch(next)
              void swallow(() => setCustomDetail("GpuDynamicBoostSwitch", next))
            }}
          />
        </Row>
        <NumberField
          name="动态加速值"
          field="GpuDynamicBoost"
          ariaLabel="动态加速值"
        />
      </Collapse>
      <Collapse name="温度与风扇">
        <Row name="CPU 温度墙">
          <Segmented
            value={tccSwitch}
            options={ON_OFF}
            onChange={(next) => {
              setTccSwitch(next)
              void swallow(() => setCustomDetail("CpuTccOffsetSwitch", next))
            }}
          />
        </Row>
        <NumberField
          name="温度墙值 (°C)"
          field="CpuTccOffset"
          ariaLabel="温度墙值 (°C)"
        />
        <Row name="风扇转换灵敏度">
          <Segmented
            value={fanSwitch}
            options={ON_OFF}
            onChange={(next) => {
              setFanSwitch(next)
              void swallow(() => setCustomDetail("FanSwitchSpeedEnabled", next))
            }}
          />
        </Row>
        <NumberField
          name="换挡延迟 (ms)"
          field="FanSwitchSpeed"
          ariaLabel="换挡延迟 (ms)"
        />
      </Collapse>
      {ocSettings ? (
        <Collapse name="超频" defaultOpen>
          <Row name="GPU 超频">
            <Segmented
              value={ocSwitch}
              options={ON_OFF}
              onChange={(next) => {
                setOcSwitch(next)
                void swallow(() => setCustomDetail("OverClockingSwitch", next))
              }}
            />
          </Row>
          <NumberField
            name="核心频率偏移 (MHz)"
            field="GpuCoreClockOffsetOC"
            ariaLabel="核心频率偏移 (MHz)"
          />
          <NumberField
            name="显存频率偏移 (MHz)"
            field="GpuMemoryClockOffsetOC"
            ariaLabel="显存频率偏移 (MHz)"
          />
        </Collapse>
      ) : null}
      <div className="row__control">
        <button
          type="button"
          className="settings-dialog__action"
          onClick={() => {
            void onRestore()
          }}
        >
          恢复当前档默认
        </button>
        <button
          type="button"
          className="settings-dialog__action"
          onClick={onOpenFanCurve}
        >
          风扇曲线
        </button>
      </div>
    </>
  )
}
