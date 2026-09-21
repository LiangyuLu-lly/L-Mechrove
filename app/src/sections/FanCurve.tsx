import { useRef, useState } from "react"
import type { KeyboardEvent, PointerEvent } from "react"
import { setFanCurve } from "../lib/api"
import type { FanCurveType } from "../lib/types"
import "./FanCurve.css"

const POINT_COUNT = 16
const DUTY_MIN = 0
const DUTY_MAX = 100
const VIEW_W = 320
const VIEW_H = 160
const PAD_L = 28
const PAD_R = 8
const PAD_T = 10
const PAD_B = 20
const PLOT_W = VIEW_W - PAD_L - PAD_R
const PLOT_H = VIEW_H - PAD_T - PAD_B
const Y_TICKS = [0, 25, 50, 75, 100] as const
const X_TICKS = [0, 5, 10, 15] as const

type FanCurvePlotProps = {
  readonly type: FanCurveType
  readonly duties: readonly number[]
  readonly onChange?: (duties: readonly number[]) => void
  readonly onHostError?: (message: string) => void
}

export type FanCurveProps = {
  readonly cpuDuties: readonly number[]
  readonly gpuDuties: readonly number[]
  readonly onCpuChange?: (duties: readonly number[]) => void
  readonly onGpuChange?: (duties: readonly number[]) => void
  readonly onHostError?: (message: string) => void
}

function clampDuty(value: number): number {
  return Math.min(DUTY_MAX, Math.max(DUTY_MIN, value))
}

function xAt(index: number): number {
  return PAD_L + (PLOT_W * index) / (POINT_COUNT - 1)
}

function yAt(duty: number): number {
  return PAD_T + PLOT_H * (1 - duty / DUTY_MAX)
}

function dutyFromClientY(clientY: number, svg: SVGSVGElement): number {
  const rect = svg.getBoundingClientRect()
  const y = ((clientY - rect.top) / rect.height) * VIEW_H
  const rel = 1 - (y - PAD_T) / PLOT_H
  return clampDuty(Math.round(rel * DUTY_MAX))
}

function keyDelta(key: string): number {
  if (key === "ArrowUp" || key === "ArrowRight") {
    return 1
  }
  if (key === "ArrowDown" || key === "ArrowLeft") {
    return -1
  }
  return 0
}

async function runHost(
  run: () => Promise<unknown>,
  onHostError?: (message: string) => void,
): Promise<void> {
  try {
    await run()
  } catch (error) {
    onHostError?.(error instanceof Error ? error.message : String(error))
  }
}

export function FanCurve({
  cpuDuties,
  gpuDuties,
  onCpuChange,
  onGpuChange,
  onHostError,
}: FanCurveProps) {
  return (
    <div className="fan-curve-pair">
      <FanCurvePlot
        type="CPU"
        duties={cpuDuties}
        onChange={onCpuChange}
        onHostError={onHostError}
      />
      <FanCurvePlot
        type="GPU"
        duties={gpuDuties}
        onChange={onGpuChange}
        onHostError={onHostError}
      />
    </div>
  )
}

function FanCurvePlot({ type, duties, onChange, onHostError }: FanCurvePlotProps) {
  const svgRef = useRef<SVGSVGElement>(null)
  const [dragIndex, setDragIndex] = useState<number | null>(null)

  function publish(next: readonly number[]): void {
    onChange?.(next)
    void runHost(() => setFanCurve("curve", type, next), onHostError)
  }

  function replaceDuty(index: number, duty: number): void {
    const clamped = clampDuty(duty)
    const current = duties[index]
    if (current === clamped) {
      return
    }
    publish(duties.map((value, i) => (i === index ? clamped : value)))
  }

  function onPointKeyDown(index: number, event: KeyboardEvent<SVGCircleElement>): void {
    const delta = keyDelta(event.key)
    if (delta === 0) {
      return
    }
    event.preventDefault()
    replaceDuty(index, (duties[index] ?? 0) + delta)
  }

  function onPointPointerDown(
    index: number,
    event: PointerEvent<SVGCircleElement>,
  ): void {
    event.currentTarget.setPointerCapture(event.pointerId)
    setDragIndex(index)
  }

  function onPointPointerMove(
    index: number,
    event: PointerEvent<SVGCircleElement>,
  ): void {
    if (dragIndex !== index) {
      return
    }
    const svg = svgRef.current
    if (svg === null) {
      return
    }
    replaceDuty(index, dutyFromClientY(event.clientY, svg))
  }

  const points = Array.from({ length: POINT_COUNT }, (_, index) => {
    const duty = clampDuty(duties[index] ?? 0)
    return { index, duty, x: xAt(index), y: yAt(duty) }
  })

  return (
    <svg
      ref={svgRef}
      className="fan-curve"
      viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
      role="img"
      aria-label={`${type} 风扇曲线`}
      onPointerUp={() => {
        setDragIndex(null)
      }}
      onPointerCancel={() => {
        setDragIndex(null)
      }}
    >
      {Y_TICKS.map((tick) => {
        const y = yAt(tick)
        const axis = tick === 0 || tick === DUTY_MAX
        return (
          <g key={tick}>
            <line
              className={axis ? "fan-curve__axis" : "fan-curve__grid"}
              x1={PAD_L}
              y1={y}
              x2={PAD_L + PLOT_W}
              y2={y}
            />
            <text
              className="fan-curve__tick"
              x={PAD_L - 4}
              y={y}
              textAnchor="end"
              dominantBaseline="middle"
            >
              {tick}
            </text>
          </g>
        )
      })}
      {X_TICKS.map((tick) => (
        <text
          key={tick}
          className="fan-curve__tick"
          x={xAt(tick)}
          y={VIEW_H - 4}
          textAnchor="middle"
        >
          {`T${tick}`}
        </text>
      ))}
      <polyline
        className="fan-curve__line"
        points={points.map((point) => `${point.x},${point.y}`).join(" ")}
      />
      {points.map((point) => (
        <circle
          key={point.index}
          className="fan-curve__point"
          cx={point.x}
          cy={point.y}
          r={5}
          role="slider"
          tabIndex={0}
          aria-label={`${type} T${point.index} 转速`}
          aria-orientation="vertical"
          aria-valuemin={DUTY_MIN}
          aria-valuemax={DUTY_MAX}
          aria-valuenow={point.duty}
          onKeyDown={(event) => {
            onPointKeyDown(point.index, event)
          }}
          onPointerDown={(event) => {
            onPointPointerDown(point.index, event)
          }}
          onPointerMove={(event) => {
            onPointPointerMove(point.index, event)
          }}
          onPointerUp={() => {
            setDragIndex(null)
          }}
        />
      ))}
    </svg>
  )
}
