import { useEffect, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import { Footer } from "./components/Footer"
import { StatusPill } from "./components/StatusPill"
import { Toast } from "./components/Toast"
import { hwSnapshot, overlayPrefs, VITE_FALLBACK_SNAPSHOT } from "./lib/api"
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

const SNAPSHOT_EVENT = "hw_snapshot"

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
    let unlisten: (() => void) | undefined
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
          customProfileOffered={snapshot.customProfileOffered}
          onHostError={setToast}
        />
        <TelemetryRow snapshot={snapshot} />
        <Gpu actions={snapshot.gpuActions} onHostError={setToast} />
        <Screen
          hzList={snapshot.hzList}
          dcHzSeen={snapshot.dcHzSeen}
          colorCalibration={snapshot.colorCalibration}
          overdrive={snapshot.overdrive}
          localDimming={snapshot.localDimming}
          onHostError={setToast}
        />
        <Battery
          percent={snapshot.chargePercent}
          health={snapshot.batteryHealth === "" ? undefined : snapshot.batteryHealth}
          chargeStatus={
            snapshot.chargeStatus === "" ? undefined : snapshot.chargeStatus
          }
          chargeFullOffered={snapshot.chargeFullOffered}
          onHostError={setToast}
        />
        <LiquidCooling
          liquidCooling={snapshot.liquidCooling}
          connection={snapshot.lcConnection}
          onHostError={setToast}
        />
        <Lighting
          lighting={snapshot.lighting}
          keyboardHidUnavailable={snapshot.keyboardHidUnavailable}
          lightingOffOnBattery={snapshot.lightingOffOnBattery}
          lightingIdleSeconds={snapshot.lightingIdleSeconds}
          onHostError={setToast}
        />
        <MoreSwitches
          offered={snapshot.offeredSwitches}
          onHostError={setToast}
        />
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
        updateAvailable={snapshot.updateAvailable}
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
          overlay={overlayPrefs()}
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
