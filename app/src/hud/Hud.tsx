import { useEffect, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import type { HwSnapshot } from "../lib/types"
import "../App.css"

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
