import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setLightEffect } from "../lib/api"
import {
  catalogFor,
  type LightChannel,
} from "../lib/lightingCatalog"
import type { LightingVisibility } from "../lib/types"
import "./Lighting.css"

const SEGMENTED_MAX = 4

export type LightingProps = {
  readonly lighting: LightingVisibility
}

export function Lighting({ lighting }: LightingProps) {
  const visible = lighting.keyboard || lighting.lightbar || lighting.logo
  if (!visible) {
    return null
  }
  return (
    <Collapse name="灯光">
      {lighting.keyboard ? (
        <ChannelRow
          name="键盘"
          channel="keyboard"
          keyboardType={lighting.keyboardType}
        />
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

type ChannelRowProps = {
  readonly name: string
  readonly channel: LightChannel
  readonly keyboardType: number
}

function ChannelRow({ name, channel, keyboardType }: ChannelRowProps) {
  const options = catalogFor(channel, keyboardType)
  const [effect, setEffect] = useState(options[0]?.value ?? "Single")

  async function onChange(next: string): Promise<void> {
    setEffect(next)
    try {
      await setLightEffect(channel, next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name={name}>
      {options.length <= SEGMENTED_MAX ? (
        <Segmented value={effect} options={options} onChange={onChange} />
      ) : (
        <select
          className="lighting-select"
          value={effect}
          aria-label={name}
          onChange={(event) => {
            void onChange(event.target.value)
          }}
        >
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      )}
    </Row>
  )
}
