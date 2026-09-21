import { useEffect, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import type { HwSnapshot } from "../lib/types"
import { hudLinesFromSnapshot } from "./hudTelemetry"
import "../App.css"

export type HudPanelProps = {
  readonly snapshot: HwSnapshot
}

export function HudPanel({ snapshot }: HudPanelProps) {
  const lines = hudLinesFromSnapshot(snapshot)
  return (
    <div className="hud">
      {lines.map((line) => (
        <div key={line.id} className="hud__line">
          <span>{line.label}</span>
          <span>{line.tempC}</span>
          <span>{line.rpm}</span>
          <span>{line.watt}</span>
        </div>
      ))}
    </div>
  )
}

const SNAPSHOT_EVENT = "hw_snapshot"

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
    <div className="hud">
      <span className="hud__mqtt">{snapshot?.mqtt ?? "—"}</span>
      <span className="hud__metric">
        {snapshot === undefined ? "—" : `${snapshot.chargePercent}%`}
      </span>
      <span className="hud__metric">
        {snapshot === undefined ? "—" : snapshot.hdrOn ? "HDR" : "SDR"}
      </span>
    </div>
  )
}
