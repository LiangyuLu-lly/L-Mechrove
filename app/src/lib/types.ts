export const MQTT_STATUSES = [
  "Connecting",
  "Connected",
  "Disconnected",
  "Error",
] as const
export type MqttStatus = (typeof MQTT_STATUSES)[number]

export type LightingVisibility = {
  readonly keyboard: boolean
  readonly lightbar: boolean
  readonly logo: boolean
  readonly keyboardType: number
}

export type HwSnapshot = {
  readonly mqtt: MqttStatus
  readonly lighting: LightingVisibility
  readonly chargePercent: number
  readonly gpuActions: readonly string[]
  readonly writeAllowed: boolean
  readonly hzList: readonly string[]
  readonly offeredSwitches: readonly string[]
  readonly liquidCooling: boolean
  readonly hdrOn: boolean
  readonly tccAdjustable: boolean
  readonly ocSettings: boolean
  readonly silentTurbo: boolean
  readonly dcHzSeen: boolean
  readonly cpuTempC?: number
  readonly gpuTempC?: number
  readonly cpuRpm?: number
  readonly gpuRpm?: number
  readonly cpuWatt?: number
  readonly gpuWatt?: number
  readonly keyboardHidUnavailable: boolean
  readonly lightingOffOnBattery: boolean
  readonly lightingIdleSeconds: number
  readonly modelReason: string
  readonly projectId: string
  readonly ocRequiresElevation: boolean
  readonly themeMode: "night" | "day"
  readonly releaseLabel: string
}

export type UpdateDto = {
  readonly updateAvailable: boolean
  readonly latestVersion: string
}

export const FAN_CURVE_TYPES = ["CPU", "GPU"] as const
export type FanCurveType = (typeof FAN_CURVE_TYPES)[number]

export const PERFORMANCE_MODES = [
  "office",
  "gaming",
  "turbo",
  "silentTurbo",
  "custom",
] as const
export type PerformanceMode = (typeof PERFORMANCE_MODES)[number]
