import { JSDOM } from "jsdom"

const dom = new JSDOM(
  "<!doctype html><html lang='zh-CN' data-theme='night'><body></body></html>",
  { url: "http://localhost/", pretendToBeVisual: true },
)

const win = dom.window

Object.defineProperty(globalThis, "window", {
  configurable: true,
  writable: true,
  value: win,
})
Object.defineProperty(globalThis, "document", {
  configurable: true,
  writable: true,
  value: win.document,
})
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  writable: true,
  value: win.navigator,
})
Object.defineProperty(globalThis, "HTMLElement", {
  configurable: true,
  writable: true,
  value: win.HTMLElement,
})
Object.defineProperty(globalThis, "Element", {
  configurable: true,
  writable: true,
  value: win.Element,
})
Object.defineProperty(globalThis, "Node", {
  configurable: true,
  writable: true,
  value: win.Node,
})
Object.defineProperty(globalThis, "SVGElement", {
  configurable: true,
  writable: true,
  value: win.SVGElement,
})
Object.defineProperty(globalThis, "DocumentFragment", {
  configurable: true,
  writable: true,
  value: win.DocumentFragment,
})
Object.defineProperty(globalThis, "MutationObserver", {
  configurable: true,
  writable: true,
  value: win.MutationObserver,
})
Object.defineProperty(globalThis, "getComputedStyle", {
  configurable: true,
  writable: true,
  value: win.getComputedStyle.bind(win),
})

Reflect.set(globalThis, "IS_REACT_ACT_ENVIRONMENT", true)
