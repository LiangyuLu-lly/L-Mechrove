import { Row } from "../components/Row"
import { setCustomDetail } from "../lib/api"

async function swallow(run: () => Promise<unknown>): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

export function NumberField(props: {
  readonly name: string
  readonly field: string
  readonly ariaLabel: string
  readonly defaultValue?: number
}) {
  return (
    <Row name={props.name}>
      <input
        className="settings-dialog__number"
        type="number"
        defaultValue={props.defaultValue}
        aria-label={props.ariaLabel}
        onChange={(event) => {
          void swallow(() => setCustomDetail(props.field, event.target.value))
        }}
      />
    </Row>
  )
}
