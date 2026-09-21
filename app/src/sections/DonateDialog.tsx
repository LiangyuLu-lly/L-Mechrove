import { useState, type ReactNode } from "react"
import "./DonateDialog.css"

export type DonateDialogProps = {
  readonly onClose: () => void
  readonly qrSources?: readonly [string, string]
}

function defaultQrSources(): readonly [string, string] {
  return [
    new URL("../assets/qrcode1.jpg", import.meta.url).href,
    new URL("../assets/qrcode2.jpg", import.meta.url).href,
  ]
}

function QrSlot({ src, label }: { readonly src: string; readonly label: string }): ReactNode {
  const [failed, setFailed] = useState(src.length === 0)
  if (src.length === 0 || failed) {
    return <p className="donate-dialog__fallback">二维码资源缺失</p>
  }
  return (
    <img
      className="donate-dialog__qr"
      src={src}
      alt={label}
      onError={() => {
        setFailed(true)
      }}
    />
  )
}

export function DonateDialog({ onClose, qrSources }: DonateDialogProps) {
  const sources = qrSources ?? defaultQrSources()
  return (
    <div className="settings-dialog" role="presentation" onClick={onClose}>
      <div
        className="settings-dialog__panel donate-dialog__panel"
        role="dialog"
        aria-labelledby="donate-title"
        onClick={(event) => {
          event.stopPropagation()
        }}
      >
        <div className="settings-dialog__head">
          <h2 id="donate-title" className="settings-dialog__title">
            赞助支持
          </h2>
          <button
            type="button"
            className="settings-dialog__close"
            aria-label="关闭赞助"
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
        <p className="donate-dialog__headline">感谢支持 L-Mechrevo</p>
        <p className="donate-dialog__subtitle">扫码赞助，支持持续开发</p>
        <div className="donate-dialog__qr-grid">
          <div className="donate-dialog__qr-slot">
            <QrSlot src={sources[0]} label="赞助二维码 1" />
          </div>
          <div className="donate-dialog__qr-slot">
            <QrSlot src={sources[1]} label="赞助二维码 2" />
          </div>
        </div>
      </div>
    </div>
  )
}
