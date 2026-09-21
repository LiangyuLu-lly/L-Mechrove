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

  it("shows PL2 input when custom mode is active", () => {
    render(
      <PerformanceWattage
        tccAdjustable={false}
        ocSettings={false}
        customMode={true}
      />,
    )
    expect(screen.getByRole("spinbutton", { name: "PL2" })).toBeTruthy()
  })

  it("invokes set_custom_detail with PL2 and a string value when PL2 changes", () => {
    render(
      <PerformanceWattage
        tccAdjustable={false}
        ocSettings={false}
        customMode={true}
      />,
    )
    fireEvent.change(screen.getByRole("spinbutton", { name: "PL2" }), {
      target: { value: "45" },
    })
    expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
      field: "PL2",
      value: "45",
    })
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
})
