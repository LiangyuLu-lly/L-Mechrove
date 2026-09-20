import { assertNever } from "./assertNever"

export type LightChannel = "keyboard" | "lightbar" | "logo"

export type LightEffectOption = {
  readonly value: string
  readonly label: string
}

const GCU_KEYBOARD = [
  { value: "Single", label: "单色" },
  { value: "Breathing", label: "呼吸" },
  { value: "Wave", label: "波浪" },
  { value: "Reactive", label: "按键反应" },
  { value: "Rainbow", label: "彩虹" },
  { value: "Ripple", label: "涟漪" },
  { value: "Raindrop", label: "雨滴" },
  { value: "Marquee", label: "跑马灯" },
  { value: "Spark", label: "火花" },
  { value: "Aurora", label: "极光" },
  { value: "Gaming", label: "游戏" },
] as const satisfies readonly LightEffectOption[]

const SINGLE_ZONE = [
  { value: "Single", label: "单色" },
  { value: "Breathing", label: "呼吸" },
] as const satisfies readonly LightEffectOption[]

const LIGHTBAR = [
  { value: "Single", label: "单色" },
  { value: "Breathing", label: "呼吸" },
  { value: "Wave", label: "波浪" },
  { value: "Impact", label: "冲击" },
  { value: "Raindrop", label: "流星" },
] as const satisfies readonly LightEffectOption[]

const LOGO = [
  { value: "Single", label: "单色" },
  { value: "Breathing", label: "呼吸" },
  { value: "Mix", label: "混合" },
] as const satisfies readonly LightEffectOption[]

export function keyboardCatalog(
  keyboardType: number,
): readonly LightEffectOption[] {
  if (keyboardType === 1 || keyboardType === 2) {
    return SINGLE_ZONE
  }
  return GCU_KEYBOARD
}

export function lightbarCatalog(): readonly LightEffectOption[] {
  return LIGHTBAR
}

export function logoCatalog(): readonly LightEffectOption[] {
  return LOGO
}

export function catalogFor(
  channel: LightChannel,
  keyboardType: number,
): readonly LightEffectOption[] {
  switch (channel) {
    case "keyboard":
      return keyboardCatalog(keyboardType)
    case "lightbar":
      return lightbarCatalog()
    case "logo":
      return logoCatalog()
    default:
      return assertNever(channel)
  }
}
