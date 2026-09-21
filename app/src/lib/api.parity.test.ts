import { describe, expect, it } from "bun:test"
import { readFileSync } from "node:fs"
import { VITE_FALLBACK_SNAPSHOT } from "./api"

const HUB_KEYS = [
  "batteryHealth",
  "chargeStatus",
  "chargeFullOffered",
  "overdrive",
  "localDimming",
  "customProfileOffered",
  "lcConnection",
  "fanCurveTableName",
  "updateAvailable",
] as const

const UI_COMMANDS = [
  "hw_snapshot",
  "set_performance_mode",
  "set_charge_limit",
  "set_charge_full",
  "set_gpu_route",
  "set_light_effect",
  "set_light_power",
  "set_display_hz",
  "set_auto_refresh_rate",
  "set_brightness",
  "set_calibration",
  "set_overdrive",
  "set_local_dimming",
  "set_fan_curve",
  "set_fan_boost",
  "set_custom_detail",
  "set_monitor_off",
  "set_quick_switch",
  "set_lc_pump",
  "set_lc_fan",
  "set_lc_connect",
  "set_lc_disconnect",
  "updates_check",
  "overlay_set",
  "diagnostics_export",
  "app_quit",
  "updates_install",
  "updates_open_page",
  "overlay_update",
  "set_theme_mode",
  "set_ui_language",
  "set_project_id",
  "set_lighting_policy",
  "open_custom_mode_window",
  "close_custom_mode_window",
] as const

describe("VITE_FALLBACK_SNAPSHOT parity DTO", () => {
  it("contains releaseLabel keyboardHidUnavailable themeMode", () => {
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).toBeDefined()
    expect(VITE_FALLBACK_SNAPSHOT).toHaveProperty("keyboardHidUnavailable")
    expect(VITE_FALLBACK_SNAPSHOT.themeMode).toBe("night")
  })

  it("carries the C# release label, not a placeholder", () => {
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).toBe("0.289.0-beta18")
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).not.toBe("0.1.0")
  })

  it("carries every hub field the UI reads", () => {
    for (const key of HUB_KEYS) {
      expect(VITE_FALLBACK_SNAPSHOT).toHaveProperty(key)
    }
  })
})

describe("api wrappers cover UI host commands", () => {
  it("exports an invoke wrapper for every command the UI calls", () => {
    const source = readFileSync(new URL("./api.ts", import.meta.url), "utf8")
    for (const command of UI_COMMANDS) {
      const invokeCall = new RegExp(
        String.raw`invoke(?:<[^>]+>)?\("${command}"`,
      )
      expect(invokeCall.test(source)).toBe(true)
    }
  })

  it("registers every UI command in generate_handler", () => {
    const lib = readFileSync(
      new URL("../../src-tauri/src/lib.rs", import.meta.url),
      "utf8",
    )
    const start = lib.indexOf("generate_handler![")
    const end = lib.indexOf("]", start)
    expect(start).toBeGreaterThanOrEqual(0)
    expect(end).toBeGreaterThan(start)
    const block = lib.slice(start, end)
    for (const command of UI_COMMANDS) {
      expect(block).toContain(command)
    }
  })
})
