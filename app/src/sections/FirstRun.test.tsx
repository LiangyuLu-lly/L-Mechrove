import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { FirstRun } from "./FirstRun"

const invoke = mock(() => Promise.resolve({ requiresPrompt: false }))
const openUrl = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

mock.module("@tauri-apps/plugin-opener", () => ({
  openUrl,
}))

function leftoverStatus(): { readonly leftoverBridge: boolean; readonly vendorUi: boolean; readonly requiresPrompt: boolean } {
  return { leftoverBridge: true, vendorUi: false, requiresPrompt: true }
}

describe("FirstRun leftover console prompt", () => {
  beforeEach(() => {
    invoke.mockReset()
    openUrl.mockReset()
    openUrl.mockImplementation(() => Promise.resolve())
    invoke.mockImplementation(() => Promise.resolve({ requiresPrompt: false }))
  })

  afterEach(() => {
    cleanup()
  })

  it("omits the apps-settings button when nothing is left over", async () => {
    render(<FirstRun onLater={() => undefined} onGoSystem={() => undefined} />)
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("gcu_coexistence_status")
    })
    expect(screen.queryByRole("button", { name: "打开应用设置" })).toBeNull()
  })

  it("shows the apps-settings button when a leftover is detected", async () => {
    invoke.mockImplementation(() => Promise.resolve(leftoverStatus()))
    render(<FirstRun onLater={() => undefined} onGoSystem={() => undefined} />)
    expect(await screen.findByRole("button", { name: "打开应用设置" })).toBeTruthy()
  })

  it("opens ms-settings:appsfeatures when the apps-settings button is clicked", async () => {
    invoke.mockImplementation(() => Promise.resolve(leftoverStatus()))
    render(<FirstRun onLater={() => undefined} onGoSystem={() => undefined} />)
    fireEvent.click(await screen.findByRole("button", { name: "打开应用设置" }))
    expect(openUrl).toHaveBeenCalledWith("ms-settings:appsfeatures")
  })

  it("does not invoke a process-kill or leftover-removal command", async () => {
    invoke.mockImplementation(() => Promise.resolve(leftoverStatus()))
    render(<FirstRun onLater={() => undefined} onGoSystem={() => undefined} />)
    fireEvent.click(await screen.findByRole("button", { name: "打开应用设置" }))
    const commands = invoke.mock.calls.map((call) => call[0])
    expect(commands).toEqual(["gcu_coexistence_status"])
  })
})
