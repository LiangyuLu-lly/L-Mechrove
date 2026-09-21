import { useState } from "react"
import { Checkbox } from "../components/Checkbox"
import { Collapse } from "../components/Collapse"
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

export type MoreSwitchesProps = {
  readonly offered: readonly string[]
  readonly onHostError?: (message: string) => void
}

export function MoreSwitches({ offered, onHostError }: MoreSwitchesProps) {
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
            <SwitchRow key={key} switchKey={key} onHostError={onHostError} />
          ))}
        </section>
      ))}
      {extras.map((key) => (
        <SwitchRow key={key} switchKey={key} onHostError={onHostError} />
      ))}
    </Collapse>
  )
}

type SwitchRowProps = {
  readonly switchKey: string
  readonly onHostError?: (message: string) => void
}

function SwitchRow({ switchKey, onHostError }: SwitchRowProps) {
  const [checked, setChecked] = useState(false)
  const label = SWITCH_LABELS[switchKey] ?? switchKey

  async function onChange(next: boolean): Promise<void> {
    const previous = checked
    setChecked(next)
    try {
      await setQuickSwitch(switchKey, next)
    } catch (error) {
      setChecked(previous)
      onHostError?.(error instanceof Error ? error.message : String(error))
    }
  }

  return <Checkbox checked={checked} label={label} onChange={onChange} />
}
