import { useState } from "react"
import { Collapse } from "../components/Collapse"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { assertNever } from "../lib/assertNever"
import { setLcConnect, setLcDisconnect, setLcFan, setLcPump, setLightEffect } from "../lib/api"
import "./LiquidCooling.css"

const PUMP_OPTIONS = [
  { value: "auto", label: "自动" },
  { value: "0", label: "低" },
  { value: "1", label: "中" },
  { value: "2", label: "高" },
] as const

const FAN_OPTIONS = [
  { value: "0", label: "0" },
  { value: "1", label: "1" },
  { value: "2", label: "2" },
  { value: "3", label: "3" },
  { value: "4", label: "自动" },
] as const

const CHIP_COPY = {
  none: "未连接",
  direct: "已直连",
  gcu: "GCU 已连接水冷",
} as const

const LIGHT_GROUPS = [
  {
    header: "头部灯效",
    items: [
      { value: "cyan_static", label: "青色常亮" },
      { value: "cyan_breath", label: "青色呼吸" },
      { value: "colorful", label: "多彩" },
      { value: "colorful_breath", label: "彩色呼吸" },
    ],
  },
  {
    header: "自定义颜色",
    items: [
      { value: "custom_static", label: "自定义颜色常亮…" },
      { value: "custom_breath", label: "自定义颜色呼吸…" },
    ],
  },
  {
    header: "风扇灯效（仅 Mk2）",
    items: [
      { value: "fan_rotate", label: "风扇旋转色（Mk2）" },
      { value: "fan_rainbow", label: "风扇彩虹（Mk2）" },
    ],
  },
] as const

const LIGHT_OFF = { value: "off", label: "关闭全部灯光" } as const

type PumpIndex = (typeof PUMP_OPTIONS)[number]["value"]
type FanIndex = (typeof FAN_OPTIONS)[number]["value"]
export type LcConnection = keyof typeof CHIP_COPY

export type LiquidCoolingProps = {
  readonly liquidCooling: boolean
  readonly connection?: LcConnection
  readonly onHostError?: (message: string) => void
}

async function invokeSafe(
  run: () => Promise<void>,
  onHostError?: (message: string) => void,
): Promise<void> {
  try {
    await run()
  } catch (error) {
    onHostError?.(error instanceof Error ? error.message : String(error))
  }
}

export function LiquidCooling({
  liquidCooling,
  connection = "none",
  onHostError,
}: LiquidCoolingProps) {
  if (!liquidCooling) {
    return null
  }
  return (
    <Collapse name="液冷">
      <PumpRow onHostError={onHostError} />
      <FanRow onHostError={onHostError} />
      <LightRow connection={connection} onHostError={onHostError} />
    </Collapse>
  )
}

function PumpRow({
  onHostError,
}: {
  readonly onHostError?: (message: string) => void
}) {
  const [index, setIndex] = useState<PumpIndex>("1")

  async function onChange(next: PumpIndex): Promise<void> {
    setIndex(next)
    switch (next) {
      case "auto":
        await invokeSafe(() => setLcFan(4), onHostError)
        return
      case "0":
      case "1":
      case "2":
        await invokeSafe(() => setLcPump(Number(next)), onHostError)
        return
      default:
        assertNever(next)
    }
  }

  return (
    <Row name="水泵">
      <Segmented value={index} options={PUMP_OPTIONS} onChange={onChange} />
    </Row>
  )
}

function FanRow({
  onHostError,
}: {
  readonly onHostError?: (message: string) => void
}) {
  const [index, setIndex] = useState<FanIndex>("4")

  async function onChange(next: FanIndex): Promise<void> {
    setIndex(next)
    await invokeSafe(() => setLcFan(Number(next)), onHostError)
  }

  return (
    <Row name="风扇">
      <Segmented value={index} options={FAN_OPTIONS} onChange={onChange} />
    </Row>
  )
}

function LightRow({
  connection,
  onHostError,
}: {
  readonly connection: LcConnection
  readonly onHostError?: (message: string) => void
}) {
  const [chip, setChip] = useState<LcConnection>(connection)
  const [menuOpen, setMenuOpen] = useState(false)
  const [color, setColor] = useState("#00ffff")

  async function onChip(): Promise<void> {
    switch (chip) {
      case "none":
        setChip("direct")
        await invokeSafe(() => setLcConnect(), onHostError)
        return
      case "direct":
        setChip("none")
        await invokeSafe(() => setLcDisconnect(), onHostError)
        return
      case "gcu":
        await invokeSafe(() => setLcConnect(), onHostError)
        return
      default:
        assertNever(chip)
    }
  }

  async function onEffect(effect: string): Promise<void> {
    await invokeSafe(() => setLightEffect("liquid", effect), onHostError)
  }

  async function onColor(next: string): Promise<void> {
    setColor(next)
    await invokeSafe(
      () => setLightEffect("liquid", "custom_static", { color: next }),
      onHostError,
    )
  }

  const connected = chip !== "none"
  return (
    <Row name="灯光">
      <div className="lc-light-row">
        <button
          type="button"
          className="lc-light"
          onClick={() => {
            setMenuOpen(!menuOpen)
          }}
        >
          水冷灯光
        </button>
        <button
          type="button"
          className={connected ? "lc-chip lc-chip--ok" : "lc-chip"}
          style={{ height: "32px" }}
          onClick={() => {
            void onChip()
          }}
        >
          {CHIP_COPY[chip]}
        </button>
      </div>
      {menuOpen ? (
        <div className="lc-menu">
          {LIGHT_GROUPS.map((group) => (
            <div key={group.header}>
              <div className="lc-menu__header">{group.header}</div>
              {group.items.map((item) => (
                <button
                  key={item.value}
                  type="button"
                  className="lc-menu__item"
                  onClick={() => {
                    void onEffect(item.value)
                  }}
                >
                  {item.label}
                </button>
              ))}
            </div>
          ))}
          <div className="lc-menu__color">
            <span className="lc-menu__header">自定义颜色</span>
            <input
              className="lc-color"
              type="color"
              value={color}
              aria-label="自定义颜色"
              onChange={(event) => {
                void onColor(event.currentTarget.value)
              }}
            />
          </div>
          <button
            type="button"
            className="lc-menu__item"
            onClick={() => {
              void onEffect(LIGHT_OFF.value)
            }}
          >
            {LIGHT_OFF.label}
          </button>
        </div>
      ) : null}
    </Row>
  )
}
