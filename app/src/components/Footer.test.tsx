import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Footer } from "./Footer"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Footer", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders 6 keys including 诊断 when the shell mounts", () => {
    render(<Footer />)

    const keys = screen.getAllByRole("button").filter((key) =>
      ["悬浮窗", "设置", "更新", "诊断", "赞助", "退出"].includes(
        key.textContent ?? "",
      ),
    )
    expect(keys).toHaveLength(6)
    expect(keys.map((key) => key.textContent)).toEqual([
      "悬浮窗",
      "设置",
      "更新",
      "诊断",
      "赞助",
      "退出",
    ])
    expect(screen.getByRole("button", { name: "导出诊断包" })).toBeTruthy()
  })

  it("invokes overlay_set with on true when 悬浮窗 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "悬浮窗" }))
    expect(invoke).toHaveBeenCalledWith("overlay_set", { on: true })
  })

  it("opens 外观 显示 系统 when 设置 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "设置" }))
    expect(screen.getByText("外观")).toBeTruthy()
    expect(screen.getByText("显示")).toBeTruthy()
    expect(screen.getByText("系统")).toBeTruthy()
  })

  it("invokes updates_check when 更新 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "更新" }))
    expect(invoke).toHaveBeenCalledWith("updates_check")
  })

  it("invokes diagnostics_export when 诊断 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "导出诊断包" }))
    expect(invoke).toHaveBeenCalledWith("diagnostics_export")
  })

  it("does not invoke host commands when 赞助 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "赞助" }))
    expect(invoke).not.toHaveBeenCalled()
  })

  it("shows 赞助支持 when 赞助 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "赞助" }))
    expect(screen.getByText("赞助支持")).toBeTruthy()
  })

  it("invokes app_quit when 退出 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "退出" }))
    expect(invoke).toHaveBeenCalledWith("app_quit")
  })

  it("gives all 6 keys class footer__key including overlay", () => {
    render(<Footer />)
    const keys = screen.getAllByRole("button").filter((key) =>
      ["悬浮窗", "设置", "更新", "诊断", "赞助", "退出"].includes(
        key.textContent ?? "",
      ),
    )
    expect(keys).toHaveLength(6)
    for (const key of keys) {
      expect(key.className).toBe("footer__key")
    }
  })

  it("uses footer__key is-active only when overlay is on", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "悬浮窗" }))
    expect(screen.getByRole("button", { name: "悬浮窗" }).className).toBe(
      "footer__key is-active",
    )
    expect(screen.getByRole("button", { name: "设置" }).className).toBe(
      "footer__key",
    )
  })
})
