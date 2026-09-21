import { Checkbox } from "../components/Checkbox"
import { Collapse } from "../components/Collapse"
import { setLightingPolicy } from "../lib/api"
import type { LightingVisibility } from "../lib/types"
import { ChannelRow } from "./LightingChannel"
import "./Lighting.css"
import "./LightingPolicy.css"

export type LightingProps = {
  readonly lighting: LightingVisibility
  readonly keyboardHidUnavailable?: boolean
  readonly lightingOffOnBattery?: boolean
  readonly lightingIdleSeconds?: number
}

const KEYBOARD_HID_UNAVAILABLE_CAPTION =
  "本机控制器不支持软件灯效控制，已改用官方通道"

const IDLE_OPTIONS = [
  { label: "关闭", idleSeconds: 0 },
  { label: "10 秒（软件）", idleSeconds: 10 },
  { label: "10 分钟", idleSeconds: 600 },
  { label: "15 分钟", idleSeconds: 900 },
  { label: "20 分钟", idleSeconds: 1200 },
  { label: "30 分钟", idleSeconds: 1800 },
  { label: "45 分钟", idleSeconds: 2700 },
  { label: "1 小时", idleSeconds: 3600 },
  { label: "2 小时", idleSeconds: 7200 },
] as const

async function invokePolicy(offOnBattery: boolean, idleSeconds: number): Promise<void> {
  try {
    await setLightingPolicy({ offOnBattery, idleSeconds })
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

function idleSecondsFromValue(value: string): number {
  const match = IDLE_OPTIONS.find((option) => String(option.idleSeconds) === value)
  return match?.idleSeconds ?? 0
}

export function Lighting({
  lighting,
  keyboardHidUnavailable = false,
  lightingOffOnBattery = false,
  lightingIdleSeconds = 0,
}: LightingProps) {
  const visible = lighting.keyboard || lighting.lightbar || lighting.logo
  if (!visible) {
    return null
  }
  const selectedIdle = IDLE_OPTIONS.some((option) => option.idleSeconds === lightingIdleSeconds)
    ? lightingIdleSeconds
    : 0
  return (
    <Collapse name="灯光" defaultOpen>
      <div className="lighting-policy">
        <Checkbox
          checked={lightingOffOnBattery}
          label="离电自动关闭全部灯效"
          onChange={(checked) => {
            void invokePolicy(checked, selectedIdle)
          }}
        />
        <select
          className="lighting-policy__idle lighting-select"
          aria-label="灯光睡眠时间"
          value={String(selectedIdle)}
          onChange={(event) => {
            void invokePolicy(
              lightingOffOnBattery,
              idleSecondsFromValue(event.currentTarget.value),
            )
          }}
        >
          {IDLE_OPTIONS.map((option) => (
            <option key={option.idleSeconds} value={option.idleSeconds}>
              {option.label}
            </option>
          ))}
        </select>
      </div>
      {lighting.keyboard ? (
        <>
          <ChannelRow
            name="键盘"
            channel="keyboard"
            keyboardType={lighting.keyboardType}
          />
          {keyboardHidUnavailable ? (
            <p className="lighting-caption">{KEYBOARD_HID_UNAVAILABLE_CAPTION}</p>
          ) : null}
        </>
      ) : null}
      {lighting.lightbar ? (
        <ChannelRow
          name="灯条"
          channel="lightbar"
          keyboardType={lighting.keyboardType}
        />
      ) : null}
      {lighting.logo ? (
        <ChannelRow
          name="Logo"
          channel="logo"
          keyboardType={lighting.keyboardType}
        />
      ) : null}
    </Collapse>
  )
}
