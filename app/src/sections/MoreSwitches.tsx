import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setQuickSwitch } from "../lib/api"

const SWITCH_LABELS: Record<string, string> = {
  touchpad: "触摸板",
  wifi: "Wi-Fi",
  bt: "蓝牙",
  webcam: "摄像头",
  winkey: "Win 键",
  fnkey: "Fn 键",
  osd: "OSD",
  usb: "USB 充电",
  numpad: "数字键盘",
  copilot: "Copilot",
  acrecovery: "通电恢复",
  highperf: "高性能",
  fanboost: "风扇加速",
  lightbar: "灯条",
  logolight: "Logo 灯",
  touchpadtoggle: "触控板切换",
  singlecolorkb: "单色键盘",
  uni: "Uni",
  omni: "Omni",
  powerlight: "电源灯",
  batterylogo: "电池 Logo",
  gamewhitelist: "游戏白名单",
  cpuadvperf: "CPU 超频",
  deepsleep: "深度睡眠",
}

const ON_OFF = [
  { value: "on", label: "开" },
  { value: "off", label: "关" },
] as const

type OnOff = (typeof ON_OFF)[number]["value"]

export type MoreSwitchesProps = {
  readonly offered: readonly string[]
}

export function MoreSwitches({ offered }: MoreSwitchesProps) {
  const keys = offered.filter((key) => key !== "whisper")
  return (
    <Collapse name="更多开关">
      {keys.map((key) => (
        <SwitchRow key={key} switchKey={key} />
      ))}
    </Collapse>
  )
}

type SwitchRowProps = {
  readonly switchKey: string
}

function SwitchRow({ switchKey }: SwitchRowProps) {
  const [value, setValue] = useState<OnOff>("off")
  const label = SWITCH_LABELS[switchKey] ?? switchKey

  async function onChange(next: OnOff): Promise<void> {
    setValue(next)
    try {
      await setQuickSwitch(switchKey, next === "on")
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name={label}>
      <Segmented value={value} options={ON_OFF} onChange={onChange} />
    </Row>
  )
}
