import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setCustomDetail, setPerformanceMode } from "../lib/api"
import {
  CUSTOM_PROFILES,
  CUSTOM_RESTORE_DEFAULTS,
  ON_OFF,
  type OnOff,
} from "./customModeFields"
import { NumberField } from "./WattageField"

type ProfileIndex = (typeof CUSTOM_PROFILES)[number]["value"]

export type PerformanceCustomProps = {
  readonly ocSettings: boolean
  readonly onOpenFanCurve: () => void
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
  onOpenFanCurve,
}: PerformanceCustomProps) {
  const [profile, setProfile] = useState<ProfileIndex>("0")
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
    await swallow(async () => {
      for (const [field, value] of CUSTOM_RESTORE_DEFAULTS) {
        await setCustomDetail(field, value)
      }
    })
  }

  return (
    <>
      <Row name="自定义档">
        <Segmented value={profile} options={CUSTOM_PROFILES} onChange={onProfile} />
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
      {ocSettings ? (
        <>
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
        </>
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
