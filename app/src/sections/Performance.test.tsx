import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Performance } from "./Performance"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Performance", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("shows 静音模式 and 平衡模式 by default", () => {
    render(<Performance />)
    expect(screen.getByRole("radio", { name: "静音模式" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "平衡模式" })).toBeTruthy()
  })

  it("invokes set_performance_mode with office when 静音模式 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "静音模式" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "office" })
    expect(invoke).not.toHaveBeenCalledWith("set_performance_mode", {
      mode: "silentTurbo",
    })
  })

  it("omits 静音狂暴 when silentTurbo is false", () => {
    render(<Performance silentTurbo={false} />)
    expect(screen.queryByRole("radio", { name: "静音狂暴" })).toBeNull()
  })

  it("shows 静音狂暴 when silentTurbo is true", () => {
    render(<Performance silentTurbo={true} />)
    expect(screen.getByRole("radio", { name: "静音狂暴" })).toBeTruthy()
  })

  it("invokes set_performance_mode with turbo when 狂暴 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "狂暴" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "turbo" })
  })

  it("invokes set_performance_mode with custom when 自定义 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "自定义" }))
    expect(invoke).toHaveBeenCalledWith("set_performance_mode", { mode: "custom" })
  })

  it("opens the custom-mode window when 自定义 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "自定义" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window", {
        mode: "custom",
      })
    })
  })

  it("opens the editor when 静音模式 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "静音模式" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window", {
        mode: "office",
      })
    })
  })

  it("opens the editor when 平衡模式 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "平衡模式" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window", {
        mode: "gaming",
      })
    })
  })

  it("opens the editor when 狂暴 is clicked", async () => {
    render(<Performance />)
    fireEvent.click(screen.getByRole("radio", { name: "狂暴" }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("open_custom_mode_window", {
        mode: "turbo",
      })
    })
  })

  it("keeps the main column free of the custom editor", () => {
    render(<Performance tccAdjustable={true} ocSettings={true} />)
    expect(screen.queryByRole("spinbutton")).toBeNull()
    expect(screen.queryByText("PL1")).toBeNull()
    expect(screen.queryByText("TCC")).toBeNull()
  })

  it("reports a host failure through onHostError instead of swallowing it", async () => {
    const onHostError = mock(() => {})
    invoke.mockRejectedValueOnce(new Error("host down"))
    render(<Performance onHostError={onHostError} />)
    fireEvent.click(screen.getByRole("radio", { name: "狂暴" }))
    await Promise.resolve()
    await Promise.resolve()
    expect(onHostError).toHaveBeenCalledWith("host down")
  })
})
