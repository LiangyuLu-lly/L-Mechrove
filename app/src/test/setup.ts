import { vi } from "vitest"

vi.mock("@tauri-apps/api/core", () => ({
  invoke: vi.fn(() => Promise.reject(new Error("no rust host"))),
}))
