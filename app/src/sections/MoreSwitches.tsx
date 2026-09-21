import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setQuickSwitch } from "../lib/api"

const SWITCH_LABELS: Record<string, string> = {
  touchpad: "触摸板",
  touchpadtoggle: "触摸板切换键",
  wifi: "WiFi",
  bt: "蓝牙",
  webcam: "摄像头",
  numpad: "小键盘锁",
  winkey: "Win键锁",
  fnkey: "Fn键锁",
  copilot: "Copilot键锁",
  osd: "OSD提示",
  usb: "USB充电",
  highperf: "高性能电源",
  fanboost: "风扇增强",
  acrecovery: "来电自启",
  cpuadvperf: "CPU高级性能",
  gamewhitelist: "游戏白名单",
  taskbarautohide: "任务栏自动隐藏",
  transparency: "透明效果",
  darktheme: "深色主题",
  deepsleep: "深度睡眠",
  monitoroff: "息屏（不睡眠）",
  startup: "开机自启动",
  lightbar: "灯条",
  logolight: "Logo 灯",
  singlecolorkb: "单色键盘",
  uni: "Uni",
  omni: "Omni",
  powerlight: "电源灯",
  batterylogo: "电池 Logo",
}

const SWITCH_GROUPS = [
  {
    title: "输入设备",
    keys: ["touchpad", "touchpadtoggle", "wifi", "bt", "webcam", "numpad"],
  },
  {
    title: "键盘与热键",
    keys: ["winkey", "fnkey", "copilot", "osd"],
  },
  {
    title: "电源与系统",
    keys: [
      "usb",
      "highperf",
      "fanboost",
      "acrecovery",
      "cpuadvperf",
      "gamewhitelist",
      "taskbarautohide",
      "transparency",
      "darktheme",
      "deepsleep",
      "monitoroff",
      "startup",
    ],
  },
] as const

const GROUPED_KEYS: ReadonlySet<string> = new Set(
  SWITCH_GROUPS.flatMap((group) => [...group.keys]),
)

const ON_OFF = [
  { value: "on", label: "开" },
  { value: "off", label: "关" },
] as const

type OnOff = (typeof ON_OFF)[number]["value"]

export type MoreSwitchesProps = {
  readonly offered: readonly string[]
}

export function MoreSwitches({ offered }: MoreSwitchesProps) {
  const visible = offered.filter((key) => key !== "whisper")
  const visibleSet = new Set(visible)
  const groups = SWITCH_GROUPS.map((group) => ({
    title: group.title,
    keys: group.keys.filter((key) => visibleSet.has(key)),
  })).filter((group) => group.keys.length > 0)
  const extras = visible.filter((key) => !GROUPED_KEYS.has(key))

  return (
    <Collapse name="更多开关">
      {groups.map((group) => (
        <section key={group.title} className="more-switches__group">
          <p className="more-switches__group-label">{group.title}</p>
          {group.keys.map((key) => (
            <SwitchRow key={key} switchKey={key} />
          ))}
        </section>
      ))}
      {extras.map((key) => (
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
