import { useEffect, useState } from "react"
import { assertNever } from "../lib/assertNever"
import { updatesCheck, updatesInstall, updatesOpenPage } from "../lib/api"

export type UpdateDialogProps = {
  readonly onClose: () => void
}

type CheckState =
  | { readonly kind: "checking" }
  | { readonly kind: "latest"; readonly latestVersion: string }
  | { readonly kind: "available"; readonly latestVersion: string }
  | { readonly kind: "failed"; readonly message: string }

async function swallowHostError(run: () => Promise<unknown>): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error || typeof error === "string") {
      return
    }
    throw error
  }
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

export function UpdateDialog({ onClose }: UpdateDialogProps) {
  const [state, setState] = useState<CheckState>({ kind: "checking" })

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
        <div className="settings-dialog__actions">
          {state.kind === "available" ? (
            <>
              <button
                type="button"
                className="settings-dialog__action"
                onClick={() => {
                  void swallowHostError(() => updatesInstall())
                }}
              >
                下载并安装
              </button>
              <button
                type="button"
                className="settings-dialog__action"
                onClick={() => {
                  void swallowHostError(() => updatesOpenPage())
                }}
              >
                打开下载页
              </button>
            </>
          ) : null}
          <button
            type="button"
            className="settings-dialog__action"
            onClick={onClose}
          >
            稍后
          </button>
        </div>
      </div>
    </div>
  )
}
