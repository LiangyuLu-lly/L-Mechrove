import { useEffect, useLayoutEffect, useRef, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import { getCurrentWindow } from "@tauri-apps/api/window"
import { overlayUpdate } from "../lib/api"
import type { HwSnapshot } from "../lib/types"
import {
  EMPTY_HUD_SNAPSHOT,
  overlayPrefs,
  overlayShouldShow,
  subscribeOverlayPrefs,
  type OverlayPersistPrefs,
} from "./hudOverlay"
import {
  clampScalePercent,
  fitColumnsToWidth,
  hudLinesFromSnapshot,
  nextOverlayMode,
  nextScalePercent,
  overlayBlocks,
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
  readonly gameOnly?: boolean
  readonly displayOff?: boolean
  readonly is_game?: boolean
  readonly display_off?: boolean
  readonly onHostError?: (message: string) => void
}

const SNAPSHOT_EVENT = "hw_snapshot"

function reportHostError(
  onHostError: ((message: string) => void) | undefined,
  error: unknown,
): void {
  onHostError?.(error instanceof Error ? error.message : String(error))
}

function persistOverlay(
  prefs: OverlayPersistPrefs,
  onHostError?: (message: string) => void,
): void {
  void overlayUpdate(prefs).catch((error: unknown) => {
    reportHostError(onHostError, error)
  })
}

function startHudDrag(onHostError?: (message: string) => void): void {
  void getCurrentWindow()
    .startDragging()
    .catch((error: unknown) => {
      reportHostError(onHostError, error)
    })
}

function panelFromPrefs(prefs: OverlayPersistPrefs): Omit<HudPanelProps, "snapshot"> {
  return {
    ...(prefs.mode !== undefined ? { mode: prefs.mode } : {}),
    ...(prefs.scalePercent !== undefined
      ? { scalePercent: prefs.scalePercent }
      : {}),
    ...(prefs.gameOnly !== undefined ? { gameOnly: prefs.gameOnly } : {}),
    ...(prefs.displayOff !== undefined ? { displayOff: prefs.displayOff } : {}),
    ...(prefs.showTemp !== undefined ? { showTemp: prefs.showTemp } : {}),
    ...(prefs.showFans !== undefined ? { showFans: prefs.showFans } : {}),
    ...(prefs.showPower !== undefined ? { showPower: prefs.showPower } : {}),
    ...(prefs.showUsage !== undefined ? { showUsage: prefs.showUsage } : {}),
    ...(prefs.showRam !== undefined ? { showRam: prefs.showRam } : {}),
    ...(prefs.showBattery !== undefined
      ? { showBattery: prefs.showBattery }
      : {}),
    ...(prefs.names !== undefined ? { names: prefs.names } : {}),
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

const DRAG_THRESHOLD = 4

export function HudPanel(props: HudPanelProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  const { onHostError } = props
  const [mode, setMode] = useState<OverlayModeName>(props.mode ?? "default")
  const [scalePercent, setScalePercent] = useState(props.scalePercent ?? 100)
  const gameOnly = props.gameOnly ?? false
  const displayOff = props.displayOff ?? false
  const scale = clampScalePercent(scalePercent)
  const visible = overlayShouldShow({
    gameOnly,
    is_game: props.is_game,
    displayOff,
    display_off: props.display_off,
  })

  const pointerStart = useRef<{ x: number; y: number } | null>(null)
  const didDrag = useRef(false)

  const view: OverlayView = {
    mode,
    showTemp: props.showTemp,
    showFans: props.showFans,
    showPower: props.showPower,
    showUsage: props.showUsage,
    showRam: props.showRam,
    showBattery: props.showBattery,
    names: props.names,
  }

  const baseBlocks = overlayBlocks(mode, view)
  const effectiveBlocks = fitColumnsToWidth(
    baseBlocks,
    typeof window !== "undefined" ? window.innerWidth : 320,
  )

  const lines = hudLinesFromSnapshot(props.snapshot, {
    ...view,
    showTemp: effectiveBlocks.temp,
    showFans: effectiveBlocks.fans,
    showPower: effectiveBlocks.power,
    showUsage: effectiveBlocks.usage,
    showRam: effectiveBlocks.ram,
    showBattery: effectiveBlocks.battery,
    names: effectiveBlocks.names,
  })

  useLayoutEffect(() => {
    rootRef.current?.style.setProperty("--hud-scale", String(scale / 100))
  }, [scale])

  if (!visible) {
    return null
  }

  return (
    <div
      ref={rootRef}
      className={`hud hud--scale-${scale}`}
      data-scale={String(scale)}
      data-mode={mode}
      onClick={() => {
        if (didDrag.current) return
        const next = nextOverlayMode(mode)
        setMode(next)
        persistOverlay({ mode: next }, onHostError)
      }}
      onPointerDown={(e) => {
        pointerStart.current = { x: e.clientX, y: e.clientY }
        didDrag.current = false
      }}
      onPointerMove={(e) => {
        if (!pointerStart.current || didDrag.current) return
        const dx = e.clientX - pointerStart.current.x
        const dy = e.clientY - pointerStart.current.y
        if (Math.abs(dx) > DRAG_THRESHOLD || Math.abs(dy) > DRAG_THRESHOLD) {
          didDrag.current = true
          pointerStart.current = null
          startHudDrag(onHostError)
        }
      }}
      onWheel={(event) => {
        if (!event.ctrlKey) {
          return
        }
        const next = nextScalePercent(scale, event.deltaY)
        setScalePercent(next)
        persistOverlay({ scalePercent: next }, onHostError)
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
  const [prefs, setPrefs] = useState(overlayPrefs)

  useEffect(() => {
    return subscribeOverlayPrefs(() => {
      setPrefs(overlayPrefs())
    })
  }, [])

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
      .catch((error: unknown) => {
        if (error instanceof Error) {
          return
        }
        throw error
      })
    return () => {
      cancelled = true
      unlisten?.()
    }
  }, [])

  return (
    <HudPanel
      snapshot={snapshot ?? EMPTY_HUD_SNAPSHOT}
      {...panelFromPrefs(prefs)}
    />
  )
}
