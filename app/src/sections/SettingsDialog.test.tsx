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

  it("omits migrated 校色 局部调光 PL1 熄屏 and 跟随系统", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.queryByText("校色")).toBeNull()
    expect(screen.queryByRole("radio", { name: "sRGB" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "P3" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "Adobe" })).toBeNull()
    expect(screen.queryByText("局部调光")).toBeNull()
    expect(screen.queryByRole("spinbutton", { name: "PL1" })).toBeNull()
    expect(screen.queryByText("PL1")).toBeNull()
    expect(screen.queryByText("熄屏")).toBeNull()
    expect(screen.queryByRole("button", { name: "立即熄屏" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "跟随系统" })).toBeNull()
    expect(screen.queryByText("跟随系统")).toBeNull()
    expect(screen.queryByText("显示")).toBeNull()
  })

  it("renders 外观 theme 日间/夜间 and 语言 中文/English", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.getByText("外观")).toBeTruthy()
    expect(screen.getByRole("radio", { name: "日间" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "夜间" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "中文" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "English" })).toBeTruthy()
  })

  it("invokes set_theme_mode when 日间 is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: "日间" }))
    expect(invoke).toHaveBeenCalledWith("set_theme_mode", { mode: "day" })
  })

  it("invokes set_ui_language when English is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: "English" }))
    expect(invoke).toHaveBeenCalledWith("set_ui_language", { code: "en" })
  })

  it("shows 隔离官方界面与托盘 and 恢复官方控制台", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(
      screen.getByRole("button", { name: "隔离官方界面与托盘" }),
    ).toBeTruthy()
    expect(screen.getByRole("button", { name: "恢复官方控制台" })).toBeTruthy()
  })

  it("invokes set_official_isolation true when 隔离官方界面与托盘 is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("button", { name: "隔离官方界面与托盘" }))
    expect(invoke).toHaveBeenCalledWith("set_official_isolation", {
      isolate: true,
    })
  })
})
