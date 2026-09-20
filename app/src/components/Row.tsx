import type { ReactNode } from "react"

export type RowProps = {
  readonly name: string
  readonly status?: string
  readonly children?: ReactNode
}

export function Row({ name, status, children }: RowProps) {
  return (
    <div className="row">
      <div className="row__head">
        <svg
          className="row__icon"
          viewBox="0 0 16 16"
          width="16"
          height="16"
          aria-hidden="true"
        >
          <rect
            x="2.5"
            y="2.5"
            width="11"
            height="11"
            rx="1.5"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.25"
          />
        </svg>
        <span className="row__name">{name}</span>
        {status !== undefined ? (
          <span className="row__status">{status}</span>
        ) : null}
      </div>
      {children !== undefined ? (
        <div className="row__control">{children}</div>
      ) : null}
    </div>
  )
}
