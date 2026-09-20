import { fireEvent, render, screen } from "@testing-library/react"
import { beforeEach, describe, expect, it, mock } from "bun:test"
import { Battery } from "./Battery"

const invoke = mock(() => Promise.resolve(80))

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Battery", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  it("invokes set_charge_limit with 80 when the slider moves to 80", () => {
    render(<Battery percent={100} />)
    const slider = screen.getByRole("slider", { name: "限充" })
    fireEvent.change(slider, { target: { value: "80" } })
    expect(invoke).toHaveBeenCalledWith("set_charge_limit", { percent: 80 })
    expect(screen.getByText("80%")).toBeTruthy()
  })
})
