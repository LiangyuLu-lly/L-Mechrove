import { cleanup, render, screen } from "@testing-library/react"
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

  it("offers no override input or 应用 when writeAllowed is false", () => {
    render(
      <ModelBanner
        writeAllowed={false}
        modelReason="unserved"
        projectId=""
      />,
    )
    expect(screen.queryByLabelText("手动机型")).toBeNull()
    expect(screen.queryByRole("textbox")).toBeNull()
    expect(screen.queryByRole("button", { name: "应用" })).toBeNull()
    expect(invoke).not.toHaveBeenCalled()
    expect(invoke.mock.calls.some((call) => call[0] === "set_project_id")).toBe(
      false,
    )
  })
})
