export type SegmentOption<T extends string> = {
  readonly value: T
  readonly label: string
}

export type SegmentedProps<T extends string> = {
  readonly value: T
  readonly options: readonly SegmentOption<T>[]
  readonly onChange: (value: T) => void
}

export function Segmented<T extends string>({
  value,
  options,
  onChange,
}: SegmentedProps<T>) {
  return (
    <div className="segmented" role="radiogroup">
      {options.map((option) => {
        const selected = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            className={
              selected ? "segmented__option is-selected" : "segmented__option"
            }
            role="radio"
            aria-checked={selected}
            onClick={() => {
              onChange(option.value)
            }}
          >
            {option.label}
          </button>
        )
      })}
    </div>
  )
}
