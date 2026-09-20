import { Row } from "../components/Row"
import { setCustomDetail } from "../lib/api"

export type PerformanceWattageProps = {
  readonly tccAdjustable: boolean
  readonly ocSettings: boolean
  readonly customMode: boolean
}

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

function NumberField(props: {
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

export function PerformanceWattage({
  tccAdjustable,
  ocSettings,
  customMode,
}: PerformanceWattageProps) {
  return (
    <>
      <NumberField name="PL1" field="PL1" ariaLabel="PL1" defaultValue={45} />
      {tccAdjustable ? (
        <NumberField name="TCC" field="CpuTccOffset" ariaLabel="TCC" />
      ) : null}
      {customMode && ocSettings ? (
        <>
          <NumberField
            name="Core"
            field="GpuCoreClockOffsetOC"
            ariaLabel="Core"
          />
          <NumberField
            name="Memory"
            field="GpuMemoryClockOffsetOC"
            ariaLabel="Memory"
          />
        </>
      ) : null}
    </>
  )
}
