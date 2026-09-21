import type { HwSnapshot } from "../lib/types"
import "./TelemetryRow.css"

export type TelemetryRowProps = Pick<
  HwSnapshot,
  "cpuTempC" | "gpuTempC" | "cpuRpm" | "gpuRpm" | "cpuWatt" | "gpuWatt"
>

const MISSING = "—"

function formatTempC(value: number | undefined): string {
  return value === undefined ? MISSING : `${Math.round(value)}°C`
}

function formatWatt(value: number | undefined): string {
  return value === undefined ? MISSING : `${Math.round(value)}W`
}

function formatRpm(value: number | undefined): string {
  return value === undefined ? MISSING : `${Math.round(value)}rpm`
}

function restText(watt: number | undefined, rpm: number | undefined): string {
  return `${formatWatt(watt)} ${formatRpm(rpm)}`
}

export function TelemetryRow({
  cpuTempC,
  gpuTempC,
  cpuRpm,
  gpuRpm,
  cpuWatt,
  gpuWatt,
}: TelemetryRowProps) {
  return (
    <div className="telemetry-row">
      <span className="telemetry-row__name">CPU</span>
      <span className="telemetry-row__temp">{formatTempC(cpuTempC)}</span>
      <span className="telemetry-row__rest">{restText(cpuWatt, cpuRpm)}</span>
      <span className="telemetry-row__sep">·</span>
      <span className="telemetry-row__name">GPU</span>
      <span className="telemetry-row__temp">{formatTempC(gpuTempC)}</span>
      <span className="telemetry-row__rest">{restText(gpuWatt, gpuRpm)}</span>
    </div>
  )
}
