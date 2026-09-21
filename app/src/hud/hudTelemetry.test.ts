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
  colorCalibration: false,
  keyboardHidUnavailable: false,
  lightingOffOnBattery: false,
  lightingIdleSeconds: 0,
  modelReason: "",
  projectId: "",
  ocRequiresElevation: false,
  themeMode: "night",
  releaseLabel: "",
}

const DEFAULT_TELEMETRY = {
  ...BASE_SNAPSHOT,
  cpuTempC: 78,
  gpuTempC: 82,
  cpuRpm: 2100,
  gpuRpm: 2100,
  cpuWatt: 45,
  gpuWatt: 80,
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

  it("omits rpm when overlay mode is Light", () => {
    const lines = hudLinesFromSnapshot(DEFAULT_TELEMETRY, { mode: "light" })

    expect(lines[0]?.tempC).toBe("78C")
    expect(lines[0]?.watt).toBe("45.0W")
    expect(lines[0]?.rpm).toBeUndefined()
    expect(lines[1]?.rpm).toBeUndefined()
    expect(lines[0]?.usage).toBeUndefined()
  })

  it("adds usage percent when overlay mode is Full", () => {
    const lines = hudLinesFromSnapshot(
      { ...DEFAULT_TELEMETRY, cpuUsage: 40, gpuUsage: 70 },
      { mode: "full" },
    )

    expect(lines[0]?.rpm).toBe("2100rpm")
    expect(lines[0]?.usage).toBe("40%")
    expect(lines[1]?.usage).toBe("70%")
    expect(lines[0]?.mem).toBeUndefined()
    expect(lines[0]?.battery).toBeUndefined()
  })

  it("uses em dash for missing Full usage sensors", () => {
    const lines = hudLinesFromSnapshot(DEFAULT_TELEMETRY, { mode: "full" })

    expect(lines[0]?.usage).toBe("—")
    expect(lines[1]?.usage).toBe("—")
  })

  it("hides Complete fans usage ram battery names when show flags are off", () => {
    const lines = hudLinesFromSnapshot(
      {
        ...DEFAULT_TELEMETRY,
        cpuUsage: 40,
        gpuUsage: 70,
        ramUsedGb: 8.5,
        vramUsedGb: 6,
        cpuName: "Ultra 185H",
        gpuName: "RTX 4070",
      },
      {
        mode: "complete",
        showFans: false,
        showUsage: false,
        showRam: false,
        showBattery: false,
        names: false,
      },
    )

    expect(lines[0]?.tempC).toBe("78C")
    expect(lines[0]?.watt).toBe("45.0W")
    expect(lines[0]?.rpm).toBeUndefined()
    expect(lines[0]?.usage).toBeUndefined()
    expect(lines[0]?.mem).toBeUndefined()
    expect(lines[0]?.battery).toBeUndefined()
    expect(lines[0]?.name).toBeUndefined()
    expect(lines[1]?.name).toBeUndefined()
  })

  it("includes Complete ram battery and names when flags are on", () => {
    const lines = hudLinesFromSnapshot(
      {
        ...DEFAULT_TELEMETRY,
        cpuUsage: 40,
        gpuUsage: 70,
        ramUsedGb: 8.5,
        vramUsedGb: 6,
        cpuName: "Ultra 185H",
        gpuName: "RTX 4070",
      },
      { mode: "complete", names: true },
    )

    expect(lines[0]?.rpm).toBe("2100rpm")
    expect(lines[0]?.usage).toBe("40%")
    expect(lines[0]?.mem).toBe("8.5GB")
    expect(lines[1]?.mem).toBe("6.0GB")
    expect(lines[0]?.battery).toBe("100%")
    expect(lines[0]?.name).toBe("Ultra 185H")
    expect(lines[1]?.name).toBe("RTX 4070")
  })

  it("hides names in Complete unless overlay_names is on", () => {
    const lines = hudLinesFromSnapshot(
      { ...DEFAULT_TELEMETRY, cpuName: "Ultra 185H", gpuName: "RTX 4070" },
      { mode: "complete" },
    )

    expect(lines[0]?.name).toBeUndefined()
    expect(lines[1]?.name).toBeUndefined()
  })
})
