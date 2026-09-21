import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { Slider } from "../components/Slider"
import { setLightEffect, setLightPower } from "../lib/api"
import {
  catalogFor,
  type LightChannel,
} from "../lib/lightingCatalog"

const SEGMENTED_MAX = 4

const SPEED_OPTIONS = [
  { value: "1", label: "慢" },
  { value: "2", label: "中" },
  { value: "3", label: "快" },
] as const

type Speed = (typeof SPEED_OPTIONS)[number]["value"]

export type ChannelRowProps = {
  readonly name: string
  readonly channel: LightChannel
  readonly keyboardType: number
}

export function ChannelRow({ name, channel, keyboardType }: ChannelRowProps) {
  const options = catalogFor(channel, keyboardType)
  const [effect, setEffect] = useState(options[0]?.value ?? "Single")
  const [power, setPower] = useState(true)
  const [editing, setEditing] = useState(false)
  const [light, setLight] = useState(4)
  const [speed, setSpeed] = useState<Speed>("1")
  const [color, setColor] = useState("#ffffff")

  async function invokeSafe(run: () => Promise<void>): Promise<void> {
    try {
      await run()
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  function effectParams(nextEffect: string): {
    readonly light: string
    readonly speed: string
    readonly color?: string
  } {
    return {
      light: String(light),
      speed,
      ...(nextEffect === "Single" ? { color } : {}),
    }
  }

  async function onEffect(next: string): Promise<void> {
    setEffect(next)
    if (!power) {
      return
    }
    await invokeSafe(() => setLightEffect(channel, next, effectParams(next)))
  }

  async function onPower(next: boolean): Promise<void> {
    setPower(next)
    await invokeSafe(async () => {
      await setLightPower(channel, next)
      if (next) {
        await setLightEffect(channel, effect, effectParams(effect))
      }
    })
  }

  async function onParams(nextLight: number, nextSpeed: Speed, nextColor: string): Promise<void> {
    setLight(nextLight)
    setSpeed(nextSpeed)
    setColor(nextColor)
    if (!power) {
      return
    }
    await invokeSafe(() =>
      setLightEffect(channel, effect, {
        light: String(nextLight),
        speed: nextSpeed,
        ...(effect === "Single" ? { color: nextColor } : {}),
      }),
    )
  }

  return (
    <Row name={name}>
      <div className="lighting-controls">
        <input
          className="lighting-power"
          type="checkbox"
          checked={power}
          aria-label={`${name}电源`}
          onChange={(event) => {
            void onPower(event.currentTarget.checked)
          }}
        />
        {options.length <= SEGMENTED_MAX ? (
          <Segmented value={effect} options={options} onChange={onEffect} />
        ) : (
          <select
            className="lighting-select"
            value={effect}
            aria-label={name}
            onChange={(event) => {
              void onEffect(event.target.value)
            }}
          >
            {options.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        )}
        <button
          type="button"
          className="lighting-edit"
          onClick={() => {
            setEditing(!editing)
          }}
        >
          编辑
        </button>
      </div>
      {editing ? (
        <ChannelEditor
          light={light}
          speed={speed}
          color={color}
          showColor={effect === "Single"}
          enabled={power}
          onParams={onParams}
        />
      ) : null}
    </Row>
  )
}

type ChannelEditorProps = {
  readonly light: number
  readonly speed: Speed
  readonly color: string
  readonly showColor: boolean
  readonly enabled: boolean
  readonly onParams: (light: number, speed: Speed, color: string) => Promise<void>
}

function ChannelEditor({
  light,
  speed,
  color,
  showColor,
  enabled,
  onParams,
}: ChannelEditorProps) {
  return (
    <div className="lighting-editor">
      <div className="lighting-editor__row">
        <span className="lighting-editor__label">亮度</span>
        <Slider
          value={light}
          min={0}
          max={4}
          label="亮度"
          onChange={
            enabled
              ? (value) => {
                  void onParams(value, speed, color)
                }
              : undefined
          }
        />
      </div>
      <div className="lighting-editor__row">
        <span className="lighting-editor__label">速度</span>
        <Segmented
          value={speed}
          options={SPEED_OPTIONS}
          onChange={(next) => {
            if (!enabled) {
              return
            }
            void onParams(light, next, color)
          }}
        />
      </div>
      {showColor ? (
        <div className="lighting-editor__row">
          <span className="lighting-editor__label">单色颜色</span>
          <input
            className="lighting-color"
            type="color"
            value={color}
            disabled={!enabled}
            aria-label="单色颜色"
            onChange={(event) => {
              void onParams(light, speed, event.currentTarget.value)
            }}
          />
        </div>
      ) : null}
    </div>
  )
}
