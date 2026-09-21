import { cleanup, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import { Segmented } from "./Segmented"

describe("Segmented", () => {
  afterEach(() => {
    cleanup()
  })

  it("uses the body face for CJK labels", () => {
    render(
      <Segmented
        value="igpu"
        options={[
          { value: "igpu", label: "集显" },
          { value: "standard", label: "标准" },
          { value: "dgpu", label: "直连" },
        ]}
        onChange={() => {
          return
        }}
      />,
    )
    expect(screen.getByRole("radiogroup").className).toBe("segmented segmented--body")
  })

  it("keeps the mono face for numeric ticks", () => {
    render(
      <Segmented
        value="60"
        options={[
          { value: "60", label: "60" },
          { value: "165", label: "165" },
        ]}
        onChange={() => {
          return
        }}
      />,
    )
    expect(screen.getByRole("radiogroup").className).toBe("segmented")
  })
})
