import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { HudControls, type HudBlockFlags } from "../hud/HudControls"
import {
  persistPrefsForBlock,
  type HudBlockKey,
  type OverlayPersistPrefs,
} from "../hud/hudOverlay"
import { overlayUpdate, setThemeMode, setUiLanguage } from "../lib/api"

const THEME_OPTIONS = [
  { value: "night", label: "夜间" },
  { value: "day", label: "日间" },
] as const

type ThemeMode = (typeof THEME_OPTIONS)[number]["value"]

const LANGUAGE_OPTIONS = [
  { value: "zh-CN", label: "中文" },
  { value: "en", label: "English", disabled: true },
] as const

type UiLanguage = (typeof LANGUAGE_OPTIONS)[number]["value"]

export type OverlaySettings = {
  readonly gameOnly?: boolean
  readonly displayOff?: boolean
  readonly showTemp?: boolean
  readonly showFans?: boolean
  readonly showPower?: boolean
  readonly showUsage?: boolean
  readonly showRam?: boolean
  readonly showBattery?: boolean
  readonly names?: boolean
}

export type SettingsDialogProps = {
  readonly hdrOn: boolean
  readonly onClose: () => void
  readonly overlay?: OverlaySettings
}

function hostMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message.length > 0) {
    return error.message
  }
  if (typeof error === "string" && error.length > 0) {
    return error
  }
  return fallback
}

function flagsFromOverlay(overlay: OverlaySettings): HudBlockFlags {
  return {
    showTemp: overlay.showTemp ?? true,
    showFans: overlay.showFans ?? true,
    showPower: overlay.showPower ?? true,
    showUsage: overlay.showUsage ?? true,
    showRam: overlay.showRam ?? true,
    showBattery: overlay.showBattery ?? true,
    names: overlay.names ?? false,
  }
}

export function SettingsDialog({ onClose, overlay }: SettingsDialogProps) {
  const [theme, setTheme] = useState<ThemeMode>("night")
  const [language, setLanguage] = useState<UiLanguage>("zh-CN")
  const [gameOnly, setGameOnly] = useState(overlay?.gameOnly ?? false)
  const [displayOff, setDisplayOff] = useState(overlay?.displayOff ?? false)
  const [flags, setFlags] = useState(() => flagsFromOverlay(overlay ?? {}))
  const [hostError, setHostError] = useState("")

  function report(error: unknown, fallback: string): void {
    setHostError(hostMessage(error, fallback))
  }

  function persistOverlayPrefs(prefs: OverlayPersistPrefs): void {
    setHostError("")
    void overlayUpdate(prefs).catch((error: unknown) => {
      report(error, "悬浮窗设置未能保存")
    })
  }

  function onTheme(next: ThemeMode): void {
    setTheme(next)
    document.documentElement.dataset.theme = next
    setHostError("")
    void setThemeMode(next).catch((error: unknown) => {
      report(error, "主题未能保存")
    })
  }

  function onLanguage(next: UiLanguage): void {
    setLanguage(next)
    setHostError("")
    void setUiLanguage(next).catch((error: unknown) => {
      report(error, "语言未能保存")
    })
  }

  return (
    <div className="settings-dialog" role="presentation" onClick={onClose}>
      <div
        className="settings-dialog__panel"
        role="dialog"
        aria-labelledby="settings-title"
        onClick={(event) => {
          event.stopPropagation()
        }}
      >
        <div className="settings-dialog__head">
          <h2 id="settings-title" className="settings-dialog__title">
            设置
          </h2>
          <button
            type="button"
            className="settings-dialog__close"
            aria-label="关闭设置"
            onClick={onClose}
          >
            <svg
              viewBox="0 0 16 16"
              width="16"
              height="16"
              aria-hidden="true"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.25"
            >
              <path d="M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6" />
            </svg>
          </button>
        </div>
        {hostError.length > 0 ? (
          <p className="settings-dialog__zone-title">{hostError}</p>
        ) : null}
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">外观</h3>
          <Row name="主题">
            <Segmented
              value={theme}
              options={THEME_OPTIONS}
              onChange={onTheme}
            />
          </Row>
          <Row name="语言">
            <div className="settings-dialog__language">
              <Segmented
                value={language}
                options={LANGUAGE_OPTIONS}
                onChange={onLanguage}
              />
              <p className="settings-dialog__lang-note">
                English 暂不可用：尚无字符串表
              </p>
            </div>
          </Row>
        </section>
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">悬浮窗</h3>
          <div className="settings-dialog__actions">
            <HudControls
              gameOnly={gameOnly}
              displayOff={displayOff}
              flags={flags}
              onGameOnly={(checked) => {
                setGameOnly(checked)
                persistOverlayPrefs({ gameOnly: checked })
              }}
              onDisplayOff={(checked) => {
                setDisplayOff(checked)
                persistOverlayPrefs({ displayOff: checked })
              }}
              onBlock={(key: HudBlockKey, on: boolean) => {
                setFlags((prev) => ({ ...prev, [key]: on }))
                persistOverlayPrefs(persistPrefsForBlock(key, on))
              }}
            />
          </div>
        </section>
      </div>
    </div>
  )
}
