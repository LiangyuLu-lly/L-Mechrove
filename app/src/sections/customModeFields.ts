export const CUSTOM_PROFILES = [
  { value: "0", label: "自定义 1" },
  { value: "1", label: "自定义 2" },
  { value: "2", label: "自定义 3" },
  { value: "3", label: "自定义 4" },
] as const

export const ON_OFF = [
  { value: "1", label: "开" },
  { value: "0", label: "关" },
] as const

export type OnOff = (typeof ON_OFF)[number]["value"]

export const CUSTOM_RESTORE_DEFAULTS: readonly (readonly [string, string])[] = [
  ["PL1", "45"],
  ["PL2", "45"],
  ["PL4", "45"],
  ["CpuTccOffsetSwitch", "0"],
  ["CpuTccOffset", "0"],
  ["GpuConfigurableTGPTarget", "0"],
  ["GpuDynamicBoostSwitch", "0"],
  ["GpuDynamicBoost", "0"],
  ["FanSwitchSpeedEnabled", "0"],
  ["FanSwitchSpeed", "0"],
  ["OverClockingSwitch", "0"],
  ["GpuCoreClockOffsetOC", "0"],
  ["GpuMemoryClockOffsetOC", "0"],
]

export const DEFAULT_FAN_DUTIES: number[] = [
  0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
]
