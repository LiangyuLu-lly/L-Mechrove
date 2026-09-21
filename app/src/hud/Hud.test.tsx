import { cleanup, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import type { HwSnapshot } from "../lib/types"
import { HudPanel } from "./Hud"

const FAKE_TELEMETRY: HwSnapshot = {
  mqtt: "Connected",
  lighting: {
    keyboard: true,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 100,
  gpuActions: [],
  writeAllowed: true,
  hzList: ["60"],
  offeredSwitches: [],
  liquidCooling: false,
  hdrOn: true,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
  cpuTempC: 78,
  gpuTempC: 82,
  cpuRpm: 2100,
  gpuRpm: 2100,
  cpuWatt: 45,
  gpuWatt: 80,
}

describe("HudPanel", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders Default overlay temp rpm watt and not mqtt charge HDR", () => {
    render(<HudPanel snapshot={FAKE_TELEMETRY} />)

    expect(screen.getByText("CPU")).toBeTruthy()
    expect(screen.getByText("GPU")).toBeTruthy()
    expect(screen.getByText("78C")).toBeTruthy()
    expect(screen.getByText("82C")).toBeTruthy()
    expect(screen.getAllByText("2100rpm")).toHaveLength(2)
    expect(screen.getByText("45.0W")).toBeTruthy()
    expect(screen.getByText("80.0W")).toBeTruthy()
    expect(screen.queryByText("Connected")).toBeNull()
    expect(screen.queryByText("100%")).toBeNull()
    expect(screen.queryByText("HDR")).toBeNull()
  })
})
