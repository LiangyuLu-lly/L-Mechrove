import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { ModelBanner } from "./ModelBanner"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("ModelBanner", () => {
  beforeEach(() => {
    invoke.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  it("is hidden when writeAllowed is true", () => {
    render(
      <ModelBanner
        writeAllowed={true}
        modelReason="unserved"
        projectId=""
      />,
    )
    expect(screen.queryByRole("status")).toBeNull()
    expect(screen.queryByLabelText("手动机型")).toBeNull()
  })

  it("shows read-only copy and modelReason when writeAllowed is false", () => {
    render(
      <ModelBanner
        writeAllowed={false}
        modelReason="unserved"
        projectId=""
      />,
    )
    const status = screen.getByRole("status")
    expect(status.textContent).toContain("机型只读")
    expect(status.textContent).toContain("无法识别机型")
    expect(status.textContent).toContain("unserved")
  })

  it("invokes setProjectId with the typed id when 应用 is clicked", () => {
    render(
      <ModelBanner
        writeAllowed={false}
        modelReason="unserved"
        projectId=""
      />,
    )
    fireEvent.change(screen.getByRole("textbox", { name: "手动机型" }), {
      target: { value: "GK7NXXR" },
    })
    fireEvent.click(screen.getByRole("button", { name: "应用" }))
    expect(invoke).toHaveBeenCalledWith("set_project_id", { id: "GK7NXXR" })
  })
})
