import { assertNever } from "../lib/assertNever"
import type { MqttStatus } from "../lib/types"

export type StatusPillProps = {
  readonly mqtt: MqttStatus
}

type PillTone = "ok" | "warn" | "danger" | "muted"

function toneClass(tone: PillTone): string {
  switch (tone) {
    case "ok":
      return "status-pill status-pill--ok"
    case "warn":
      return "status-pill status-pill--warn"
    case "danger":
      return "status-pill status-pill--danger"
    case "muted":
      return "status-pill"
    default:
      return assertNever(tone)
  }
}

const DISCONNECTED_COPY = "● GCU 未连接"

function pillFor(mqtt: MqttStatus): { readonly label: string; readonly tone: PillTone } {
  switch (mqtt) {
    case "Connecting":
      return { label: "● GCU 连接中", tone: "warn" }
    case "Connected":
      return { label: "● GCU 已连接", tone: "ok" }
    case "Disconnected":
      return { label: DISCONNECTED_COPY, tone: "muted" }
    case "Error":
      return { label: DISCONNECTED_COPY, tone: "danger" }
    default:
      return assertNever(mqtt)
  }
}

export function StatusPill({ mqtt }: StatusPillProps) {
  const pill = pillFor(mqtt)
  return (
    <div className={toneClass(pill.tone)}>
      <span className="status-pill__dot" aria-hidden="true" />
      <span>{pill.label}</span>
    </div>
  )
}
