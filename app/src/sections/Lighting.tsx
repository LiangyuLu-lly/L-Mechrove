import { Collapse } from "../components/Collapse"
import type { LightingVisibility } from "../lib/types"
import { ChannelRow } from "./LightingChannel"
import "./Lighting.css"

export type LightingProps = {
  readonly lighting: LightingVisibility
  readonly keyboardHidUnavailable?: boolean
}

const KEYBOARD_HID_UNAVAILABLE_CAPTION =
  "本机控制器不支持软件灯效控制，已改用官方通道"

export function Lighting({ lighting, keyboardHidUnavailable = false }: LightingProps) {
  const visible = lighting.keyboard || lighting.lightbar || lighting.logo
  if (!visible) {
    return null
  }
  return (
    <Collapse name="灯光" defaultOpen>
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
