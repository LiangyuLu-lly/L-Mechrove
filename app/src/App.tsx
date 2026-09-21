import { useEffect, useState } from "react"
import { Footer } from "./components/Footer"
import { StatusPill } from "./components/StatusPill"
import { Toast } from "./components/Toast"
import { hwSnapshot, VITE_FALLBACK_SNAPSHOT } from "./lib/api"
import type { HwSnapshot } from "./lib/types"
import { Battery } from "./sections/Battery"
import { Gpu } from "./sections/Gpu"
import { Lighting } from "./sections/Lighting"
import { LiquidCooling } from "./sections/LiquidCooling"
import { ModelBanner } from "./sections/ModelBanner"
import { MoreSwitches } from "./sections/MoreSwitches"
import { Performance } from "./sections/Performance"
import { FirstRun } from "./sections/FirstRun"
import { Screen } from "./sections/Screen"
import { SettingsDialog } from "./sections/SettingsDialog"
import { TelemetryRow } from "./sections/TelemetryRow"
import { UpdateDialog } from "./sections/UpdateDialog"
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
  const [updateOpen, setUpdateOpen] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [firstRunOpen, setFirstRunOpen] = useState(() => {
    try {
      return window.localStorage.getItem("lmechrevo.firstRun.done") !== "1"
    } catch {
      return true
    }
  })

  useEffect(() => {
    document.documentElement.dataset.theme = snapshot.themeMode
  }, [snapshot.themeMode])

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
        <ModelBanner
          writeAllowed={snapshot.writeAllowed}
          modelReason={snapshot.modelReason}
          projectId={snapshot.projectId}
        />
        <Performance
          silentTurbo={snapshot.silentTurbo}
          onHostError={setToast}
        />
        <TelemetryRow snapshot={snapshot} />
        <Gpu actions={snapshot.gpuActions} />
        <Screen
          hzList={snapshot.hzList}
          dcHzSeen={snapshot.dcHzSeen}
          colorCalibration={snapshot.colorCalibration}
        />
        <Battery percent={snapshot.chargePercent} />
        <LiquidCooling liquidCooling={snapshot.liquidCooling} />
        <Lighting
          lighting={snapshot.lighting}
          keyboardHidUnavailable={snapshot.keyboardHidUnavailable}
        />
        <MoreSwitches offered={snapshot.offeredSwitches} />
      </div>
      <Footer
        onSettings={() => {
          setSettingsOpen(true)
        }}
        onUpdates={() => {
          setUpdateOpen(true)
        }}
        onHostError={setToast}
        releaseLabel={snapshot.releaseLabel}
      />
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
      {updateOpen ? (
        <UpdateDialog
          onClose={() => {
            setUpdateOpen(false)
          }}
        />
      ) : null}
      <Toast
        message={toast}
        onDismiss={() => {
          setToast(null)
        }}
      />
    </div>
  )
}
