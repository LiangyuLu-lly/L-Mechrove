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
    fireEvent.click(screen.getByRole("button", { name: "灯光" }))
    fireEvent.change(screen.getByRole("combobox", { name: "灯条" }), {
      target: { value: "Breathing" },
    })
    expect(invoke).toHaveBeenCalledWith("set_light_effect", {
      channel: "lightbar",
      effect: "Breathing",
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
    fireEvent.click(screen.getByRole("button", { name: "灯光" }))
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
    fireEvent.click(screen.getByRole("button", { name: "灯光" }))
    expect(screen.getByText("波浪")).toBeTruthy()
  })
})
