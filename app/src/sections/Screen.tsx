import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { Slider } from "../components/Slider"
import { setBrightness, setDisplayHz } from "../lib/api"

export type ScreenProps = {
  readonly hzList: readonly string[]
}

export function Screen({ hzList }: ScreenProps) {
  const [hz, setHz] = useState(hzList[0] ?? "60")
  const [brightness, setBright] = useState(70)
  const options = hzList.map((value) => ({
    value,
    label: `${value} Hz`,
  }))

  async function onHz(next: string): Promise<void> {
    setHz(next)
    try {
      await setDisplayHz(next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  async function onBrightness(next: number): Promise<void> {
    setBright(next)
    try {
      await setBrightness(next)
    } catch (error) {
      if (error instanceof Error) {
        return
      }
      throw error
    }
  }

  return (
    <Row name="屏幕">
      {options.length > 0 ? (
        <Segmented value={hz} options={options} onChange={onHz} />
      ) : null}
      <Slider
        value={brightness}
        min={10}
        max={100}
        label="亮度"
        onChange={onBrightness}
      />
    </Row>
  )
}
