import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it } from "bun:test"
import { Collapse } from "./Collapse"

describe("Collapse", () => {
  afterEach(() => {
    cleanup()
  })

  it("does not render closed content at all, so no CSS can paint it", () => {
    render(
      <Collapse name="更多开关">
        <p>触摸板</p>
      </Collapse>,
    )

    expect(screen.queryByText("触摸板")).toBeNull()
  })

  it("renders content once the header is clicked", () => {
    render(
      <Collapse name="更多开关">
        <p>触摸板</p>
      </Collapse>,
    )

    fireEvent.click(screen.getByRole("button", { name: /更多开关/ }))

    expect(screen.getByText("触摸板")).toBeTruthy()
  })

  it("keeps defaultOpen groups rendered", () => {
    render(
      <Collapse name="灯光" defaultOpen={true}>
        <p>键盘</p>
      </Collapse>,
    )

    expect(screen.getByText("键盘")).toBeTruthy()
  })

  it("paints 0 body height when collapsed — no .collapse__body in DOM", () => {
    render(
      <Collapse name="更多开关">
        <p>触摸板</p>
      </Collapse>,
    )

    expect(document.querySelector(".collapse__body")).toBeNull()
  })

  it("removes .collapse__body from DOM when toggled closed", () => {
    render(
      <Collapse name="灯光" defaultOpen={true}>
        <p>键盘</p>
      </Collapse>,
    )

    expect(document.querySelector(".collapse__body")).not.toBeNull()

    fireEvent.click(screen.getByRole("button", { name: /灯光/ }))

    expect(document.querySelector(".collapse__body")).toBeNull()
  })
})
