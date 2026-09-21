import { useEffect, useLayoutEffect, useRef, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import { getCurrentWindow } from "@tauri-apps/api/window"
import { overlayUpdate } from "../lib/api"
import type { HwSnapshot } from "../lib/types"
import {
  clampScalePercent,
  hudLinesFromSnapshot,
  nextOverlayMode,
  nextScalePercent,
  type HudLine,
  type HudTelemetry,
  type OverlayModeName,
  type OverlayView,
} from "./hudTelemetry"
import "../App.css"
import "./hud.css"

export type HudPanelProps = {
  readonly snapshot: HudTelemetry
  readonly mode?: OverlayModeName
  readonly scalePercent?: number
  readonly showTemp?: boolean
  readonly showFans?: boolean
  readonly showPower?: boolean
  readonly showUsage?: boolean
  readonly showRam?: boolean
  readonly showBattery?: boolean
  readonly names?: boolean
}

const SNAPSHOT_EVENT = "hw_snapshot"

const EMPTY_SNAPSHOT: HudTelemetry = {
  mqtt: "Disconnected",
  lighting: {
    keyboard: false,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 0,
  gpuActions: [],
  writeAllowed: false,
  hzList: [],
  offeredSwitches: [],
  liquidCooling: false,
  hdrOn: false,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
  keyboardHidUnavailable: false,
  lightingOffOnBattery: false,
  lightingIdleSeconds: 0,
  modelReason: "",
  projectId: "",
  ocRequiresElevation: false,
  themeMode: "night",
  releaseLabel: "",
}

function ignoreHostError(error: unknown): void {
  if (error instanceof Error) {
    return
  }
  throw error
}

function persistOverlay(prefs: {
  readonly mode?: OverlayModeName
  readonly scalePercent?: number
}): void {
  void overlayUpdate(prefs).catch(ignoreHostError)
}

function startHudDrag(): void {
  void getCurrentWindow().startDragging().catch(ignoreHostError)
}

function viewFromProps(
  props: HudPanelProps,
  mode: OverlayModeName,
): OverlayView {
  return {
    mode,
    showTemp: props.showTemp ?? true,
    showFans: props.showFans ?? true,
    showPower: props.showPower ?? true,
    showUsage: props.showUsage ?? true,
    showRam: props.showRam ?? true,
    showBattery: props.showBattery ?? true,
    names: props.names ?? false,
  }
}

function HudMetric({
  className,
  value,
}: {
  readonly className: string
  readonly value: string | undefined
}) {
  if (value === undefined) {
    return null
  }
  return <span className={className}>{value}</span>
}

function HudUsageBar({ line }: { readonly line: HudLine }) {
  if (line.usage === undefined) {
    return null
  }
  return (
    <>
      <span className="hud__usage">{line.usage}</span>
      <span className="hud__bar" aria-hidden="true">
        <span
          className="hud__bar-fill"
          style={{ height: `${line.usagePct ?? 0}%` }}
        />
      </span>
    </>
  )
}

export function HudPanel(props: HudPanelProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  const [mode, setMode] = useState<OverlayModeName>(props.mode ?? "default")
  const [scalePercent, setScalePercent] = useState(props.scalePercent ?? 100)
  const scale = clampScalePercent(scalePercent)
  const lines = hudLinesFromSnapshot(
    props.snapshot,
    viewFromProps(props, mode),
  )

  useLayoutEffect(() => {
    rootRef.current?.style.setProperty("--hud-scale", String(scale / 100))
  }, [scale])

  return (
    <div
      ref={rootRef}
      className={`hud hud--scale-${scale}`}
      data-scale={String(scale)}
      data-mode={mode}
      onClick={() => {
        const next = nextOverlayMode(mode)
        setMode(next)
        persistOverlay({ mode: next })
      }}
      onPointerDown={() => {
        startHudDrag()
      }}
      onWheel={(event) => {
        if (!event.ctrlKey) {
          return
        }
        const next = nextScalePercent(scale, event.deltaY)
        setScalePercent(next)
        persistOverlay({ scalePercent: next })
      }}
    >
      {lines.map((line) => (
        <div key={line.id} className={`hud__line hud__line--${line.id}`}>
          <HudMetric className="hud__name" value={line.name} />
          <span className="hud__label">{line.label}</span>
          <span className="hud__temp">{line.tempC}</span>
          <HudMetric className="hud__rpm" value={line.rpm} />
          <span className="hud__watt">{line.watt}</span>
          <HudUsageBar line={line} />
          <HudMetric className="hud__mem" value={line.mem} />
          <HudMetric className="hud__battery" value={line.battery} />
        </div>
      ))}
    </div>
  )
}

export function Hud() {
  const [snapshot, setSnapshot] = useState<HwSnapshot | undefined>(undefined)

  useEffect(() => {
    let cancelled = false
    let unlisten: (() => void) | undefined
    void listen<HwSnapshot>(SNAPSHOT_EVENT, (event) => {
      setSnapshot(event.payload)
    })
      .then((fn) => {
        if (cancelled) {
          fn()
          return
        }
        unlisten = fn
      })
      .catch(ignoreHostError)
    return () => {
      cancelled = true
      unlisten?.()
    }
  }, [])

  return <HudPanel snapshot={snapshot ?? EMPTY_SNAPSHOT} />
}
