import { cleanup, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import { TelemetryRow } from "./TelemetryRow"

const FAKE = {
  cpuTempC: 78,
  gpuTempC: 82,
  cpuRpm: 2100,
  gpuRpm: 2100,
  cpuWatt: 45,
  gpuWatt: 80,
} as const

describe("TelemetryRow", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders CPU and GPU labels", () => {
    render(<TelemetryRow {...FAKE} />)
    expect(screen.getByText("CPU")).toBeTruthy()
    expect(screen.getByText("GPU")).toBeTruthy()
  })

  it("renders fake fixture temp watt rpm", () => {
    const { container } = render(<TelemetryRow {...FAKE} />)
    const text = container.textContent ?? ""
    expect(text).toContain("78°C")
    expect(text).toContain("2100rpm")
    expect(text).toContain("45W")
  })

  it("renders an em dash when sensors are undefined", () => {
    render(<TelemetryRow />)
    expect(screen.getAllByText("—").length).toBeGreaterThan(0)
  })

  it("contains no slider and no input", () => {
    const { container } = render(<TelemetryRow {...FAKE} />)
    expect(screen.queryByRole("slider")).toBeNull()
    expect(container.querySelector("input")).toBeNull()
  })
})
