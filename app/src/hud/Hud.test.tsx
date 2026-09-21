import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import type { HwSnapshot } from "../lib/types"
import { HudPanel } from "./Hud"

const invoke = mock(() => Promise.resolve())
const startDragging = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

mock.module("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({ startDragging }),
}))

const FAKE_TELEMETRY: HwSnapshot = {
  mqtt: "Connected",
  lighting: {
    keyboard: true,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 100,
  gpuActions: [],
  writeAllowed: true,
  hzList: ["60"],
  offeredSwitches: [],
  liquidCooling: false,
  hdrOn: true,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
  colorCalibration: false,
  keyboardHidUnavailable: false,
  lightingOffOnBattery: false,
  lightingIdleSeconds: 0,
  modelReason: "",
  projectId: "",
  ocRequiresElevation: false,
  themeMode: "night",
  releaseLabel: "",
  cpuTempC: 78,
  gpuTempC: 82,
  cpuRpm: 2100,
  gpuRpm: 2100,
  cpuWatt: 45,
  gpuWatt: 80,
}

const NAMED_TELEMETRY = {
  ...FAKE_TELEMETRY,
  cpuName: "Ultra 185H",
  gpuName: "RTX 4070",
  cpuUsage: 40,
  gpuUsage: 70,
  ramUsedGb: 8.5,
  vramUsedGb: 6,
}

function hudRoot(container: HTMLElement): HTMLElement {
  const node = container.querySelector(".hud")
  if (!(node instanceof HTMLElement)) {
    throw new Error("hud root missing")
  }
  return node
}

describe("HudPanel", () => {
  beforeEach(() => {
    invoke.mockClear()
    startDragging.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders Default overlay temp rpm watt and not mqtt charge HDR", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    expect(screen.getByText("CPU")).toBeTruthy()
    expect(screen.getByText("GPU")).toBeTruthy()
    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.getByText("82C")).toBeTruthy()
    expect(screen.getAllByText("2100rpm")).toHaveLength(2)
    expect(screen.getByText("45.0W")).toBeTruthy()
    expect(screen.getByText("80.0W")).toBeTruthy()
    expect(screen.queryByText("Connected")).toBeNull()
    expect(screen.queryByText("100%")).toBeNull()
    expect(screen.queryByText("HDR")).toBeNull()
  })

  it("hides fans when overlay mode is Light", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} mode="light" />)

    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.getByText("45.0W")).toBeTruthy()
    expect(screen.queryAllByText("2100rpm")).toHaveLength(0)
  })

  it("hides Complete fans when showFans is off", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} mode="complete" showFans={false} />)

    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.queryAllByText("2100rpm")).toHaveLength(0)
  })

  it("hides names unless overlay_names is on in Complete", () => {
    render(<HudPanel snapshot={NAMED_TELEMETRY} mode="complete" />)

    expect(screen.queryByText("Ultra 185H")).toBeNull()
    expect(screen.queryByText("RTX 4070")).toBeNull()
  })

  it("shows names when overlay_names is on in Complete", () => {
    render(<HudPanel snapshot={NAMED_TELEMETRY} mode="complete" names />)

    expect(screen.getByText("Ultra 185H")).toBeTruthy()
    expect(screen.getByText("RTX 4070")).toBeTruthy()
  })

  it("maps scalePercent 150 into hud scale style", () => {
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} scalePercent={150} />,
    )
    const root = hudRoot(container)

    expect(root.getAttribute("data-scale")).toBe("150")
    expect(root.style.getPropertyValue("--hud-scale")).toBe("1.5")
    expect(root.className.includes("hud--scale-150")).toBe(true)
  })

  it("clamps scalePercent 20 up to 35 in the scale style", () => {
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} scalePercent={20} />,
    )
    const root = hudRoot(container)

    expect(root.getAttribute("data-scale")).toBe("35")
    expect(root.style.getPropertyValue("--hud-scale")).toBe("0.35")
  })

  it("cycles Default to Full via overlayUpdate on click", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    fireEvent.click(hudRoot(container))

    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { mode: "full" },
    })
  })

  it("cycles Light to Default via overlayUpdate on click", () => {
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} mode="light" />,
    )

    fireEvent.click(hudRoot(container))

    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { mode: "default" },
    })
  })

  it("steps scale by 10 on Ctrl+wheel", () => {
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} scalePercent={100} />,
    )

    fireEvent.wheel(hudRoot(container), { deltaY: -120, ctrlKey: true })

    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { scalePercent: 110 },
    })
  })

  it("clamps Ctrl+wheel scale at 300", () => {
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} scalePercent={300} />,
    )

    fireEvent.wheel(hudRoot(container), { deltaY: -120, ctrlKey: true })

    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { scalePercent: 300 },
    })
  })

  it("starts dragging on pointer down", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    fireEvent.pointerDown(hudRoot(container))

    expect(startDragging).toHaveBeenCalled()
  })
})
