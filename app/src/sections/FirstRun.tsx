import { useEffect, useState } from "react"
import { invoke } from "@tauri-apps/api/core"
import { openUrl } from "@tauri-apps/plugin-opener"

export type FirstRunProps = {
  readonly onLater: () => void
  readonly onGoSystem: () => void
}

const APPS_FEATURES_URI = "ms-settings:appsfeatures" as const

type CoexistenceStatus = {
  readonly requiresPrompt: boolean
}

type Step = {
  readonly marker: string
  readonly title: string
  readonly body: string
  readonly warning: boolean
  readonly appsSettings: boolean
}

const REPLACED_STEP: Step = {
  marker: "1",
  title: "无需安装任何其他控制台",
  body: "安装器已经装好 GCU 服务与驱动，本程序自带全部必要组件。厂商「官方控制台」已由安装器清理并替换为 L-Mechrevo，不需要再下载或安装它。",
  warning: false,
  appsSettings: false,
}

const UNINSTALL_STEP: Step = {
  marker: "!",
  title: "请卸载官方控制台",
  body: "检测到机器上仍有厂商的 GCU 环境。L-Mechrevo 不会替你静默删除厂商的软件。请手动卸载「官方控制台」应用（设置 → 应用 → 已安装的应用），然后重新启动 L-Mechrevo。",
  warning: true,
  appsSettings: true,
}

const LATER_STEPS: readonly Step[] = [
  {
    marker: "2",
    title: "直接开始使用",
    body: "打开本程序的“系统”页即可设置开机启动、性能模式、显卡模式与灯效；GCU 服务在后台运行，无需额外操作。",
    warning: false,
    appsSettings: false,
  },
  {
    marker: "!",
    title: "退出其他灯效控制软件",
    body: "不要同时运行 BetterRGB、OpenRGB 或其他厂商灯效程序，否则多个程序抢占 HID 设备可能导致灯效失效或设备访问冲突。",
    warning: true,
    appsSettings: false,
  },
]

const UNKNOWN_STEP: Step = {
  marker: "?",
  title: "未能确认官方控制台是否已卸载",
  body: "无法检测官方控制台。不要把它当成已经清理。请到设置 → 应用里自行确认，仍在就卸载，然后重新打开本程序。",
  warning: true,
  appsSettings: true,
}

function consoleStep(status: "clean" | "leftover" | "unknown"): Step {
  if (status === "leftover") {
    return UNINSTALL_STEP
  }
  if (status === "unknown") {
    return UNKNOWN_STEP
  }
  return REPLACED_STEP
}

function openAppsSettings(): void {
  void openUrl(APPS_FEATURES_URI).catch((error: unknown) => {
    if (error instanceof Error) {
      return
    }
    throw error
  })
}

export function FirstRun({ onLater, onGoSystem }: FirstRunProps) {
  const [consoleState, setConsoleState] = useState<"clean" | "leftover" | "unknown">("unknown")

  useEffect(() => {
    let cancelled = false
    void invoke<CoexistenceStatus>("gcu_coexistence_status")
      .then((status) => {
        if (!cancelled) {
          setConsoleState(status.requiresPrompt ? "leftover" : "clean")
        }
      })
      .catch(() => {
        if (!cancelled) {
          setConsoleState("unknown")
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  const steps = [consoleStep(consoleState), ...LATER_STEPS]

  return (
    <div className="first-run" role="dialog" aria-labelledby="first-run-title">
      <header className="first-run__header">
        <h1 id="first-run-title" className="first-run__title">
          开始使用 L-Mechrevo
        </h1>
        <p className="first-run__lead">
          完成下面三步，保留硬件服务并避免控制冲突。
        </p>
      </header>
      <div className="first-run__body">
        {steps.map((step) => (
          <section
            key={step.title}
            className={
              step.warning ? "first-run__step first-run__step--warn" : "first-run__step"
            }
          >
            <span className="first-run__marker" aria-hidden="true">
              {step.marker}
            </span>
            <div>
              <h2 className="first-run__step-title">{step.title}</h2>
              <p className="first-run__step-body">{step.body}</p>
              {step.appsSettings ? (
                <button
                  type="button"
                  className="first-run__btn first-run__step-btn"
                  onClick={openAppsSettings}
                >
                  打开应用设置
                </button>
              ) : null}
            </div>
          </section>
        ))}
      </div>
      <footer className="first-run__actions">
        <button type="button" className="first-run__btn" onClick={onLater}>
          稍后
        </button>
        <button
          type="button"
          className="first-run__btn first-run__btn--primary"
          onClick={onGoSystem}
        >
          前往系统页
        </button>
      </footer>
    </div>
  )
}
