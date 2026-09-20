import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { LiquidCooling } from "./LiquidCooling"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("LiquidCooling", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("unmounts when liquidCooling is false", () => {
    const { container } = render(<LiquidCooling liquidCooling={false} />)
    expect(container.firstChild).toBeNull()
    expect(screen.queryByText("液冷")).toBeNull()
  })

  it("invokes set_lc_pump with 2 when 高 is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("radio", { name: "高" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_pump", { index: 2 })
  })

  it("invokes set_lc_fan with 0 when 0 is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("radio", { name: "0" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_fan", { index: 0 })
  })
})
