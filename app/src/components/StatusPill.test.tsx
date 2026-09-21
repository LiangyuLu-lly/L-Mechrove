import { cleanup, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import { StatusPill } from "./StatusPill"
import type { MqttStatus } from "../lib/types"

describe("StatusPill", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders Connecting as ● GCU 连接中", () => {
    render(<StatusPill mqtt="Connecting" />)
    expect(screen.getByText("● GCU 连接中")).toBeTruthy()
  })

  it("renders Connected as ● GCU 已连接", () => {
    render(<StatusPill mqtt="Connected" />)
    expect(screen.getByText("● GCU 已连接")).toBeTruthy()
  })

  it("renders Disconnected as ● GCU 未连接", () => {
    render(<StatusPill mqtt="Disconnected" />)
    expect(screen.getByText("● GCU 未连接")).toBeTruthy()
  })

  it("renders Error with the same copy as Disconnected and never GCU 错误", () => {
    const disconnected = render(<StatusPill mqtt="Disconnected" />)
    const disconnectedCopy = disconnected.container.textContent
    cleanup()
    const error = render(<StatusPill mqtt="Error" />)
    expect(error.container.textContent).toBe(disconnectedCopy)
    expect(error.container.textContent).toContain("●")
    expect(error.container.textContent).toContain("GCU 未连接")
    expect(error.container.textContent).not.toContain("GCU 错误")
  })

  it("renders a leading ● on every GCU status label", () => {
    const statuses = [
      "Connecting",
      "Connected",
      "Disconnected",
      "Error",
    ] as const satisfies readonly MqttStatus[]
    for (const mqtt of statuses) {
      const view = render(<StatusPill mqtt={mqtt} />)
      expect(view.container.textContent).toContain("●")
      cleanup()
    }
  })
})
