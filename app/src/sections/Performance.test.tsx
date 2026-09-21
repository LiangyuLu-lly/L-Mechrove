import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Performance } from "./Performance"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Performance", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("shows 静音模式 and 平衡模式 by default", () => {
    render(<Performance />)
    expect(screen.getByRole("radio", { name: "静音模式" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "平衡模式" })).toBeTruthy()
  })

  it("invokes set_performance_mode with office when 静音模式 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "静音模式" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "office" })
    expect(invoke).not.toHaveBeenCalledWith("set_performance_mode", {
      mode: "silentTurbo",
    })
  })

  it("omits 静音狂暴 when silentTurbo is false", () => {
    render(<Performance silentTurbo={false} />)
    expect(screen.queryByRole("radio", { name: "静音狂暴" })).toBeNull()
  })

  it("shows 静音狂暴 when silentTurbo is true", () => {
    render(<Performance silentTurbo={true} />)
    expect(screen.getByRole("radio", { name: "静音狂暴" })).toBeTruthy()
  })

  it("invokes set_performance_mode with turbo when 狂暴 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "狂暴" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "turbo" })
  })

  it("invokes set_performance_mode with custom when 自定义 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "自定义" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "custom" })
  })

  it("invokes set_custom_detail with PL1 string when PL1 changes", () => {
    render(<Performance />)
    fireEvent.change(screen.getByRole("spinbutton", { name: "PL1" }), {
      target: { value: "50" },
    })
    expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
      field: "PL1",
      value: "50",
    })
  })

  it("omits TCC row when tccAdjustable is false", () => {
    render(<Performance tccAdjustable={false} />)
    expect(screen.queryByText("TCC")).toBeNull()
    expect(screen.queryByLabelText("TCC")).toBeNull()
  })
})
