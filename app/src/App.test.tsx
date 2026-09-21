import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import App from "./App"
import { VITE_FALLBACK_SNAPSHOT } from "./lib/api"
import type { HwSnapshot } from "./lib/types"

const invoke = mock(() => Promise.reject(new Error("no host")))

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

function snapshot(overrides: Partial<HwSnapshot> = {}): HwSnapshot {
  return { ...VITE_FALLBACK_SNAPSHOT, ...overrides }
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

  it("expands the Lighting group", () => {
    render(<App />)
    expect(
      screen.getByRole("button", { name: "灯光" }).getAttribute("aria-expanded"),
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
    fireEvent.click(screen.getByRole("radio", { name: "自定义" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window")
    })
  })

  it("hosts exactly one SettingsDialog", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "稍后" }))
    fireEvent.click(screen.getByRole("button", { name: "设置" }))
    expect(screen.getAllByRole("dialog", { name: "设置" })).toHaveLength(1)
  })
})
