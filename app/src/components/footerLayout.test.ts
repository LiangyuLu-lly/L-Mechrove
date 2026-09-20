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
})
