import { useState, type ReactNode } from "react"

export type CollapseProps = {
  readonly name: string
  readonly summary?: string
  readonly children?: ReactNode
  readonly defaultOpen?: boolean
}

export function Collapse({
  name,
  summary,
  children,
  defaultOpen = false,
}: CollapseProps) {
  const [open, setOpen] = useState(defaultOpen)
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
      {open ? <div className="collapse__body">{children}</div> : null}
    </div>
  )
}
