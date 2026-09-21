import { useEffect, useState } from "react"
import { Footer } from "./components/Footer"
import { StatusPill } from "./components/StatusPill"
import { hwSnapshot, VITE_FALLBACK_SNAPSHOT } from "./lib/api"
import type { HwSnapshot } from "./lib/types"
import { Battery } from "./sections/Battery"
import { Fan } from "./sections/Fan"
import { Gpu } from "./sections/Gpu"
import { Lighting } from "./sections/Lighting"
import { LiquidCooling } from "./sections/LiquidCooling"
import { MoreSwitches } from "./sections/MoreSwitches"
import { Performance } from "./sections/Performance"
import { FirstRun } from "./sections/FirstRun"
import { Screen } from "./sections/Screen"
import { SettingsDialog } from "./sections/SettingsDialog"
import "./App.css"

function dismissFirstRun(): void {
  try {
    window.localStorage.setItem("lmechrevo.firstRun.done", "1")
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

export default function App() {
  const [snapshot, setSnapshot] = useState<HwSnapshot>(VITE_FALLBACK_SNAPSHOT)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [firstRunOpen, setFirstRunOpen] = useState(() => {
    try {
      return window.localStorage.getItem("lmechrevo.firstRun.done") !== "1"
    } catch {
      return true
    }
  })

  useEffect(() => {
    let cancelled = false
    void hwSnapshot()
      .then((next) => {
        if (!cancelled) {
          setSnapshot(next)
        }
      })
      .catch((error: unknown) => {
        if (error instanceof Error) {
          return
        }
        throw error
      })
    return () => {
      cancelled = true
    }
  }, [])

  return (
    <div className="shell">
      <div className="shell__body">
        <StatusPill mqtt={snapshot.mqtt} />
        {snapshot.writeAllowed ? null : (
          <p className="readonly-banner" role="status">
            机型只读：硬件写入已关闭
          </p>
        )}
        <Performance
          tccAdjustable={snapshot.tccAdjustable}
          ocSettings={snapshot.ocSettings}
          silentTurbo={snapshot.silentTurbo}
        />
        <Fan />
        <Gpu actions={snapshot.gpuActions} />
        <Screen hzList={snapshot.hzList} dcHzSeen={snapshot.dcHzSeen} />
        <Battery percent={snapshot.chargePercent} />
        <LiquidCooling liquidCooling={snapshot.liquidCooling} />
        <Lighting lighting={snapshot.lighting} />
        <MoreSwitches offered={snapshot.offeredSwitches} />
      </div>
      <Footer hdrOn={snapshot.hdrOn} />
      {firstRunOpen ? (
        <FirstRun
          onLater={() => {
            dismissFirstRun()
            setFirstRunOpen(false)
          }}
          onGoSystem={() => {
            dismissFirstRun()
            setFirstRunOpen(false)
            setSettingsOpen(true)
          }}
        />
      ) : null}
      {settingsOpen ? (
        <SettingsDialog
          hdrOn={snapshot.hdrOn}
          onClose={() => {
            setSettingsOpen(false)
          }}
        />
      ) : null}
    </div>
  )
}
