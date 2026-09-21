import { NumberField } from "./WattageField"
import { PerformanceCustom } from "./PerformanceCustom"

export type PerformanceWattageProps = {
  readonly tccAdjustable: boolean
  readonly ocSettings: boolean
  readonly customMode: boolean
}

export function PerformanceWattage({
  tccAdjustable,
  ocSettings,
  customMode,
}: PerformanceWattageProps) {
  return (
    <>
      <NumberField name="PL1" field="PL1" ariaLabel="PL1" defaultValue={45} />
      {!customMode && tccAdjustable ? (
        <NumberField name="TCC" field="CpuTccOffset" ariaLabel="TCC" />
      ) : null}
      {customMode ? <PerformanceCustom ocSettings={ocSettings} /> : null}
    </>
  )
}
