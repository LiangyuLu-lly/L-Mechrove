import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, describe, expect, it, mock } from "bun:test"
import { Toast } from "./Toast"

describe("Toast", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders role=status and shows the message", () => {
    render(
      <Toast message="性能模式切换失败。" onDismiss={() => undefined} />,
    )
    const status = screen.getByRole("status")
    expect(status).toBeTruthy()
    expect(status.textContent).toContain("性能模式切换失败。")
  })

  it("makes a host error passed in visible", () => {
    const error = new Error("充电上限设置失败，已恢复原值。")
    render(<Toast message={error.message} onDismiss={() => undefined} />)
    expect(screen.getByRole("status").textContent).toContain(error.message)
  })

  it("auto-dismisses after a bounded time", async () => {
    const onDismiss = mock(() => undefined)
    render(
      <Toast
        message="灯效设置失败。"
        onDismiss={onDismiss}
        durationMs={1}
      />,
    )
    expect(onDismiss).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(onDismiss).toHaveBeenCalledTimes(1)
    })
  })

  it("can be dismissed", () => {
    const onDismiss = mock(() => undefined)
    render(<Toast message="灯效设置失败。" onDismiss={onDismiss} />)
    fireEvent.click(screen.getByRole("button", { name: "关闭" }))
    expect(onDismiss).toHaveBeenCalledTimes(1)
  })
})
