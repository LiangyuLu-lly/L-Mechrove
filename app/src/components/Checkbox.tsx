import "./Checkbox.css"

export type CheckboxProps = {
  readonly checked: boolean
  readonly label: string
  readonly onChange: (checked: boolean) => void
}

export function Checkbox({ checked, label, onChange }: CheckboxProps) {
  return (
    <label className="checkbox">
      <span className="checkbox__control">
        <input
          type="checkbox"
          className="checkbox__input"
          checked={checked}
          onChange={(event) => {
            onChange(event.currentTarget.checked)
          }}
        />
        <svg
          className="checkbox__tick"
          viewBox="0 0 16 16"
          width="16"
          height="16"
          aria-hidden="true"
        >
          <path
            d="M3.5 8.2 L6.6 11.2 L12.5 4.8"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.75"
            strokeLinecap="square"
            strokeLinejoin="miter"
          />
        </svg>
      </span>
      <span className="checkbox__label">{label}</span>
    </label>
  )
}
