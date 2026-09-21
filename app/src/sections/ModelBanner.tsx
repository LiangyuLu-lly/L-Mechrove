import "./ModelBanner.css"

export type ModelBannerProps = {
  readonly writeAllowed: boolean
  readonly modelReason: string
  readonly projectId: string
}

const READ_ONLY_COPY = "机型只读：无法识别机型，已进入只读模式。"

export function ModelBanner({ writeAllowed, modelReason }: ModelBannerProps) {
  if (writeAllowed) {
    return null
  }

  return (
    <div className="model-banner">
      <p className="model-banner__copy" role="status">
        {READ_ONLY_COPY}
        {modelReason !== "" ? (
          <span className="model-banner__reason">{modelReason}</span>
        ) : null}
      </p>
    </div>
  )
}
