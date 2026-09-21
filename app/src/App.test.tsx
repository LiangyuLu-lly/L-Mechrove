import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import App from "./App"

mock.module("@tauri-apps/api/core", () => ({
  invoke: mock(() => Promise.reject(new Error("no host"))),
}))

describe("App FirstRun", () => {
  beforeEach(() => {
    window.localStorage.clear()
  })

  afterEach(() => {
    cleanup()
  })

  it("shows 设置 dialog when 前往系统页 is clicked", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "前往系统页" }))
    expect(screen.getByRole("dialog", { name: "设置" })).toBeTruthy()
  })

  it("does not show 设置 dialog when 稍后 is clicked", () => {
    render(<App />)
    fireEvent.click(screen.getByRole("button", { name: "稍后" }))
    expect(screen.queryByRole("dialog", { name: "设置" })).toBeNull()
  })
})
