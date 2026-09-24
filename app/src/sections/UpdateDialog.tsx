import { useEffect, useState } from "react"
import { listen } from "@tauri-apps/api/event"
import { assertNever } from "../lib/assertNever"
import { updatesCheck, updatesInstall, updatesOpenPage } from "../lib/api"
import "./UpdateDialog.css"

export type UpdateDialogProps = {
  readonly onClose: () => void
  readonly releaseNotes?: string
  readonly hasValidSha256?: boolean
  readonly onFeedback?: () => void
}

type CheckState =
  | { readonly kind: "checking" }
  | { readonly kind: "latest"; readonly latestVersion: string }
  | { readonly kind: "available"; readonly latestVersion: string }
  | { readonly kind: "failed"; readonly message: string }

const PROGRESS_EVENT = "updates_progress"

function hostMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message.length > 0) {
    return error.message
  }
  if (typeof error === "string" && error.length > 0) {
    return error
  }
  return fallback
}

function headline(state: CheckState): string {
  switch (state.kind) {
    case "checking":
      return "正在检查更新…"
    case "latest":
      return "已是最新版本"
    case "available":
      return `发现新版本 ${state.latestVersion}`
    case "failed":
      return "检查更新失败"
    default:
      return assertNever(state)
  }
}

function progressPercent(payload: unknown): number | undefined {
  if (typeof payload !== "number" || !Number.isFinite(payload)) {
    return undefined
  }
  return Math.min(100, Math.max(0, payload))
}

export function UpdateDialog({
  onClose,
  releaseNotes,
  hasValidSha256 = false,
  onFeedback,
}: UpdateDialogProps) {
  const [state, setState] = useState<CheckState>({ kind: "checking" })
  const [installing, setInstalling] = useState(false)
  const [progress, setProgress] = useState(0)
  const [actionError, setActionError] = useState("")
  const notes = releaseNotes?.trim() ?? ""

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const dto = await updatesCheck()
        if (cancelled) {
          return
        }
        setState(
          dto.updateAvailable
            ? { kind: "available", latestVersion: dto.latestVersion }
            : { kind: "latest", latestVersion: dto.latestVersion },
        )
      } catch (error) {
        if (cancelled) {
          return
        }
        if (error instanceof Error) {
          setState({ kind: "failed", message: error.message })
          return
        }
        if (typeof error === "string") {
          setState({ kind: "failed", message: error })
          return
        }
        setState({ kind: "failed", message: "检查更新失败" })
      }
    })()
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    let unlisten: (() => void) | undefined
    void listen(PROGRESS_EVENT, (event) => {
      const percent = progressPercent(event.payload)
      if (percent === undefined) {
        return
      }
      setProgress(percent)
    })
      .then((stop) => {
        if (cancelled) {
          stop()
          return
        }
        unlisten = stop
      })
      .catch((error: unknown) => {
        if (error instanceof Error || typeof error === "string") {
          return
        }
        throw error
      })
    return () => {
      cancelled = true
      unlisten?.()
    }
  }, [])

  const canInstall = state.kind === "available" && hasValidSha256 && !installing

  return (
    <div className="settings-dialog" role="presentation" onClick={onClose}>
      <div
        className="settings-dialog__panel"
        role="dialog"
        aria-labelledby="update-title"
        onClick={(event) => {
          event.stopPropagation()
        }}
      >
        <div className="settings-dialog__head">
          <h2 id="update-title" className="settings-dialog__title">
            检查更新
          </h2>
          <button
            type="button"
            className="settings-dialog__close"
            aria-label="关闭更新"
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
        <p className="settings-dialog__zone-title">{headline(state)}</p>
        {state.kind === "failed" ? (
          <p className="settings-dialog__zone-title">{state.message}</p>
        ) : null}
        {actionError.length > 0 ? <p className="settings-dialog__zone-title">{actionError}</p> : null}
        {notes.length > 0 ? (
          <pre className="update-dialog__notes">{releaseNotes}</pre>
        ) : null}
        {installing ? (
          <div
            className="update-dialog__progress"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={progress}
          >
            <div
              className="update-dialog__progress-fill"
              style={{ transform: `scaleX(${progress / 100})` }}
            />
          </div>
        ) : null}
        <div className="settings-dialog__actions">
          {state.kind === "available" ? (
            <>
              <button
                type="button"
                className="settings-dialog__action"
                disabled={!canInstall}
                onClick={() => {
                  setInstalling(true)
                  setProgress(0)
                  setActionError("")
                  void updatesInstall()
                    .catch((error: unknown) => {
                      setActionError(hostMessage(error, "安装失败"))
                    })
                    .finally(() => {
                      setInstalling(false)
                    })
                }}
              >
                下载并安装
              </button>
              <button
                type="button"
                className="settings-dialog__action"
                disabled={installing}
                onClick={() => {
                  setActionError("")
                  void updatesOpenPage().catch((error: unknown) => {
                    setActionError(hostMessage(error, "无法打开下载页"))
                  })
                }}
              >
                打开下载页
              </button>
            </>
          ) : null}
          <button
            type="button"
            className="settings-dialog__action"
            disabled={installing}
            onClick={onClose}
          >
            稍后
          </button>
          <button
            type="button"
            className="settings-dialog__action"
            disabled={installing}
            onClick={() => {
              onFeedback?.()
            }}
          >
            反馈
          </button>
        </div>
      </div>
    </div>
  )
}
