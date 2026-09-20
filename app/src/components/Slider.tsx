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
      onChange={(event) => {
        onChange?.(Number(event.currentTarget.value))
      }}
    />
  )
}
