import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { MoreSwitches } from "./MoreSwitches"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const MQTT_COMMANDS = [
  "set_performance_mode",
  "set_light_effect",
  "set_light_power",
  "set_gpu_route",
  "set_display_hz",
  "set_auto_refresh_rate",
  "set_fan_boost",
  "set_custom_detail",
  "set_lc_pump",
  "set_lc_fan",
  "set_calibration",
  "set_overdrive",
  "set_local_dimming",
  "set_brightness",
] as const

const WIN32_SWITCHES = [
  { key: "startup", label: "开机自启动" },
  { key: "taskbarautohide", label: "任务栏自动隐藏" },
  { key: "transparency", label: "透明效果" },
  { key: "darktheme", label: "深色主题" },
] as const

describe("MoreSwitches", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("always offers 触摸板 and omits whisper 铰链 同步", () => {
    render(<MoreSwitches offered={["touchpad", "whisper"]} />)
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    expect(screen.getByRole("checkbox", { name: "触摸板" })).toBeTruthy()
    expect(screen.queryByText("whisper")).toBeNull()
    expect(screen.queryByText("铰链")).toBeNull()
    expect(screen.queryByText("同步")).toBeNull()
  })

  it("exposes each switch as a checkbox, not 开/关 radios", () => {
    render(
      <MoreSwitches offered={["touchpad", "fanboost", "acrecovery"]} />,
    )
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    expect(screen.getByRole("checkbox", { name: "触摸板" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "风扇增强" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "来电自启" })).toBeTruthy()
    expect(screen.queryByRole("radio", { name: "开" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "关" })).toBeNull()
  })

  it("invokes set_quick_switch with touchpad true when the checkbox is clicked", () => {
    render(<MoreSwitches offered={["touchpad"]} />)
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    fireEvent.click(screen.getByRole("checkbox", { name: "触摸板" }))
    expect(invoke).toHaveBeenCalledWith("set_quick_switch", {
      key: "touchpad",
      on: true,
    })
  })

  it("labels Win32 keys 开机自启动 任务栏自动隐藏 透明效果 深色主题", () => {
    render(
      <MoreSwitches
        offered={["startup", "taskbarautohide", "transparency", "darktheme"]}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    expect(screen.getByRole("checkbox", { name: "开机自启动" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "任务栏自动隐藏" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "透明效果" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "深色主题" })).toBeTruthy()
  })

  it("renders the three C# group labels and 风扇增强 when offered spans all groups", () => {
    render(
      <MoreSwitches
        offered={["fanboost", "touchpad", "winkey", "startup", "uni"]}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    expect(screen.getByText("输入设备")).toBeTruthy()
    expect(screen.getByText("键盘与热键")).toBeTruthy()
    expect(screen.getByText("电源与系统")).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "风扇增强" })).toBeTruthy()
    expect(screen.queryByText("风扇加速")).toBeNull()
  })

  it("routes Win32 keys through set_quick_switch and never an MQTT command", () => {
    render(
      <MoreSwitches
        offered={WIN32_SWITCHES.map((item) => item.key)}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    for (const item of WIN32_SWITCHES) {
      fireEvent.click(screen.getByRole("checkbox", { name: item.label }))
    }
    expect(invoke).toHaveBeenCalledTimes(WIN32_SWITCHES.length)
    for (const item of WIN32_SWITCHES) {
      expect(invoke).toHaveBeenCalledWith("set_quick_switch", {
        key: item.key,
        on: true,
      })
    }
    const commands = invoke.mock.calls.map((call) => call[0])
    expect(commands.every((command) => command === "set_quick_switch")).toBe(
      true,
    )
    for (const mqttCommand of MQTT_COMMANDS) {
      expect(commands).not.toContain(mqttCommand)
    }
  })
})
