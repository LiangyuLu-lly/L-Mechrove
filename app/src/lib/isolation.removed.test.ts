import { describe, expect, it } from "bun:test"
import { readFileSync } from "node:fs"

const ISOLATION_COMMAND = "set_official_isolation"
const ISOLATION_WRAPPER = "setOfficialIsolation"
const ISOLATION_FIELD = "officialIsolation"
const ISOLATION_FIELD_SNAKE = "official_isolation"

function read(rel: string): string {
  return readFileSync(new URL(rel, import.meta.url), "utf8")
}

function handlerBlock(lib: string): string {
  const start = lib.indexOf("generate_handler![")
  const end = lib.indexOf("]", start)
  expect(start).toBeGreaterThanOrEqual(0)
  expect(end).toBeGreaterThan(start)
  return lib.slice(start, end)
}

describe("port exposes no official-console isolation", () => {
  it("registers no isolation command when generate_handler is read", () => {
    const lib = read("../../src-tauri/src/lib.rs")
    expect(handlerBlock(lib)).not.toContain(ISOLATION_COMMAND)
  })

  it("carries no isolation field when snapshot types are read", () => {
    const ts = read("./types.ts")
    const rust = read("../../src-tauri/src/hw_snapshot.rs")
    expect(ts).not.toContain(ISOLATION_FIELD)
    expect(ts).not.toContain(ISOLATION_FIELD_SNAKE)
    expect(rust).not.toContain(ISOLATION_FIELD)
    expect(rust).not.toContain(ISOLATION_FIELD_SNAKE)
  })

  it("wires no isolation control when the settings surface is read", () => {
    const source = read("../sections/SettingsDialog.tsx")
    expect(source).not.toContain(ISOLATION_WRAPPER)
    expect(source).not.toContain(ISOLATION_COMMAND)
    expect(source).not.toContain("onIsolation")
  })

  it("lists no isolation entry when command allowlists are read", () => {
    const parity = read("./api.parity.test.ts")
    const api = read("./api.ts")
    const caps = read("../../src-tauri/capabilities/default.json")
    expect(parity).not.toContain(ISOLATION_COMMAND)
    expect(api).not.toContain(ISOLATION_COMMAND)
    expect(api).not.toContain(ISOLATION_WRAPPER)
    expect(caps).not.toContain(ISOLATION_COMMAND)
  })
})
