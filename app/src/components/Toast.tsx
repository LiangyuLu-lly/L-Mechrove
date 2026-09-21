import { useEffect } from "react"
import "./Toast.css"

export const TOAST_DURATION_MS = 2000 as const

export type ToastProps = {
  readonly message: string | null
  readonly onDismiss: () => void
  readonly durationMs?: number
}

export function Toast({
  message,
  onDismiss,
  durationMs = TOAST_DURATION_MS,
}: ToastProps) {
  useEffect(() => {
    if (message === null) {
      return
    }
    const timer = window.setTimeout(onDismiss, durationMs)
    return () => {
      window.clearTimeout(timer)
    }
  }, [message, onDismiss, durationMs])

  if (message === null) {
    return null
  }

  return (
    <div className="toast" role="status">
      <span className="toast__message">{message}</span>
      <button
        type="button"
        className="toast__dismiss"
        onClick={onDismiss}
        aria-label="关闭"
      >
        关闭
      </button>
    </div>
  )
}
