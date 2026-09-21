import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Gpu } from "./Gpu"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Gpu", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("shows dash and omits 自动 when no actions are offered", () => {
    render(<Gpu actions={[]} />)
    expect(screen.getByText("显卡模式")).toBeTruthy()
    expect(screen.getByText("—")).toBeTruthy()
    expect(screen.queryByText("自动")).toBeNull()
  })

  it("renders 集显/标准/直连 given igpu/standard/dgpu, not raw action names", () => {
    render(<Gpu actions={["igpu", "standard", "dgpu"]} />)
    expect(screen.getByRole("radio", { name: "集显" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "标准" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "直连" })).toBeTruthy()
    expect(screen.queryByRole("radio", { name: "igpu" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "standard" })).toBeNull()
    expect(screen.queryByRole("radio", { name: "dgpu" })).toBeNull()
    expect(screen.queryByText("自动")).toBeNull()
  })

  it("invokes set_gpu_route with TOGGLE_ON when 直连 is clicked", () => {
    render(<Gpu actions={["DGPU_DIRECT_CONNECT_TOGGLE_ON"]} />)
    fireEvent.click(screen.getByRole("radio", { name: "直连" }))
    expect(invoke).toHaveBeenCalledWith("set_gpu_route", {
      action: "DGPU_DIRECT_CONNECT_TOGGLE_ON",
    })
  })

  it("never renders 自动 even if AUTO is in the action list", () => {
    render(
      <Gpu
        actions={[
          "DGPU_DIRECT_CONNECT_TOGGLE_ON",
          "IGPU_ONLY_CONNECT_RB_AUTO",
        ]}
      />,
    )
    expect(screen.queryByText("自动")).toBeNull()
    expect(screen.getByRole("radio", { name: "直连" })).toBeTruthy()
  })
})
