import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { overlayUpdate } from "../lib/api"
import type { HwSnapshot } from "../lib/types"
import { Hud, HudPanel } from "./Hud"
import { persistPrefsForBlock } from "./hudOverlay"

const invoke = mock(() => Promise.resolve())
const startDragging = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

mock.module("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({ startDragging }),
}))

mock.module("@tauri-apps/api/event", () => ({
  listen: () => Promise.resolve(() => {}),
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
  batteryHealth: "",
  chargeStatus: "",
  chargeFullOffered: true,
  overdrive: false,
  localDimming: false,
  customProfileOffered: true,
  lcConnection: "none",
  fanCurveTableName: "M4T1",
  updateAvailable: false,
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
    window.localStorage.clear()
  })

  afterEach(() => {
    cleanup()
    window.localStorage.clear()
  })

  it("renders a hidden overlay block enabled through overlayUpdate prefs", async () => {
    await overlayUpdate(persistPrefsForBlock("showBattery", true))

    render(<Hud />)

    expect(screen.getByText("0%")).toBeTruthy()
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

    it("renders only telemetry for the current mode with no overlay editing controls", () => {
      render(<HudPanel snapshot={FAKE_TELEMETRY} />)

      expect(screen.queryAllByRole("checkbox")).toHaveLength(0)
      expect(screen.queryAllByRole("button")).toHaveLength(0)
      expect(screen.queryAllByRole("combobox")).toHaveLength(0)
      expect(screen.queryAllByRole("switch")).toHaveLength(0)
      expect(screen.queryAllByRole("slider")).toHaveLength(0)
      expect(document.querySelectorAll("select")).toHaveLength(0)
      expect(screen.queryByText("仅游戏显示")).toBeNull()
      expect(screen.queryByText("熄屏挂起")).toBeNull()
      expect(screen.queryByText("温度")).toBeNull()
      expect(screen.queryByText("风扇")).toBeNull()
      expect(screen.queryByText("功耗")).toBeNull()
      expect(screen.queryByText("占用")).toBeNull()
      expect(screen.queryByText("内存")).toBeNull()
      expect(screen.queryByText("电池")).toBeNull()
      expect(screen.queryByText("名称")).toBeNull()
      expect(screen.getByText("CPU")).toBeTruthy()
      expect(screen.getByText("GPU")).toBeTruthy()
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

  it("does not start drag on bare pointerdown", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    fireEvent.pointerDown(hudRoot(container))

    expect(startDragging).not.toHaveBeenCalled()
  })

  it("does not start drag when movement stays under4px", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)
    const root = hudRoot(container)

    fireEvent.pointerDown(root, { clientX: 50, clientY: 50 })
    fireEvent.pointerMove(root, { clientX: 52, clientY: 51 })

    expect(startDragging).not.toHaveBeenCalled()
  })

  it("starts drag after 5px of pointer movement", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)
    const root = hudRoot(container)

    fireEvent.pointerDown(root, { clientX: 50, clientY: 50 })
    fireEvent.pointerMove(root, { clientX: 50, clientY: 56 })

    expect(startDragging).toHaveBeenCalled()
  })

  it("click with no movement still cycles mode", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    fireEvent.click(hudRoot(container))

    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { mode: "full" },
    })
  })

  it("hides telemetry when gameOnly is on and is_game is false", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} gameOnly is_game={false} />)

    expect(screen.queryByText("CPU")).toBeNull()
    expect(screen.queryByText("78C")).toBeNull()
  })

  it("shows telemetry when gameOnly is on and is_game is true", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} gameOnly is_game={true} />)

    expect(screen.getByText("CPU")).toBeTruthy()
    expect(screen.getByText("78C")).toBeTruthy()
  })

  it("hides telemetry when displayOff is on and display_off is true", () => {
    render(
      <HudPanel snapshot={FAKE_TELEMETRY} displayOff display_off={true} />,
    )

    expect(screen.queryByText("CPU")).toBeNull()
    expect(screen.queryByText("78C")).toBeNull()
  })

  it("shows telemetry when displayOff is on and display_off is false", () => {
    render(
      <HudPanel snapshot={FAKE_TELEMETRY} displayOff display_off={false} />,
    )

    expect(screen.getByText("CPU")).toBeTruthy()
  })

  it("reports overlayUpdate failure through onHostError instead of swallowing it", async () => {
    const onHostError = mock(() => {})
    invoke.mockRejectedValueOnce(new Error("overlay host down"))
    const { container } = render(
      <HudPanel snapshot={FAKE_TELEMETRY} onHostError={onHostError} />,
    )

    fireEvent.click(hudRoot(container))
    await Promise.resolve()
    await Promise.resolve()

    expect(onHostError).toHaveBeenCalledWith("overlay host down")
  })

  it("complete mode drops names when container is 320px wide", () => {
    const orig = window.innerWidth
    Object.defineProperty(window, "innerWidth", {
      value: 320,
      configurable: true,
      writable: true,
    })

    render(
      <HudPanel snapshot={NAMED_TELEMETRY} mode="complete" names />,
    )

    expect(screen.queryByText("Ultra 185H")).toBeNull()
    expect(screen.queryByText("RTX 4070")).toBeNull()
    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.getByText("82C")).toBeTruthy()

    Object.defineProperty(window, "innerWidth", {
      value: orig,
      configurable: true,
      writable: true,
    })
  })

  it("complete mode drops ram usage battery when container is 320px wide", () => {
    const orig = window.innerWidth
    Object.defineProperty(window, "innerWidth", {
      value: 320,
      configurable: true,
      writable: true,
    })

    render(
      <HudPanel snapshot={NAMED_TELEMETRY} mode="complete" names />,
    )

    expect(screen.queryByText("8.5GB")).toBeNull()
    expect(screen.queryByText("40%")).toBeNull()
    expect(screen.queryByText("70%")).toBeNull()
    expect(screen.queryByText("100%")).toBeNull()
    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.getByText("45.0W")).toBeTruthy()

    Object.defineProperty(window, "innerWidth", {
      value: orig,
      configurable: true,
      writable: true,
    })
  })

  it("hud root has overflow hidden and lines do not wrap", () => {
    const { container } = render(<HudPanel snapshot={FAKE_TELEMETRY} />)
    const root = hudRoot(container)

    expect(root.classList.contains("hud")).toBe(true)

    const lines = root.querySelectorAll(".hud__line")
    expect(lines.length).toBe(2)
    for (const line of lines) {
      expect(line.classList.contains("hud__line")).toBe(true)
    }
  })
})
