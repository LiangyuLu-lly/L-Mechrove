import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import App from "./App"
import { DISARMED_SNAPSHOT } from "./lib/api"
import type { HwSnapshot } from "./lib/types"

const invoke = mock(() => Promise.reject(new Error("no host")))

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const HOST_SNAPSHOT: HwSnapshot = {
  mqtt: "Disconnected",
  lighting: {
    keyboard: true,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 100,
  gpuActions: [],
  writeAllowed: true,
  hzList: ["60", "165"],
  offeredSwitches: ["touchpad"],
  liquidCooling: false,
  hdrOn: false,
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
  releaseLabel: "0.289.0-beta18",
  batteryHealth: "",
  chargeStatus: "",
  chargeFullOffered: true,
  overdrive: false,
  localDimming: false,
  customProfileOffered: true,
  lcConnection: "none",
  fanCurveTableName: "M4T1",
  updateAvailable: false,
}

function snapshot(overrides: Partial<HwSnapshot> = {}): HwSnapshot {
  return { ...HOST_SNAPSHOT, ...overrides }
}

describe("App FirstRun", () => {
  beforeEach(() => {
    window.localStorage.clear()
  })

  afterEach(() => {
    cleanup()
  })

  it("shows 设置 dialog when 前往系统页 is clicked", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "前往系统页" }))
    expect(screen.getByRole("dialog", { name: "设置" })).toBeTruthy()
  })

  it("does not show 设置 dialog when 稍后 is clicked", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "稍后" }))
    expect(screen.queryByRole("dialog", { name: "设置" })).toBeNull()
  })
})

describe("App main column", () => {
  beforeEach(() => {
    window.localStorage.clear()
    invoke.mockReset()
    invoke.mockImplementation((command: string) => {
      if (command === "hw_snapshot") {
        return Promise.resolve(snapshot())
      }
      return Promise.reject(new Error("no host"))
    })
  })

  afterEach(() => {
    cleanup()
  })

  it("renders the telemetry row under 性能模式", () => {
    const { container } = render(<App />)
    const perf = screen.getByText("性能模式")
    const row = container.querySelector(".telemetry-row")
    expect(row).toBeTruthy()
    expect(
      perf.compareDocumentPosition(row as Node) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBe(Node.DOCUMENT_POSITION_FOLLOWING)
  })

  it("does not render fan-curve sliders or the custom wattage editor", () => {
    render(<App />)
    expect(screen.queryByText("自定义档")).toBeNull()
    expect(screen.queryByText("CPU 功耗墙 PL2 (W)")).toBeNull()
    expect(screen.queryByText("风扇曲线")).toBeNull()
    expect(document.querySelector(".fan-curve")).toBeNull()
  })

  it("expands the Lighting group", async () => {
    render(<App />)
    expect(
      (await screen.findByRole("button", { name: "灯光" })).getAttribute(
        "aria-expanded",
      ),
    ).toBe("true")
  })

  it("shows the keyboard HID caption when keyboardHidUnavailable is true", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "hw_snapshot") {
        return Promise.resolve(snapshot({ keyboardHidUnavailable: true }))
      }
      return Promise.reject(new Error("no host"))
    })
    render(<App />)
    expect(
      await screen.findByText(/本机控制器不支持软件灯效控制/),
    ).toBeTruthy()
  })

  it("invokes open_custom_mode_window when 自定义 is clicked", async () => {
    render(<App />)
    fireEvent.click(await screen.findByRole("radio", { name: "自定义" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window", {
        mode: "custom",
      })
    })
  })

  it("hosts exactly one SettingsDialog", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "稍后" }))
    fireEvent.click(screen.getByRole("button", { name: "设置" }))
    expect(screen.getAllByRole("dialog", { name: "设置" })).toHaveLength(1)
  })

  it("passes battery health and charge labels from the snapshot", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "hw_snapshot") {
        return Promise.resolve(
          snapshot({
            batteryHealth: "循环 12 次",
            chargeStatus: "充电: 20.0W",
          }),
        )
      }
      return Promise.reject(new Error("no host"))
    })
    render(<App />)
    expect(await screen.findByText("循环 12 次")).toBeTruthy()
    expect(screen.getByText("充电: 20.0W")).toBeTruthy()
  })

  it("shows 过驱动 when the snapshot offers overdrive", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "hw_snapshot") {
        return Promise.resolve(snapshot({ overdrive: true }))
      }
      return Promise.reject(new Error("no host"))
    })
    render(<App />)
    expect(await screen.findByRole("checkbox", { name: "过驱动" })).toBeTruthy()
  })
})

describe("App capability-off rendering", () => {
  beforeEach(() => {
    window.localStorage.setItem("lmechrevo.firstRun.done", "1")
    invoke.mockReset()
    invoke.mockImplementation((command: string) => {
      if (command === "hw_snapshot") {
        return Promise.resolve(DISARMED_SNAPSHOT)
      }
      return Promise.reject(new Error("no host"))
    })
  })

  afterEach(() => {
    cleanup()
  })

  it("omits GPU action row when gpuActions is empty", async () => {
    render(<App />)
    expect(screen.queryByText("显卡模式")).toBeNull()
  })

  it("omits keyboard lighting row when lighting is all false", async () => {
    render(<App />)
    expect(screen.queryByText("键盘")).toBeNull()
    expect(screen.queryByText("灯条")).toBeNull()
    expect(screen.queryByText("Logo")).toBeNull()
  })

  it("omits custom-profile option when customProfileOffered is false", async () => {
    render(<App />)
    expect(screen.queryByRole("radio", { name: "自定义" })).toBeNull()
  })

  it("omits liquid-cooling row when liquidCooling is false", async () => {
    render(<App />)
    expect(screen.queryByText("液冷")).toBeNull()
  })

  it("renders zero data-capability=false elements — never shows a dead control", async () => {
    render(<App />)
    expect(
      document.querySelectorAll('[data-capability="false"]').length,
    ).toBe(0)
  })
})
