export type FirstRunProps = {
  readonly onLater: () => void
  readonly onGoSystem: () => void
}

const STEPS = [
  {
    marker: "1",
    title: "无需安装任何其他控制台",
    body: "安装器已经装好 GCU 服务与驱动，本程序自带全部必要组件。厂商「官方控制台」已由安装器清理并替换为 L-Mechrevo，不需要再下载或安装它。",
    warning: false,
  },
  {
    marker: "2",
    title: "直接开始使用",
    body: "打开本程序的“系统”页即可设置开机启动、性能模式、显卡模式与灯效；GCU 服务在后台运行，无需额外操作。",
    warning: false,
  },
  {
    marker: "!",
    title: "退出其他灯效控制软件",
    body: "不要同时运行 BetterRGB、OpenRGB 或其他厂商灯效程序，否则多个程序抢占 HID 设备可能导致灯效失效或设备访问冲突。",
    warning: true,
  },
] as const

export function FirstRun({ onLater, onGoSystem }: FirstRunProps) {
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
        {STEPS.map((step) => (
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
