import { cleanup, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import { Fan } from "./Fan"

describe("Fan", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders no fan-curve sliders", () => {
    render(<Fan />)
    expect(screen.queryByRole("slider")).toBeNull()
  })

  it("renders no T0–T15 duty inputs", () => {
    const { container } = render(<Fan />)
    expect(container.querySelector("input")).toBeNull()
    expect(screen.queryByLabelText("T0")).toBeNull()
    expect(screen.queryByLabelText("T15")).toBeNull()
  })
})
