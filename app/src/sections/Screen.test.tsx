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
    expect(screen.queryByRole("combobox", { name: "屏幕校色" })).toBeNull()
    expect(screen.queryByText("校色")).toBeNull()
    expect(screen.queryByText("过驱动")).toBeNull()
    expect(screen.queryByText("局部调光")).toBeNull()
  })

  it("shows 默认/sRGB 校色 when ColorCalibration is supported", () => {
    render(<Screen hzList={["60"]} colorCalibration />)
    expect(screen.getByRole("combobox", { name: "屏幕校色" })).toBeTruthy()
    expect(screen.getByRole("option", { name: "默认" })).toBeTruthy()
    expect(screen.getByRole("option", { name: "sRGB" })).toBeTruthy()
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

  it("hides 自动刷新率 when dcHzSeen is false", () => {
    render(<Screen hzList={["60"]} dcHzSeen={false} />)
    expect(screen.queryByRole("checkbox", { name: "自动刷新率" })).toBeNull()
  })

  it("invokes set_auto_refresh_rate when 自动刷新率 is checked", () => {
    render(<Screen hzList={["60"]} dcHzSeen={true} />)
    fireEvent.click(screen.getByRole("checkbox", { name: "自动刷新率" }))
    expect(invoke).toHaveBeenCalledWith("set_auto_refresh_rate", { on: true })
  })
})
