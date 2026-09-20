import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Screen } from "./Screen"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Screen", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders Hz options from hzList and omits calibration OD LD", () => {
    render(<Screen hzList={["60", "165"]} />)
    expect(screen.getByText("屏幕")).toBeTruthy()
    expect(screen.getByRole("radio", { name: "60 Hz" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "165 Hz" })).toBeTruthy()
    expect(screen.queryByText("校色")).toBeNull()
    expect(screen.queryByText("过驱动")).toBeNull()
    expect(screen.queryByText("局部调光")).toBeNull()
  })

  it("invokes set_display_hz with 165 when 165 Hz is clicked", () => {
    render(<Screen hzList={["60", "165"]} />)
    fireEvent.click(screen.getByRole("radio", { name: "165 Hz" }))
    expect(invoke).toHaveBeenCalledWith("set_display_hz", { hz: "165" })
  })

  it("invokes set_brightness with 80 when the slider moves to 80", () => {
    render(<Screen hzList={["60"]} />)
    const slider = screen.getByRole("slider", { name: "亮度" })
    fireEvent.change(slider, { target: { value: "80" } })
    expect(invoke).toHaveBeenCalledWith("set_brightness", { percent: 80 })
  })
})
