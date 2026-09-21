import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { FanCurve } from "./FanCurve"

const setFanCurve = mock(() => Promise.resolve())

mock.module("../lib/api", () => ({
  setFanCurve,
}))

const INITIAL = [
  0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
]

describe("FanCurve", () => {
  beforeEach(() => {
    setFanCurve.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("renders 16 points given initial duties", () => {
    render(<FanCurve type="CPU" duties={INITIAL} />)
    expect(screen.getAllByRole("slider")).toHaveLength(16)
    expect(screen.getByRole("slider", { name: "CPU T3 转速" })).toBeTruthy()
  })

  it("calls setFanCurve with the new duty as STRING when ArrowUp", () => {
    render(<FanCurve type="CPU" duties={INITIAL} />)
    fireEvent.keyDown(screen.getByRole("slider", { name: "CPU T3 转速" }), {
      key: "ArrowUp",
    })
    expect(setFanCurve).toHaveBeenCalled()
    const call = setFanCurve.mock.calls[0]
    expect(call?.[0]).toBe("curve")
    expect(call?.[1]).toBe("CPU")
    const duties = call?.[2]
    expect(Array.isArray(duties)).toBe(true)
    const wire = Array.isArray(duties) ? duties.map(String) : []
    expect(wire).toEqual(
      INITIAL.map((duty, index) => String(index === 3 ? duty + 1 : duty)),
    )
    expect(wire[3]).toBe("21")
  })

  it("clamps duties to 0-100", () => {
    render(<FanCurve type="CPU" duties={Array.from({ length: 16 }, () => 100)} />)
    fireEvent.keyDown(screen.getByRole("slider", { name: "CPU T0 转速" }), {
      key: "ArrowUp",
    })
    expect(
      screen.getByRole("slider", { name: "CPU T0 转速" }).getAttribute("aria-valuenow"),
    ).toBe("100")

    cleanup()
    render(<FanCurve type="CPU" duties={Array.from({ length: 16 }, () => 0)} />)
    fireEvent.keyDown(screen.getByRole("slider", { name: "CPU T15 转速" }), {
      key: "ArrowDown",
    })
    expect(
      screen
        .getByRole("slider", { name: "CPU T15 转速" })
        .getAttribute("aria-valuenow"),
    ).toBe("0")
    for (const call of setFanCurve.mock.calls) {
      const duties = call[2]
      expect(Array.isArray(duties)).toBe(true)
      if (!Array.isArray(duties)) {
        continue
      }
      for (const duty of duties) {
        const n = Number(duty)
        expect(n).toBeGreaterThanOrEqual(0)
        expect(n).toBeLessThanOrEqual(100)
      }
    }
  })
})
