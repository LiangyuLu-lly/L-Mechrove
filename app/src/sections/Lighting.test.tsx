import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { Lighting } from "./Lighting"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("Lighting", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("omits 灯条 and Logo when lightbar and logo are false", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
      />,
    )

    expect(screen.queryByText("灯条")).toBeNull()
    expect(screen.queryByText("Logo")).toBeNull()
    expect(screen.getByText("键盘")).toBeTruthy()
  })

  it("omits 铰链 and 同步", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: true,
          logo: true,
          keyboardType: 0,
        }}
      />,
    )
    expect(screen.queryByText("铰链")).toBeNull()
    expect(screen.queryByText("同步")).toBeNull()
  })

  it("invokes set_light_effect with lightbar Breathing when 呼吸 is selected", () => {
    render(
      <Lighting
        lighting={{
          keyboard: false,
          lightbar: true,
          logo: false,
          keyboardType: 0,
        }}
      />,
    )
    fireEvent.change(screen.getByRole("combobox", { name: "灯条" }), {
      target: { value: "Breathing" },
    })
    expect(invoke).toHaveBeenCalledWith("set_light_effect", {
      channel: "lightbar",
      effect: "Breathing",
      light: "4",
      speed: "1",
    })
  })

  it("does not render 波浪 when keyboardType is 1", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 1,
        }}
      />,
    )
    expect(screen.queryByText("波浪")).toBeNull()
  })

  it("renders 波浪 when keyboardType is 0", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
      />,
    )
    expect(screen.getByText("波浪")).toBeTruthy()
  })

  it("keyboard row has 电源 control", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
      />,
    )
    expect(screen.getByRole("checkbox", { name: "键盘电源" })).toBeTruthy()
  })

  it("编辑 reveals 亮度", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "编辑" }))
    expect(screen.getByText("亮度")).toBeTruthy()
  })

  it("灯光 group header is expanded at mount and still shows the keyboard caption", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
        keyboardHidUnavailable
      />,
    )
    expect(screen.getByRole("button", { name: "灯光" }).getAttribute("aria-expanded")).toBe(
      "true",
    )
    expect(screen.getByText(/本机控制器不支持软件灯效控制/)).toBeTruthy()
  })

  it("shows 本机控制器不支持软件灯效控制 when keyboardHidUnavailable is true", () => {
    render(
      <Lighting
        lighting={{
          keyboard: true,
          lightbar: false,
          logo: false,
          keyboardType: 0,
        }}
        keyboardHidUnavailable
      />,
    )
    expect(screen.getByText(/本机控制器不支持软件灯效控制/)).toBeTruthy()
  })

    it("omits 本机控制器不支持软件灯效控制 when keyboardHidUnavailable is false", () => {
      render(
        <Lighting
          lighting={{
            keyboard: true,
            lightbar: false,
            logo: false,
            keyboardType: 0,
          }}
          keyboardHidUnavailable={false}
        />,
      )
      expect(screen.queryByText(/本机控制器不支持软件灯效控制/)).toBeNull()
    })

    it("renders 离电自动关闭全部灯效 in the 灯光 group", () => {
      render(
        <Lighting
          lighting={{
            keyboard: true,
            lightbar: false,
            logo: false,
            keyboardType: 0,
          }}
        />,
      )
      expect(screen.getByRole("checkbox", { name: "离电自动关闭全部灯效" })).toBeTruthy()
    })

    it("renders the C# lighting idle duration options", () => {
      render(
        <Lighting
          lighting={{
            keyboard: true,
            lightbar: false,
            logo: false,
            keyboardType: 0,
          }}
        />,
      )
      const combo = screen.getByRole("combobox", { name: "灯光睡眠时间" })
      const options = [...combo.querySelectorAll("option")].map((option) => ({
        label: option.textContent,
        value: option.getAttribute("value"),
      }))
      expect(options).toEqual([
        { label: "关闭", value: "0" },
        { label: "10 秒（软件）", value: "10" },
        { label: "10 分钟", value: "600" },
        { label: "15 分钟", value: "900" },
        { label: "20 分钟", value: "1200" },
        { label: "30 分钟", value: "1800" },
        { label: "45 分钟", value: "2700" },
        { label: "1 小时", value: "3600" },
        { label: "2 小时", value: "7200" },
      ])
    })

    it("invokes set_lighting_policy when 离电自动关闭全部灯效 is toggled", () => {
      render(
        <Lighting
          lighting={{
            keyboard: true,
            lightbar: false,
            logo: false,
            keyboardType: 0,
          }}
          lightingIdleSeconds={600}
        />,
      )
      fireEvent.click(screen.getByRole("checkbox", { name: "离电自动关闭全部灯效" }))
      expect(invoke).toHaveBeenCalledWith("set_lighting_policy", {
        offOnBattery: true,
        idleSeconds: 600,
      })
    })

    it("invokes set_lighting_policy when an idle duration is chosen", () => {
      render(
        <Lighting
          lighting={{
            keyboard: true,
            lightbar: false,
            logo: false,
            keyboardType: 0,
          }}
          lightingOffOnBattery
        />,
      )
      fireEvent.change(screen.getByRole("combobox", { name: "灯光睡眠时间" }), {
        target: { value: "1800" },
      })
      expect(invoke).toHaveBeenCalledWith("set_lighting_policy", {
        offOnBattery: true,
        idleSeconds: 1800,
      })
    })
  })
