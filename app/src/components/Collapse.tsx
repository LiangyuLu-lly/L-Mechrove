import { useState, type ReactNode } from "react"

export type CollapseProps = {
  readonly name: string
  readonly summary?: string
  readonly children?: ReactNode
}

export function Collapse({ name, summary, children }: CollapseProps) {
  const [open, setOpen] = useState(false)
  return (
    <div className="collapse">
      <button
        type="button"
        className="collapse__header"
        aria-expanded={open}
        onClick={() => {
          setOpen(!open)
        }}
      >
        <span className="collapse__chevron" aria-hidden="true">
          {open ? "▾" : "▸"}
        </span>
        <span className="collapse__name">{name}</span>
        {summary !== undefined ? (
          <span className="collapse__summary">{summary}</span>
        ) : null}
      </button>
      <div className="collapse__body" hidden={!open}>
        {children}
      </div>
    </div>
  )
}
