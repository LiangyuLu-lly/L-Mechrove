import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { DEFAULT_FAN_DUTIES } from "./customModeFields"
import { CustomModeWindow } from "./CustomModeWindow"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const STORED_DUTIES = [
  11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
] as const

function fanCurveCalls(): readonly unknown[][] {
  return invoke.mock.calls.filter((call) => call[0] === "set_fan_curve")
}

describe("CustomModeWindow", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders the custom fields", () => {
    render(<CustomModeWindow />)
    expect(screen.getByText("自定义性能模式")).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 功耗墙 PL1 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 功耗墙 PL2 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 瞬时功耗墙 PL4 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "GPU TGP 目标 (W)" }),
    ).toBeTruthy()
    expect(screen.getByText("CPU 温度墙")).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "换挡延迟 (ms)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "核心频率偏移 (MHz)" }),
    ).toBeTruthy()
    expect(screen.getByRole("button", { name: "恢复当前档默认" })).toBeTruthy()
  })

  it("renders a 风扇曲线 control", () => {
    render(<CustomModeWindow />)
    expect(screen.getByRole("button", { name: "风扇曲线" })).toBeTruthy()
    expect(screen.queryAllByRole("slider")).toHaveLength(0)
  })

  it("shows the 16-point editor when 风扇曲线 is clicked without dumping DEFAULT_FAN_DUTIES", () => {
    render(<CustomModeWindow duties={STORED_DUTIES} />)
    fireEvent.click(screen.getByRole("button", { name: "风扇曲线" }))
    expect(screen.getAllByRole("slider")).toHaveLength(16)
    expect(
      screen.getByRole("slider", { name: "CPU T0 转速" }).getAttribute("aria-valuenow"),
    ).toBe("11")
    expect(fanCurveCalls()).toHaveLength(0)
    for (const call of fanCurveCalls()) {
      const payload = call[1]
      if (payload !== null && typeof payload === "object" && "duties" in payload) {
        expect(payload.duties).not.toEqual(DEFAULT_FAN_DUTIES)
      }
    }
  })
})
