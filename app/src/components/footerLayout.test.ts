import { describe, expect, it } from "bun:test"

function declarationBlock(css: string, selector: string): string {
  const match = css.match(new RegExp(`(?:^|\\n)${selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*\\{([^}]+)\\}`))
  expect(match).toBeTruthy()
  return match?.[1] ?? ""
}

describe("footerLayout", () => {
  it("locks Footer.css so six keys stay one row with nowrap and flex 0 0", async () => {
    const css = await Bun.file(new URL("./Footer.css", import.meta.url)).text()

    const footer = declarationBlock(css, ".footer")
    expect(footer).toContain("flex-wrap: nowrap")

    const key = declarationBlock(css, ".footer__key")
    expect(key).toContain("flex: 0 0")
    expect(key).toContain("white-space: nowrap")
  })

  it("keeps the shell fluid capped at 420 so a scrollbar cannot force horizontal overflow", async () => {
    const css = await Bun.file(new URL("../App.css", import.meta.url)).text()

    const declarations = declarationBlock(css, ".shell")
      .split(";")
      .map((declaration) => declaration.trim())

    expect(declarations).toContain("width: 100%")
    expect(declarations).toContain("max-width: 420px")
    expect(declarations).not.toContain("width: 420px")
  })

  it("lets a segmented row share its line instead of starving the slider", async () => {
    const css = await Bun.file(new URL("../App.css", import.meta.url)).text()

    const segmented = declarationBlock(css, ".segmented")
      .split(";")
      .map((declaration) => declaration.trim())
    expect(segmented).not.toContain("width: 100%")

    const slider = declarationBlock(css, ".slider")
      .split(";")
      .map((declaration) => declaration.trim())
    expect(slider.some((declaration) => declaration.startsWith("min-width:"))).toBe(true)
  })
})
