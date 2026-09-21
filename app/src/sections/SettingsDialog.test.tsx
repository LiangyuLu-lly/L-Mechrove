import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { SettingsDialog } from "./SettingsDialog"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("SettingsDialog", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders 外观 显示 系统 and omits overlay toggle", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.getByText("外观")).toBeTruthy()
    expect(screen.getByText("显示")).toBeTruthy()
    expect(screen.getByText("系统")).toBeTruthy()
    expect(screen.queryByText("悬浮窗")).toBeNull()
  })

  it("omits calibration radios when hdrOn is true", () => {
    render(<SettingsDialog hdrOn={true} onClose={() => undefined} />)
    expect(screen.queryByRole("radio", { name: "sRGB" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "默认" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "P3" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "Adobe" })).toBeNull()
    expect(screen.getByText("显示")).toBeTruthy()
    expect(screen.getByText("响应加速")).toBeTruthy()
  })

  it("invokes set_calibration with SRGB when sRGB is clicked and HDR is off", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: "sRGB" }))
    expect(invoke).toHaveBeenCalledWith("set_calibration", {
      mode: "COLOR_CALIBRATION_ON_SRGB",
    })
  })

  it("shows 隔离官方界面与托盘 and omits 跟随系统", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(
      screen.getByRole("button", { name: "隔离官方界面与托盘" }),
    ).toBeTruthy()
    expect(screen.getByRole("button", { name: "恢复官方控制台" })).toBeTruthy()
    expect(screen.queryByRole("radio", { name: "跟随系统" })).toBeNull()
    expect(screen.queryByText("跟随系统")).toBeNull()
  })

  it("invokes set_official_isolation true when 隔离官方界面与托盘 is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("button", { name: "隔离官方界面与托盘" }))
    expect(invoke).toHaveBeenCalledWith("set_official_isolation", {
      isolate: true,
    })
  })
})
