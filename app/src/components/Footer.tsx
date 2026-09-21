import { useEffect, useState, type ReactNode } from "react"
import { assertNever } from "../lib/assertNever"
import { appQuit, diagnosticsExport, overlaySet, updatesCheck } from "../lib/api"
import { DonateDialog } from "../sections/DonateDialog"
import "./Footer.css"

type FooterKeyId =
  | "overlay"
  | "settings"
  | "updates"
  | "diagnostics"
  | "donate"
  | "quit"

type FooterKey = {
  readonly id: FooterKeyId
  readonly label: string
  readonly ariaLabel: string
  readonly icon: ReactNode
}

function LinearIcon({ children }: { readonly children: ReactNode }) {
  return (
    <svg
      viewBox="0 0 16 16"
      width="16"
      height="16"
      aria-hidden="true"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.25"
    >
      {children}
    </svg>
  )
}

const FOOTER_KEYS: readonly FooterKey[] = [
  {
    id: "overlay",
    label: "悬浮窗",
    ariaLabel: "悬浮窗",
    icon: (
      <LinearIcon>
        <circle cx="8" cy="8" r="5.5" />
        <circle cx="8" cy="8" r="1.5" />
      </LinearIcon>
    ),
  },
  {
    id: "settings",
    label: "设置",
    ariaLabel: "设置",
    icon: (
      <LinearIcon>
        <circle cx="8" cy="8" r="2.25" />
        <path d="M8 1.75v1.8M8 12.45v1.8M1.75 8h1.8M12.45 8h1.8M3.4 3.4l1.27 1.27M11.33 11.33l1.27 1.27M3.4 12.6l1.27-1.27M11.33 4.67l1.27-1.27" />
      </LinearIcon>
    ),
  },
  {
    id: "updates",
    label: "更新",
    ariaLabel: "更新",
    icon: (
      <LinearIcon>
        <path d="M3.2 8a4.8 4.8 0 0 1 8.2-3.3L13 6.2" />
        <path d="M13 3.2v3H10" />
        <path d="M12.8 8a4.8 4.8 0 0 1-8.2 3.3L3 9.8" />
        <path d="M3 12.8v-3h3" />
      </LinearIcon>
    ),
  },
  {
    id: "donate",
    label: "赞助",
    ariaLabel: "赞助",
    icon: (
      <LinearIcon>
        <path d="M8 13.2S3.2 10 3.2 6.6A2.7 2.7 0 0 1 8 5.2 2.7 2.7 0 0 1 12.8 6.6C12.8 10 8 13.2 8 13.2z" />
      </LinearIcon>
    ),
  },
  {
    id: "diagnostics",
    label: "诊断",
    ariaLabel: "导出诊断包",
    icon: (
      <LinearIcon>
        <path d="M4 3.5h5.2L12.5 6.8V12.5H4z" />
        <path d="M9.2 3.5V6.8H12.5" />
      </LinearIcon>
    ),
  },
  {
    id: "quit",
    label: "退出",
    ariaLabel: "退出",
    icon: (
      <LinearIcon>
        <path d="M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6" />
      </LinearIcon>
    ),
  },
]

function offerIsAvailable(value: unknown): boolean {
  if (typeof value !== "object" || value === null) {
    return false
  }
  if (!("updateAvailable" in value)) {
    return false
  }
  return value.updateAvailable === true
}

async function runHost(
  run: () => Promise<unknown>,
  onHostError: ((message: string) => void) | undefined,
): Promise<void> {
  try {
    await run()
  } catch (error) {
    if (error instanceof Error) {
      if (onHostError) {
        onHostError(error.message)
        return
      }
      throw error
    }
    throw error
  }
}

export type FooterProps = {
  readonly releaseLabel?: string
  readonly onSettings?: () => void
  readonly onUpdates?: () => void
  readonly onHostError?: (message: string) => void
  readonly updateAvailable?: boolean
}

export function Footer({
  releaseLabel = "",
  onSettings,
  onUpdates,
  onHostError,
  updateAvailable: updateAvailableProp = false,
}: FooterProps) {
  const [overlayOn, setOverlayOn] = useState(false)
  const [donateOpen, setDonateOpen] = useState(false)
  const [updateAvailable, setUpdateAvailable] = useState(updateAvailableProp)

  useEffect(() => {
    setUpdateAvailable(updateAvailableProp)
  }, [updateAvailableProp])

  useEffect(() => {
    if (updateAvailableProp) {
      return
    }
    let cancelled = false
    void updatesCheck()
      .then((dto) => {
        if (!cancelled && offerIsAvailable(dto)) {
          setUpdateAvailable(true)
        }
      })
      .catch((error: unknown) => {
        if (error instanceof Error || typeof error === "string") {
          return
        }
        throw error
      })
    return () => {
      cancelled = true
    }
  }, [updateAvailableProp])

  function onFooterKey(id: FooterKeyId): void {
    switch (id) {
      case "overlay": {
        const next = !overlayOn
        setOverlayOn(next)
        void runHost(() => overlaySet(next), onHostError)
        return
      }
      case "settings":
        onSettings?.()
        return
      case "updates":
        onUpdates?.()
        return
      case "diagnostics":
        void runHost(() => diagnosticsExport(), onHostError)
        return
      case "donate":
        setDonateOpen(true)
        return
      case "quit":
        void runHost(() => appQuit(), onHostError)
        return
      default:
        assertNever(id)
    }
  }

  return (
    <footer className="footer">
      <span className="footer__version">{releaseLabel}</span>
      <div className="footer__keys">
        {FOOTER_KEYS.map((key) => (
          <button
            key={key.id}
            type="button"
            className={
              key.id === "overlay" && overlayOn
                ? "footer__key is-active"
                : "footer__key"
            }
            aria-label={key.ariaLabel}
            aria-pressed={key.id === "overlay" ? overlayOn : undefined}
            onClick={() => {
              onFooterKey(key.id)
            }}
          >
            {key.icon}
            <span>{key.label}</span>
            {key.id === "updates" && updateAvailable ? (
              <span className="footer__badge">有新版本</span>
            ) : null}
          </button>
        ))}
      </div>
      {donateOpen ? (
        <DonateDialog
          onClose={() => {
            setDonateOpen(false)
          }}
        />
      ) : null}
    </footer>
  )
}
