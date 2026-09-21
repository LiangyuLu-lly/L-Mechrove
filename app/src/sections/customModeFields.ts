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

export const POWER_PLANS = [
  { value: "381b4222-f694-41f0-9685-ff5bb260df2e", label: "平衡" },
  { value: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", label: "高性能" },
  { value: "a1841308-3541-4fab-bc81-f71556f20b4a", label: "节能" },
  { value: "e9a42b02-d5df-448d-aa00-03f14749eb61", label: "卓越性能" },
] as const

export const BOOST_MODES = [
  { value: "0", label: "禁用睿频（CPU 不超基础频率）" },
  { value: "1", label: "启用睿频（默认，系统自动）" },
  { value: "2", label: "激进睿频（性能最强，发热最高）" },
  { value: "3", label: "效率睿频（省电优先，发热低）" },
  { value: "4", label: "高效激进（性能与省电平衡）" },
  { value: "5", label: "激进·保底（先保基准频率再加速）" },
  { value: "6", label: "高效·保底（保底 + 省电）" },
] as const

export const RESTORE_OPERATING_MODE_DETAIL = "RESTORE_OPERATING_MODE_DETAIL"

export const RESTORE_CONFIRM = "恢复当前自定义档为默认参数？"

export const DEFAULT_FAN_DUTIES: number[] = [
  0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
]

export function tableNameForProfile(index: string): string {
  return `M4T${Number(index) + 1}`
}
