import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { SettingsDialog } from "./SettingsDialog"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("SettingsDialog", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("omits migrated 校色 局部调光 PL1 熄屏 and 跟随系统", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.queryByText("校色")).toBeNull()
    expect(screen.queryByRole("radio", { name: "sRGB" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "P3" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "Adobe" })).toBeNull()
    expect(screen.queryByText("局部调光")).toBeNull()
    expect(screen.queryByRole("spinbutton", { name: "PL1" })).toBeNull()
    expect(screen.queryByText("PL1")).toBeNull()
    expect(screen.queryByText("熄屏")).toBeNull()
    expect(screen.queryByRole("button", { name: "立即熄屏" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "跟随系统" })).toBeNull()
    expect(screen.queryByText("跟随系统")).toBeNull()
    expect(screen.queryByText("显示")).toBeNull()
  })

  it("renders 外观 theme 日间/夜间 and 语言 中文/English", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.getByText("外观")).toBeTruthy()
    expect(screen.getByRole("radio", { name: "日间" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "夜间" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "中文" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "English" })).toBeTruthy()
  })

  it("does not offer English as a selectable language", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    const english = screen.queryByRole("radio", { name: /English/ })
    if (english === null) {
      expect(english).toBeNull()
      return
    }
    expect((english as HTMLButtonElement).disabled).toBe(true)
    const accessibleName = english.getAttribute("aria-label") ?? english.textContent ?? ""
    const reason = screen.queryByText(/尚无字符串表|暂不可用/)
    expect(
      accessibleName.includes("尚无字符串表") ||
        accessibleName.includes("暂不可用") ||
        reason !== null,
    ).toBe(true)
  })

  it("caps --settings-width at the main window width", async () => {
    const css = await Bun.file(new URL("../App.css", import.meta.url)).text()
    const tauri = (await Bun.file(
      new URL("../../src-tauri/tauri.conf.json", import.meta.url),
    ).json()) as {
      readonly app: {
        readonly windows: readonly {
          readonly label?: string
          readonly width: number
        }[]
      }
    }
    const main = tauri.app.windows.find((window) => window.label === "main")
    expect(main).toBeDefined()
    const token = css.match(/--settings-width:\s*([^;]+)/)
    expect(token).toBeTruthy()
    const px = Number.parseFloat(token?.[1]?.trim() ?? "")
    expect(Number.isFinite(px)).toBe(true)
    expect(px).toBeLessThanOrEqual(main?.width ?? 0)
    expect(css).toMatch(/\.settings-dialog__panel\s*\{[^}]*max-width:\s*100%/)
  })

  it("invokes set_theme_mode when 日间 is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: "日间" }))
    expect(invoke).toHaveBeenCalledWith("set_theme_mode", { mode: "day" })
  })

  it("shows the host error when set_theme_mode rejects", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "set_theme_mode") {
        return Promise.reject(new Error("主题未能保存"))
      }
      return Promise.resolve()
    })
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: "日间" }))
    expect(await screen.findByText("主题未能保存")).toBeTruthy()
  })

  it("does not invoke set_ui_language when English is clicked", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("radio", { name: /English/ }))
    expect(invoke).not.toHaveBeenCalledWith("set_ui_language", { code: "en" })
  })

  it("renders overlay prefs standalone without an overlay prop", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    expect(screen.getByRole("checkbox", { name: "仅游戏显示" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "熄屏挂起" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "温度" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "风扇" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "功耗" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "占用" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "内存" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "电池" })).toBeTruthy()
    expect(screen.getByRole("checkbox", { name: "名称" })).toBeTruthy()
  })

  it("persists gameOnly through overlayUpdate from overlay prefs", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("checkbox", { name: "仅游戏显示" }))
    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { gameOnly: true },
    })
  })

  it("persists displayOff through overlayUpdate from overlay prefs", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("checkbox", { name: "熄屏挂起" }))
    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { displayOff: true },
    })
  })

  it("persists per-block 风扇 through overlayUpdate from overlay prefs", () => {
    render(<SettingsDialog hdrOn={false} onClose={() => undefined} />)
    fireEvent.click(screen.getByRole("checkbox", { name: "风扇" }))
    expect(invoke).toHaveBeenCalledWith("overlay_update", {
      prefs: { showFans: false },
    })
  })

  it("seeds overlay checkboxes from the overlay prop", () => {
    render(
      <SettingsDialog
        hdrOn={false}
        onClose={() => undefined}
        overlay={{ gameOnly: true, names: true, showFans: false }}
      />,
    )
    expect(
      (screen.getByRole("checkbox", { name: "仅游戏显示" }) as HTMLInputElement)
        .checked,
    ).toBe(true)
    expect(
      (screen.getByRole("checkbox", { name: "名称" }) as HTMLInputElement)
        .checked,
    ).toBe(true)
    expect(
      (screen.getByRole("checkbox", { name: "风扇" }) as HTMLInputElement)
        .checked,
    ).toBe(false)
  })
})
