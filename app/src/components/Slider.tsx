import type { CSSProperties } from "react"

export type SliderProps = {
  readonly value: number
  readonly min: number
  readonly max: number
  readonly step?: number
  readonly label?: string
  readonly onChange?: (value: number) => void
}

export function Slider({
  value,
  min,
  max,
  step = 1,
  label = "限充",
  onChange,
}: SliderProps) {
  const writable = onChange !== undefined
  const span = max - min
  const progressPct = span === 0 ? 0 : ((value - min) / span) * 100
  const style: CSSProperties & { "--slider-progress": string } = {
    "--slider-progress": `${progressPct}%`,
  }
  return (
    <input
      className="slider"
      type="range"
      min={min}
      max={max}
      step={step}
      value={value}
      disabled={!writable}
      aria-label={label}
      style={style}
      onChange={(event) => {
        onChange?.(Number(event.currentTarget.value))
      }}
    />
  )
}
