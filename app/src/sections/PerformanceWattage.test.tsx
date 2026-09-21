import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { PerformanceWattage } from "./PerformanceWattage"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("PerformanceWattage", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("does not render custom fields on the main column when customMode is true", () => {
    render(
      <PerformanceWattage
        tccAdjustable={false}
        ocSettings={true}
        customMode={true}
      />,
    )
    expect(screen.queryByRole("spinbutton", { name: "PL2" })).toBeNull()
    expect(screen.queryByRole("spinbutton", { name: "PL4" })).toBeNull()
    expect(
      screen.queryByRole("spinbutton", { name: "CPU 功耗墙 PL2 (W)" }),
    ).toBeNull()
    expect(
      screen.queryByRole("spinbutton", { name: "GPU TGP 目标 (W)" }),
    ).toBeNull()
    expect(screen.queryByText("CPU 温度墙")).toBeNull()
    expect(screen.queryByText("换挡延迟 (ms)")).toBeNull()
    expect(screen.queryByText("核心频率偏移 (MHz)")).toBeNull()
    expect(screen.queryByRole("button", { name: "风扇曲线" })).toBeNull()
    expect(screen.queryByText("自定义性能模式")).toBeNull()
    expect(screen.getByRole("spinbutton", { name: "PL1" })).toBeTruthy()
  })

  it("omits PL2 when custom mode is inactive", () => {
    render(
      <PerformanceWattage
        tccAdjustable={false}
        ocSettings={false}
        customMode={false}
      />,
    )
    expect(screen.queryByRole("spinbutton", { name: "PL2" })).toBeNull()
  })

  it("invokes set_custom_detail with PL1 and a string value when PL1 changes", () => {
    render(
      <PerformanceWattage
        tccAdjustable={false}
        ocSettings={false}
        customMode={false}
      />,
    )
    fireEvent.change(screen.getByRole("spinbutton", { name: "PL1" }), {
      target: { value: "50" },
    })
    expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
      field: "PL1",
      value: "50",
    })
  })
})
