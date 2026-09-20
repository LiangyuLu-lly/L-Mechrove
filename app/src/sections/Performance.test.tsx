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

  it("invokes set_performance_mode with turbo when 狂暴 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "狂暴" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "turbo" })
  })

  it("invokes set_performance_mode with silentTurbo when 静音 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "静音" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", {
      mode: "silentTurbo",
    })
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
