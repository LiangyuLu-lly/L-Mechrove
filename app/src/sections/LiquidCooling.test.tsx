import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { LiquidCooling } from "./LiquidCooling"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("LiquidCooling", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("unmounts when liquidCooling is false", () => {
    const { container } = render(<LiquidCooling liquidCooling={false} />)
    expect(container.firstChild).toBeNull()
    expect(screen.queryByText("液冷")).toBeNull()
  })

  it("invokes set_lc_pump with 2 when 高 is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("radio", { name: "高" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_pump", { index: 2 })
  })

  it("invokes set_lc_fan with 0 when 0 is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("radio", { name: "0" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_fan", { index: 0 })
  })

  it("renders 未连接 on the chip when disconnected", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    expect(screen.getByRole("button", { name: "未连接" })).toBeTruthy()
  })

  it("renders 已直连 on the chip when connection is direct", () => {
    render(<LiquidCooling liquidCooling={true} connection="direct" />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    expect(screen.getByRole("button", { name: "已直连" })).toBeTruthy()
  })

  it("renders GCU 已连接水冷 on the chip when connection is gcu", () => {
    render(<LiquidCooling liquidCooling={true} connection="gcu" />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    expect(screen.getByRole("button", { name: "GCU 已连接水冷" })).toBeTruthy()
  })

  it("invokes set_lc_connect when the 未连接 chip is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("button", { name: "未连接" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_connect")
  })

  it("invokes set_lc_disconnect when the 已直连 chip is clicked", () => {
    render(<LiquidCooling liquidCooling={true} connection="direct" />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("button", { name: "已直连" }))
    expect(invoke).toHaveBeenCalledWith("set_lc_disconnect")
  })

  it("offers 自动 on the pump in addition to 低 中 高", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    const autos = screen.getAllByRole("radio", { name: "自动" })
    expect(autos.length).toBeGreaterThanOrEqual(2)
    expect(screen.getByRole("radio", { name: "低" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "中" })).toBeTruthy()
    expect(screen.getByRole("radio", { name: "高" })).toBeTruthy()
  })

  it("invokes set_lc_fan with 4 when pump 自动 is clicked", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    const autos = screen.getAllByRole("radio", { name: "自动" })
    const pumpAuto = autos[0]
    if (pumpAuto === undefined) {
      throw new Error("pump 自动 missing")
    }
    fireEvent.click(pumpAuto)
    expect(invoke).toHaveBeenCalledWith("set_lc_fan", { index: 4 })
  })

  it("offers the C# water-cooling light effect names", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("button", { name: "水冷灯光" }))
    expect(screen.getByRole("button", { name: "青色常亮" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "青色呼吸" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "多彩" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "彩色呼吸" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "自定义颜色常亮…" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "自定义颜色呼吸…" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "风扇旋转色（Mk2）" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "风扇彩虹（Mk2）" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "关闭全部灯光" })).toBeTruthy()
  })

  it("invokes set_light_effect when the colour control changes", () => {
    render(<LiquidCooling liquidCooling={true} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("button", { name: "水冷灯光" }))
    fireEvent.change(screen.getByLabelText("自定义颜色"), {
      target: { value: "#ff0000" },
    })
    expect(invoke).toHaveBeenCalledWith("set_light_effect", {
      channel: "liquid",
      effect: "custom_static",
      color: "#ff0000",
    })
  })

  it("chip row height is 32px at 420px", () => {
    const { container } = render(
      <div style={{ width: "420px" }}>
        <LiquidCooling liquidCooling={true} />
      </div>,
    )
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    const chip = screen.getByRole("button", { name: "未连接" })
    const row = chip.closest(".row")
    if (row === null) {
      throw new Error("chip row missing")
    }
    container.style.width = "420px"
    expect(getComputedStyle(chip).height).toBe("32px")
  })

  it("reports set_lc_pump failure through onHostError instead of swallowing it", async () => {
    const onHostError = mock(() => {})
    invoke.mockRejectedValueOnce(new Error("lc host down"))
    render(<LiquidCooling liquidCooling={true} onHostError={onHostError} />)
    fireEvent.click(screen.getByRole("button", { name: "液冷" }))
    fireEvent.click(screen.getByRole("radio", { name: "高" }))
    await Promise.resolve()
    await Promise.resolve()
    expect(onHostError).toHaveBeenCalledWith("lc host down")
  })
})
