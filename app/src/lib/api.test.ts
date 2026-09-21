import { describe, expect, it, mock } from "bun:test"
import { hwSnapshot } from "./api"
import type { HwSnapshot } from "./types"

const invoke = mock(() => Promise.reject(new Error("no host")))

mock.module("@tauri-apps/api/core", () => ({
  invoke,
}))

describe("hwSnapshot", () => {
  it("does not yield writeAllowed true when invoke rejects", async () => {
    invoke.mockImplementation(() => Promise.reject(new Error("no host")))
    let snapshot: HwSnapshot | undefined
    try {
      snapshot = await hwSnapshot()
    } catch {
      snapshot = undefined
    }
    expect(snapshot?.writeAllowed).not.toBe(true)
  })
})
