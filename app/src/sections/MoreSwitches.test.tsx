import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { MoreSwitches } from "./MoreSwitches"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

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
    expect(screen.getByText("触摸板")).toBeTruthy()
    expect(screen.queryByText("whisper")).toBeNull()
    expect(screen.queryByText("铰链")).toBeNull()
    expect(screen.queryByText("同步")).toBeNull()
  })

  it("invokes set_quick_switch with touchpad true when 开 is clicked", () => {
    render(<MoreSwitches offered={["touchpad"]} />)
    fireEvent.click(screen.getByRole("button", { name: "更多开关" }))
    fireEvent.click(screen.getByRole("radio", { name: "开" }))
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
    expect(screen.getByText("开机自启动")).toBeTruthy()
    expect(screen.getByText("任务栏自动隐藏")).toBeTruthy()
    expect(screen.getByText("透明效果")).toBeTruthy()
    expect(screen.getByText("深色主题")).toBeTruthy()
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
    expect(screen.getByText("风扇增强")).toBeTruthy()
    expect(screen.queryByText("风扇加速")).toBeNull()
  })
})
