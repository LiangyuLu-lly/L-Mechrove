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

  it("renders health and charge labels from the snapshot when present", () => {
    render(
      <Battery
        percent={70}
        health="循环 12 次"
        chargeStatus="充电: 20.0W"
      />,
    )
    expect(screen.getByText("循环 12 次")).toBeTruthy()
    expect(screen.getByText("充电: 20.0W")).toBeTruthy()
  })

  it("omits health and charge labels when absent", () => {
    render(<Battery percent={70} />)
    expect(screen.queryByText("循环 12 次")).toBeNull()
    expect(screen.queryByText("充电: 20.0W")).toBeNull()
    expect(screen.queryByText("放电: 15.0W")).toBeNull()
  })

  it("invokes set_charge_full when 充满电 is clicked", () => {
    render(<Battery percent={80} />)
    fireEvent.click(screen.getByRole("button", { name: "充满电" }))
    expect(invoke).toHaveBeenCalledWith("set_charge_full")
  })

  it("reports set_charge_limit failure through onHostError instead of swallowing it", async () => {
    const onHostError = mock(() => {})
    invoke.mockRejectedValueOnce(new Error("charge host down"))
    render(<Battery percent={100} onHostError={onHostError} />)
    fireEvent.change(screen.getByRole("slider", { name: "限充" }), {
      target: { value: "80" },
    })
    await Promise.resolve()
    await Promise.resolve()
    expect(onHostError).toHaveBeenCalledWith("charge host down")
  })

  it("reports set_charge_full failure through onHostError instead of swallowing it", async () => {
    const onHostError = mock(() => {})
    invoke.mockRejectedValueOnce(new Error("full charge host down"))
    render(<Battery percent={80} onHostError={onHostError} />)
    fireEvent.click(screen.getByRole("button", { name: "充满电" }))
    await Promise.resolve()
    await Promise.resolve()
    expect(onHostError).toHaveBeenCalledWith("full charge host down")
  })
})
