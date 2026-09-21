import { describe, expect, it } from "bun:test"
import { VITE_FALLBACK_SNAPSHOT } from "./api"

describe("VITE_FALLBACK_SNAPSHOT parity DTO", () => {
  it("contains releaseLabel keyboardHidUnavailable themeMode", () => {
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).toBeDefined()
    expect(VITE_FALLBACK_SNAPSHOT).toHaveProperty("keyboardHidUnavailable")
    expect(VITE_FALLBACK_SNAPSHOT.themeMode).toBe("night")
  })

  it("carries the C# release label, not a placeholder", () => {
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).toBe("0.289.0-beta18")
    expect(VITE_FALLBACK_SNAPSHOT.releaseLabel).not.toBe("0.1.0")
  })
})
