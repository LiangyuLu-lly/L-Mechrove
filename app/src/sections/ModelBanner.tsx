import { useState } from "react"
import { setProjectId } from "../lib/api"
import "./ModelBanner.css"

export type ModelBannerProps = {
  readonly writeAllowed: boolean
  readonly modelReason: string
  readonly projectId: string
}

const READ_ONLY_COPY = "机型只读：无法识别机型，已进入只读模式。"

export function ModelBanner({
  writeAllowed,
  modelReason,
  projectId,
}: ModelBannerProps) {
  const [draft, setDraft] = useState(projectId)

  if (writeAllowed) {
    return null
  }

  function onApply(): void {
    void setProjectId(draft)
  }

  return (
    <div className="model-banner">
      <p className="model-banner__copy" role="status">
        {READ_ONLY_COPY}
        {modelReason !== "" ? (
          <span className="model-banner__reason">{modelReason}</span>
        ) : null}
      </p>
      <div className="model-banner__row">
        <label className="model-banner__label" htmlFor="model-override">
          手动机型
        </label>
        <input
          id="model-override"
          className="model-banner__input"
          value={draft}
          onChange={(event) => {
            setDraft(event.target.value)
          }}
        />
        <button
          type="button"
          className="model-banner__apply"
          onClick={onApply}
        >
          应用
        </button>
      </div>
    </div>
  )
}
