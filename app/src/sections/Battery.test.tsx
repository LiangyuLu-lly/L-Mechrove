import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Battery } from "./Battery"

const invoke = mock(() => Promise.resolve(80))

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Battery", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("invokes set_charge_limit with 80 when the slider moves to 80", () => {
    render(<Battery percent={100} />)
    const slider = screen.getByRole("slider", { name: "限充" })
    fireEvent.change(slider, { target: { value: "80" } })
    expect(invoke).toHaveBeenCalledWith("set_charge_limit", { percent: 80 })
    expect(screen.getByText("80%")).toBeTruthy()
  })

  it("shows live percent and does not repeat 限充 as row status", () => {
    render(<Battery percent={70} />)
    expect(screen.getByText("70%")).toBeTruthy()
    expect(screen.getByRole("slider", { name: "限充" })).toBeTruthy()
    expect(screen.queryByText("限充")).toBeNull()
  })

  it("sets --slider-progress to 100% at the max charge limit", () => {
    render(<Battery percent={100} />)
    expect(
      screen.getByRole("slider", { name: "限充" }).style.getPropertyValue("--slider-progress"),
    ).toBe("100%")
  })
})
