import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Footer } from "./Footer"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const KEY_ORDER = ["悬浮窗", "设置", "更新", "赞助", "诊断", "退出"] as const

describe("Footer", () => {
  beforeEach(() => {
    invoke.mockClear()
    invoke.mockImplementation(() => Promise.resolve())
  })

  afterEach(() => {
    cleanup()
  })

  it("renders 6 keys in C# order 悬浮窗,设置,更新,赞助,诊断,退出", () => {
    render(<Footer />)

    const keys = screen.getAllByRole("button").filter((key) =>
      KEY_ORDER.includes((key.textContent ?? "") as (typeof KEY_ORDER)[number]),
    )
    expect(keys).toHaveLength(6)
    expect(keys.map((key) => key.textContent)).toEqual([...KEY_ORDER])
    expect(screen.getByRole("button", { name: "导出诊断包" })).toBeTruthy()
  })

  it("renders releaseLabel and never 0.1.0", () => {
    render(<Footer releaseLabel="5.56.60.26" />)
    expect(screen.getByText("5.56.60.26")).toBeTruthy()
    expect(screen.queryByText("0.1.0")).toBeNull()
  })

  it("invokes overlay_set with on true when 悬浮窗 is clicked", () => {
    render(<Footer />)
    fireEvent.click(screen.getByRole("button", { name: "悬浮窗" }))
    expect(invoke).toHaveBeenCalledWith("overlay_set", { on: true })
  })

  it("emits onSettings when 设置 is clicked and does not host SettingsDialog", () => {
    const onSettings = mock(() => undefined)
    render(<Footer onSettings={onSettings} />)
    fireEvent.click(screen.getByRole("button", { name: "设置" }))
    expect(onSettings).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole("dialog", { name: "设置" })).toBeNull()
  })

  it("emits onUpdates when 更新 is clicked and does not invoke updates_check", () => {
    const onUpdates = mock(() => undefined)
    render(<Footer onUpdates={onUpdates} />)
    fireEvent.click(screen.getByRole("button", { name: "更新" }))
    expect(onUpdates).toHaveBeenCalledTimes(1)
    expect(invoke).not.toHaveBeenCalledWith("updates_check")
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

  it("reports host errors to onHostError instead of swallowing", async () => {
    invoke.mockImplementation(() => Promise.reject(new Error("overlay failed")))
    const onHostError = mock(() => undefined)
    render(<Footer onHostError={onHostError} />)
    fireEvent.click(screen.getByRole("button", { name: "悬浮窗" }))
    await waitFor(() => {
      expect(onHostError).toHaveBeenCalledWith("overlay failed")
    })
  })

  it("gives all 6 keys class footer__key including overlay", () => {
    render(<Footer />)
    const keys = screen.getAllByRole("button").filter((key) =>
      KEY_ORDER.includes((key.textContent ?? "") as (typeof KEY_ORDER)[number]),
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
