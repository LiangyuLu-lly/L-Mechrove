import { Checkbox } from "../components/Checkbox"
import { HUD_BLOCK_TOGGLES, type HudBlockKey } from "./hudOverlay"

export type HudBlockFlags = {
  readonly showTemp: boolean
  readonly showFans: boolean
  readonly showPower: boolean
  readonly showUsage: boolean
  readonly showRam: boolean
  readonly showBattery: boolean
  readonly names: boolean
}

export type HudControlsProps = {
  readonly gameOnly: boolean
  readonly displayOff: boolean
  readonly flags: HudBlockFlags
  readonly onGameOnly: (on: boolean) => void
  readonly onDisplayOff: (on: boolean) => void
  readonly onBlock: (key: HudBlockKey, on: boolean) => void
}

function stopHudBubble(event: { stopPropagation(): void }): void {
  event.stopPropagation()
}

export function HudControls({
  gameOnly,
  displayOff,
  flags,
  onGameOnly,
  onDisplayOff,
  onBlock,
}: HudControlsProps) {
  return (
    <>
      <div
        className="hud__prefs"
        onClick={stopHudBubble}
        onPointerDown={stopHudBubble}
      >
        <Checkbox
          checked={gameOnly}
          label="仅游戏显示"
          onChange={onGameOnly}
        />
        <Checkbox
          checked={displayOff}
          label="熄屏挂起"
          onChange={onDisplayOff}
        />
      </div>
      <div
        className="hud__blocks"
        onClick={stopHudBubble}
        onPointerDown={stopHudBubble}
      >
        {HUD_BLOCK_TOGGLES.map((toggle) => (
          <Checkbox
            key={toggle.key}
            checked={flags[toggle.key]}
            label={toggle.label}
            onChange={(checked) => {
              onBlock(toggle.key, checked)
            }}
          />
        ))}
      </div>
    </>
  )
}
