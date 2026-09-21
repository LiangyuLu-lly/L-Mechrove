import { useState } from "react"
import { Row } from "../components/Row"
import { Segmented } from "../components/Segmented"
import { setOfficialIsolation, setThemeMode, setUiLanguage } from "../lib/api"

const THEME_OPTIONS = [
  { value: "night", label: "夜间" },
  { value: "day", label: "日间" },
] as const

type ThemeMode = (typeof THEME_OPTIONS)[number]["value"]

const LANGUAGE_OPTIONS = [
  { value: "zh-CN", label: "中文" },
  { value: "en", label: "English" },
] as const

type UiLanguage = (typeof LANGUAGE_OPTIONS)[number]["value"]

export type SettingsDialogProps = {
  readonly hdrOn: boolean
  readonly onClose: () => void
}

async function swallowHostError(run: () => Promise<unknown>): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error) {
      return
    }
    throw error
  }
}

export function SettingsDialog({ onClose }: SettingsDialogProps) {
  const [theme, setTheme] = useState<ThemeMode>("night")
  const [language, setLanguage] = useState<UiLanguage>("zh-CN")
  const [isolated, setIsolated] = useState(false)

  function onTheme(next: ThemeMode): void {
    setTheme(next)
    document.documentElement.dataset.theme = next
    void swallowHostError(() => setThemeMode(next))
  }

  function onLanguage(next: UiLanguage): void {
    setLanguage(next)
    void swallowHostError(() => setUiLanguage(next))
  }

  function onIsolation(on: boolean): void {
    setIsolated(on)
    void swallowHostError(() => setOfficialIsolation(on))
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
            <Segmented
              value={language}
              options={LANGUAGE_OPTIONS}
              onChange={onLanguage}
            />
          </Row>
        </section>
        <section className="settings-dialog__zone">
          <h3 className="settings-dialog__zone-title">系统</h3>
          <Row name="官方控制台" status={isolated ? "已隔离" : "未隔离"}>
            <div className="settings-dialog__actions">
              <button
                type="button"
                className="settings-dialog__action"
                onClick={() => {
                  onIsolation(true)
                }}
              >
                隔离官方界面与托盘
              </button>
              <button
                type="button"
                className="settings-dialog__action"
                onClick={() => {
                  onIsolation(false)
                }}
              >
                恢复官方控制台
              </button>
            </div>
          </Row>
        </section>
      </div>
    </div>
  )
}
