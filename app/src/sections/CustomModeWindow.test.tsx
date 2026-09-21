import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { DEFAULT_FAN_DUTIES } from "./customModeFields"
import { CustomModeWindow } from "./CustomModeWindow"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const STORED_DUTIES = [
  11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
] as const

const GPU_DUTIES = [
  31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46,
] as const

const HIGH_PERF_PLAN = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"

function fanCurveCalls(): readonly unknown[][] {
  return invoke.mock.calls.filter((call) => call[0] === "set_fan_curve")
}

function customDetailCalls(): readonly { field: string; value: string }[] {
  return invoke.mock.calls.flatMap((call) => {
    if (call[0] !== "set_custom_detail") {
      return []
    }
    const payload = call[1]
    if (
      payload !== null &&
      typeof payload === "object" &&
      "field" in payload &&
      "value" in payload &&
      typeof payload.field === "string" &&
      typeof payload.value === "string"
    ) {
      return [{ field: payload.field, value: payload.value }]
    }
    return []
  })
}

describe("CustomModeWindow", () => {
  beforeEach(() => {
    invoke.mockReset()
    invoke.mockImplementation(() => Promise.resolve())
  })

  afterEach(() => {
    cleanup()
  })

  it("renders the custom fields", () => {
    render(<CustomModeWindow />)
    expect(screen.getByText("自定义性能模式")).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 功耗墙 PL1 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 功耗墙 PL2 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "CPU 瞬时功耗墙 PL4 (W)" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "GPU TGP 目标 (W)" }),
    ).toBeTruthy()
    fireEvent.click(screen.getByRole("button", { name: /温度与风扇/ }))
    expect(screen.getByText("CPU 温度墙")).toBeTruthy()
    expect(
      screen.getByRole("spinbutton", { name: "换挡延迟 (ms)" }),
    ).toBeTruthy()
    expect(
      screen.queryByRole("spinbutton", { name: "核心频率偏移 (MHz)" }),
    ).toBeNull()
    expect(screen.getByRole("button", { name: "恢复当前档默认" })).toBeTruthy()
  })

  it("renders a 风扇曲线 control", () => {
    render(<CustomModeWindow />)
    expect(screen.getByRole("button", { name: "风扇曲线" })).toBeTruthy()
    expect(screen.queryAllByRole("slider")).toHaveLength(0)
  })

  it("shows the 16-point editor when 风扇曲线 is clicked without dumping DEFAULT_FAN_DUTIES", () => {
    render(<CustomModeWindow duties={STORED_DUTIES} />)
    fireEvent.click(screen.getByRole("button", { name: "风扇曲线" }))
    expect(screen.getAllByRole("slider")).toHaveLength(32)
    expect(
      screen.getByRole("slider", { name: "CPU T0 转速" }).getAttribute("aria-valuenow"),
    ).toBe("11")
    expect(fanCurveCalls()).toHaveLength(0)
    for (const call of fanCurveCalls()) {
      const payload = call[1]
      if (payload !== null && typeof payload === "object" && "duties" in payload) {
        expect(payload.duties).not.toEqual(DEFAULT_FAN_DUTIES)
      }
    }
  })

  it("hides the OC row when opened without a snapshot grant", () => {
    render(<CustomModeWindow />)
    expect(screen.queryByText("GPU 超频")).toBeNull()
    expect(
      screen.queryByRole("spinbutton", { name: "核心频率偏移 (MHz)" }),
    ).toBeNull()
  })

  it("shows the OC row when snapshot.ocSettings is true", async () => {
    invoke.mockImplementation((cmd: unknown) => {
      if (cmd === "hw_snapshot") {
        return Promise.resolve({ ocSettings: true })
      }
      return Promise.resolve()
    })
    render(<CustomModeWindow />)
    await waitFor(() => {
      expect(
        screen.getByRole("spinbutton", { name: "核心频率偏移 (MHz)" }),
      ).toBeTruthy()
    })
  })

  it("renders 电源计划 with C# option names and invokes the host", () => {
    render(<CustomModeWindow />)
    const combo = screen.getByRole("combobox", { name: "电源计划" })
    expect(screen.getByRole("option", { name: "平衡" })).toBeTruthy()
    expect(screen.getByRole("option", { name: "高性能" })).toBeTruthy()
    expect(screen.getByRole("option", { name: "节能" })).toBeTruthy()
    expect(screen.getByRole("option", { name: "卓越性能" })).toBeTruthy()
    fireEvent.change(combo, { target: { value: HIGH_PERF_PLAN } })
    expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
      field: "PowerPlan",
      value: HIGH_PERF_PLAN,
    })
  })

  it("renders 睿频模式 with C# option names and invokes the host", () => {
    render(<CustomModeWindow />)
    const combo = screen.getByRole("combobox", { name: "睿频模式" })
    expect(
      screen.getByRole("option", { name: "禁用睿频（CPU 不超基础频率）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "启用睿频（默认，系统自动）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "激进睿频（性能最强，发热最高）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "效率睿频（省电优先，发热低）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "高效激进（性能与省电平衡）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "激进·保底（先保基准频率再加速）" }),
    ).toBeTruthy()
    expect(
      screen.getByRole("option", { name: "高效·保底（保底 + 省电）" }),
    ).toBeTruthy()
    fireEvent.change(combo, { target: { value: "2" } })
    expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
      field: "BoostMode",
      value: "2",
    })
  })

  it("invokes the restore command after confirm, not a zero-write", async () => {
    const confirm = mock(() => true)
    window.confirm = confirm
    render(<CustomModeWindow tableName="M4T1" />)
    fireEvent.click(screen.getByRole("button", { name: "恢复当前档默认" }))
    expect(confirm).toHaveBeenCalled()
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("set_custom_detail", {
        field: "RESTORE_OPERATING_MODE_DETAIL",
        value: "M4T1",
      })
    })
    expect(
      customDetailCalls().filter(
        (call) => call.field !== "RESTORE_OPERATING_MODE_DETAIL",
      ),
    ).toEqual([])
  })

  it("does not restore when confirm is cancelled", () => {
    window.confirm = mock(() => false)
    render(<CustomModeWindow tableName="M4T1" />)
    fireEvent.click(screen.getByRole("button", { name: "恢复当前档默认" }))
    expect(
      customDetailCalls().some(
        (call) => call.field === "RESTORE_OPERATING_MODE_DETAIL",
      ),
    ).toBe(false)
  })

  it("renders CPU and GPU fan charts and saving CPU does not overwrite GPU", () => {
    render(
      <CustomModeWindow duties={STORED_DUTIES} gpuDuties={GPU_DUTIES} />,
    )
    fireEvent.click(screen.getByRole("button", { name: "风扇曲线" }))
    expect(screen.getByRole("slider", { name: "CPU T0 转速" })).toBeTruthy()
    expect(screen.getByRole("slider", { name: "GPU T0 转速" })).toBeTruthy()
    expect(
      screen.getByRole("slider", { name: "GPU T0 转速" }).getAttribute("aria-valuenow"),
    ).toBe("31")
    fireEvent.keyDown(screen.getByRole("slider", { name: "CPU T0 转速" }), {
      key: "ArrowUp",
    })
    expect(
      screen.getByRole("slider", { name: "GPU T0 转速" }).getAttribute("aria-valuenow"),
    ).toBe("31")
    const gpuWrites = fanCurveCalls().filter((call) => {
      const payload = call[1]
      return (
        payload !== null &&
        typeof payload === "object" &&
        "ty" in payload &&
        payload.ty === "GPU"
      )
    })
    expect(gpuWrites).toHaveLength(0)
  })

  it("renders the verdict row when the host supplies it", () => {
    render(
      <CustomModeWindow powerWallVerdict="功耗墙生效中（60s 均值 40.0W / 上限 45W）" />,
    )
    expect(screen.getByRole("status", { name: "功耗墙实测" }).textContent).toBe(
      "功耗墙生效中（60s 均值 40.0W / 上限 45W）",
    )
  })

  it("omits the verdict row when the host supplies none", () => {
    render(<CustomModeWindow />)
    expect(screen.queryByRole("status", { name: "功耗墙实测" })).toBeNull()
  })

  it("groups fields under 功耗 / 温度与风扇 / 超频", () => {
    render(<CustomModeWindow ocSettings={true} />)
    expect(screen.getByRole("button", { name: /功耗/ })).toBeTruthy()
    expect(screen.getByRole("button", { name: /温度与风扇/ })).toBeTruthy()
    expect(screen.getByRole("button", { name: /超频/ })).toBeTruthy()
  })
})
