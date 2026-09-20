import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Fan } from "./Fan"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const DUTIES = [
  0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
]

describe("Fan", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders CPU and GPU segments", () => {
    render(<Fan />)
    expect(screen.getByText("风扇")).toBeTruthy()
    expect(screen.getByRole("radio", { name: "CPU" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "GPU" })).toBeTruthy()
  })

  it("invokes set_fan_curve with 16 duties when GPU is clicked", () => {
    render(<Fan />)
    fireEvent.click(screen.getByRole("radio", { name: "GPU" }))
    expect(invoke).toHaveBeenCalledWith("set_fan_curve", {
      name: "curve",
      ty: "GPU",
      duties: DUTIES,
    })
  })

  it("invokes set_fan_boost when 加速 is clicked", () => {
    render(<Fan />)
    fireEvent.click(screen.getByRole("radio", { name: "加速" }))
    expect(invoke).toHaveBeenCalledWith("set_fan_boost", { on: true })
  })
})
