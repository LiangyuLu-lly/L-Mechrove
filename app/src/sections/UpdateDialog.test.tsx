import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, mock } from "bun:test"
import { UpdateDialog } from "./UpdateDialog"

const invoke = mock(() => Promise.resolve())

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

const LATEST = {
  updateAvailable: false,
  latestVersion: "5.56.60.26",
} as const

const AVAILABLE = {
  updateAvailable: true,
  latestVersion: "5.56.61.0",
} as const

function mockCheck(
  result: Promise<{ readonly updateAvailable: boolean; readonly latestVersion: string }>,
): void {
  invoke.mockImplementation((command: string) => {
    if (command === "updates_check") {
      return result
    }
    return Promise.resolve()
  })
}

describe("UpdateDialog", () => {
  beforeEach(() => {
    invoke.mockClear()
    mockCheck(Promise.resolve(LATEST))
  })

  afterEach(() => {
    cleanup()
  })

  it("invokes updates_check when the dialog mounts", async () => {
    render(<UpdateDialog onClose={() => undefined} />)
    expect(invoke).toHaveBeenCalledWith("updates_check")
    expect(await screen.findByText(/已是最新/)).toBeTruthy()
  })

  it("shows 已是最新 and omits 下载并安装 when no update is available", async () => {
    render(<UpdateDialog onClose={() => undefined} />)
    expect(await screen.findByText(/已是最新/)).toBeTruthy()
    expect(screen.queryByRole("button", { name: "下载并安装" })).toBeNull()
  })

  it("shows the version and 下载并安装 / 打开下载页 / 稍后 when an update exists", async () => {
    mockCheck(Promise.resolve(AVAILABLE))
    render(<UpdateDialog onClose={() => undefined} />)
    expect(await screen.findByText(/5\.56\.61\.0/)).toBeTruthy()
    expect(screen.getByRole("button", { name: "下载并安装" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "打开下载页" })).toBeTruthy()
    expect(screen.getByRole("button", { name: "稍后" })).toBeTruthy()
  })

  it("closes when 稍后 is clicked", async () => {
    mockCheck(Promise.resolve(AVAILABLE))
    const onClose = mock(() => undefined)
    render(<UpdateDialog onClose={onClose} />)
    expect(await screen.findByRole("button", { name: "下载并安装" })).toBeTruthy()
    fireEvent.click(screen.getByRole("button", { name: "稍后" }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it("invokes updates_install when 下载并安装 is clicked", async () => {
    mockCheck(Promise.resolve(AVAILABLE))
    render(<UpdateDialog onClose={() => undefined} />)
    fireEvent.click(await screen.findByRole("button", { name: "下载并安装" }))
    expect(invoke).toHaveBeenCalledWith("updates_install")
  })

  it("invokes updates_open_page when 打开下载页 is clicked", async () => {
    mockCheck(Promise.resolve(AVAILABLE))
    render(<UpdateDialog onClose={() => undefined} />)
    fireEvent.click(await screen.findByRole("button", { name: "打开下载页" }))
    expect(invoke).toHaveBeenCalledWith("updates_open_page")
  })

  it("shows the error text when updates_check fails", async () => {
    mockCheck(Promise.reject(new Error("无法连接到更新服务器")))
    render(<UpdateDialog onClose={() => undefined} />)
    expect(await screen.findByText(/无法连接到更新服务器/)).toBeTruthy()
  })
})
