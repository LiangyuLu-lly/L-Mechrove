# UI 响应式测试流程

## 设计依据

- 使用 `PerMonitorV2`，并让窗体采用统一的 DPI 自动缩放模式。参考 [Windows Forms 高 DPI 支持](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms)。
- 文本区域使用 `AutoSize`，需要分配剩余空间的区域使用 `Dock`、`Anchor`、`TableLayoutPanel` 或 `FlowLayoutPanel`。参考 [Windows Forms 控件布局](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/layout)。
- 百分比列和自动内容行由 `TableLayoutPanel` 管理，避免用固定坐标拼接文本和按钮。参考 [TableLayoutPanel](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/tablelayoutpanel-control-windows-forms)。
- 顶层窗口在 DPI 变化后重新限制到当前显示器工作区；内容超过工作区时使用内部纵向滚动，不允许窗口或按钮落到屏幕外。

## 一键运行

```powershell
.\scripts\test-ui.ps1
```

指定 Release 配置和输出目录：

```powershell
.\scripts\test-ui.ps1 -Configuration Release -OutputPath .\artifacts\ui-audit-release
```

脚本先构建当前 WinForms 产品，再以 `--ui-audit` 启动只读 UI 模式。该模式不会初始化 MQTT、ACPI、BLE、键盘灯光或写入任何硬件设置。

## 覆盖矩阵

测试以下工作区、DPI 组合：

| 分辨率 | 缩放 |
| --- | --- |
| 1280x720 | 100%、150%、200% |
| 1366x768 | 125%、200% |
| 1600x900 | 125%、150% |
| 1920x1080 | 100%、125%、150%、175%、200% |
| 2560x1440 | 150%、200% |
| 3840x2160 | 175%、200% |

每组渲染主窗口、自定义性能、风扇曲线、屏幕校色、键盘 RGB、灯条、Logo 灯、赞助和颜色选择器。存在内部滚动区时同时输出顶部和底部截图。

## 自动失败条件

- 顶层窗口超出模拟工作区。
- 可见控件超出不可滚动父容器。
- 固定尺寸文字无法在控件内完整绘制。
- 同一父容器中的可见叶子控件互相覆盖。
- 出现非预期横向滚动条。
- 任一窗体构造、布局或截图失败。

结果写入 `ui-audit.json`、`ui-audit.md` 和 PNG 截图。只要存在一项问题，命令返回非零退出码。

## 人工视觉审批

自动检查通过后仍需查看截图：

1. 主窗口检查 1280x720 100% 以及 1920x1080 175%/200% 的顶部和底部。
2. 参数密集窗口检查自定义性能和键盘 RGB 的顶部、底部滚动状态。
3. 自绘页面检查两张风扇曲线、标题、坐标和底部操作区。
4. 检查屏幕校色、灯光、赞助和颜色选择器在 100%/200% 下的文字、按钮和图像比例。
5. 确认截图非空、无裁切、无覆盖、无横向滚动，并且主要操作在纵向滚动后可到达。

发布前必须同时满足：UI 审计为零、人工视觉审批通过、解决方案 Release 构建和现有测试通过。
