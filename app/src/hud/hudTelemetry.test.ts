import { describe, expect, it } from "bun:test"
import type { HwSnapshot } from "../lib/types"
import { hudLinesFromSnapshot } from "./hudTelemetry"

const BASE_SNAPSHOT: HwSnapshot = {
  mqtt: "Disconnected",
  lighting: {
    keyboard: true,
    lightbar: false,
    logo: false,
    keyboardType: 0,
  },
  chargePercent: 100,
  gpuActions: [],
  writeAllowed: true,
  hzList: ["60", "165"],
  offeredSwitches: [],
  liquidCooling: false,
  hdrOn: false,
  tccAdjustable: false,
  ocSettings: false,
  silentTurbo: false,
  dcHzSeen: false,
}

describe("hudLinesFromSnapshot", () => {
  it("maps fake Default overlay telemetry to CPU GPU temp rpm watt", () => {
    const snapshot: HwSnapshot = {
      ...BASE_SNAPSHOT,
      cpuTempC: 78,
      gpuTempC: 82,
      cpuRpm: 2100,
      gpuRpm: 2100,
      cpuWatt: 45,
      gpuWatt: 80,
    }

    const lines = hudLinesFromSnapshot(snapshot)

    expect(lines).toEqual([
      { id: "cpu", label: "CPU", tempC: "78C", rpm: "2100rpm", watt: "45.0W" },
      { id: "gpu", label: "GPU", tempC: "82C", rpm: "2100rpm", watt: "80.0W" },
    ])
  })

  it("uses em dash when overlay telemetry is absent", () => {
    const lines = hudLinesFromSnapshot(BASE_SNAPSHOT)

    expect(lines[0]).toEqual({
      id: "cpu",
      label: "CPU",
      tempC: "—",
      rpm: "—",
      watt: "—",
    })
    expect(lines[1]?.id).toBe("gpu")
    expect(lines[1]?.tempC).toBe("—")
  })
})
