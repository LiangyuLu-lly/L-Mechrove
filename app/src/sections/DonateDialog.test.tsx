import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, describe, expect, it, mock } from "bun:test"
import { DonateDialog } from "./DonateDialog"

describe("DonateDialog", () => {
  afterEach(() => {
    cleanup()
  })

  it("renders the C# headline and subtitle", () => {
    render(<DonateDialog onClose={() => undefined} />)
    expect(screen.getByText("感谢支持 L-Mechrevo")).toBeTruthy()
    expect(screen.getByText("扫码赞助，支持持续开发")).toBeTruthy()
    expect(screen.getByRole("dialog", { name: "赞助支持" })).toBeTruthy()
  })

  it("renders two QR images when both sources exist", () => {
    render(
      <DonateDialog
        onClose={() => undefined}
        qrSources={["qrcode1.jpg", "qrcode2.jpg"]}
      />,
    )
    const images = screen.getAllByRole("img")
    expect(images).toHaveLength(2)
    expect(images[0]?.getAttribute("src")).toBe("qrcode1.jpg")
    expect(images[1]?.getAttribute("src")).toBe("qrcode2.jpg")
  })

  it("shows plain text fallback instead of a broken image when a source is missing", () => {
    render(
      <DonateDialog onClose={() => undefined} qrSources={["", "qrcode2.jpg"]} />,
    )
    expect(screen.getByText("二维码资源缺失")).toBeTruthy()
    expect(screen.getAllByRole("img")).toHaveLength(1)
    expect(screen.queryByRole("img", { name: /缺失/ })).toBeNull()
  })

  it("shows two text fallbacks when both sources are missing", () => {
    render(<DonateDialog onClose={() => undefined} qrSources={["", ""]} />)
    expect(screen.getAllByText("二维码资源缺失")).toHaveLength(2)
    expect(screen.queryByRole("img")).toBeNull()
  })

  it("closes when the close control is clicked", () => {
    const onClose = mock(() => undefined)
    render(<DonateDialog onClose={onClose} />)
    fireEvent.click(screen.getByRole("button", { name: "关闭赞助" }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it("closes when the scrim is clicked", () => {
    const onClose = mock(() => undefined)
    render(<DonateDialog onClose={onClose} />)
    fireEvent.click(screen.getByRole("presentation"))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it("locks the panel height at 420px", async () => {
    const css = await Bun.file(new URL("./DonateDialog.css", import.meta.url)).text()
    expect(css).toContain("height: var(--donate-height)")
    const appCss = await Bun.file(new URL("../App.css", import.meta.url)).text()
    expect(appCss).toMatch(/--donate-height:\s*420px/)
  })
})
