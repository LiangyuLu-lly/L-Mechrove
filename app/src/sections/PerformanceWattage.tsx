import { NumberField } from "./WattageField"

export type PerformanceWattageProps = {
  readonly tccAdjustable: boolean
  readonly ocSettings: boolean
  readonly customMode: boolean
}

export function PerformanceWattage({
  tccAdjustable,
  customMode,
}: PerformanceWattageProps) {
  return (
    <>
      <NumberField name="PL1" field="PL1" ariaLabel="PL1" defaultValue={45} />
      {!customMode && tccAdjustable ? (
        <NumberField name="TCC" field="CpuTccOffset" ariaLabel="TCC" />
      ) : null}
    </>
  )
}
