// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Battery;
using MechrevoLite.Display;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.Mode;
using MechrevoLite.Properties;
using MechrevoLite.UI;
using System.Diagnostics;
using System.Timers;

namespace MechrevoLite
{
    public partial class SettingsForm : RForm
    {
        internal static readonly Size CompactDashboardLogicalClientSize = new(420, 529);

        // 紧凑化（2026-09-11，用户报「页面不够紧凑、占了很多无用空间」）：
        // 这些高度此前是各自拍出来的（分段行 42 与 48 两套、卡片比内容高出一圈），
        // 现在统一收到「刚好装下内容」，并由 --ui-audit 的越界检查兜底。
        // 分段行 / 标题行 / 常用页三张卡的高度**保持原值的字面量**：那些是未缩放数字，
        // 改成 D(…) 后在 175% 缩放下反而变大，把卡内内容挤爆（实测 248 条 text-clipping
        // + 288 条 parent-overflow，见 docs/ui-consistency-pass.md §3.3）。紧凑化只动
        // 真正有余量的三张卡。
        internal const int CompactOfficialConsoleLogicalHeight = 56;     // 官方控制台卡（46 在 420 宽下状态文字上下贴边，3840-200% 实测裁切）

        // 这两张卡的内部行是按内容顶死的（液冷卡是三行控件），
        // 2026-09-11 压到 84/88 时 --ui-audit 直接报出 376 条越界，故维持原值。
        // 液冷卡 106→116：灯光按钮高度跟随下拉首选高（字体驱动），420 窄卡内
        // Percent 行在 125% 附近缩放点只剩 ±1px 余量，被审计判溢出；加高 10 逻辑。
        internal const int LiquidCoolingLogicalHeight = 112;

        /// <summary>
        /// 快捷开关分组（顺序即界面顺序）。键必须覆盖 <c>BuildQuickSwitchPanel</c> 里 items 的
        /// 全部条目——漏一个就是建面板时抛异常、启动即崩，所以 GroupOf() 找不到组时直接抛。
        /// </summary>
        static readonly (string Title, string[] Keys)[] QuickSwitchGroups =
        {
            ("输入设备", new[] { "touchpad", "touchpadtoggle", "wifi", "bt", "webcam", "numpad" }),
            ("键盘与热键", new[] { "winkey", "fnkey", "copilot", "osd" }),
            ("电源与系统", new[] { "usb", "highperf", "fanboost", "acrecovery", "cpuadvperf", "gamewhitelist", "taskbarautohide", "transparency", "darktheme", "deepsleep", "monitoroff", "startup" }),
        };

        /// <summary>组标题 → 容器，整组没有可见项时连标题一起收掉（见 SyncQuickSwitchVisibility）。</summary>
        readonly Dictionary<string, (Control Header, Control Grid)> _quickGroupContainers = new();
        internal static readonly (float Label, float Slider, float Value) BrightnessColumnPercentages = (26F, 60F, 14F);
        internal static readonly (float Title, float Day, float Night) ThemeColumnPercentages = (60F, 20F, 20F);

        ContextMenuStrip contextMenuStrip = new CustomContextMenu();
        ToolStripMenuItem menuEco, menuStandard, menuUltimate, menuOptimized;
        DonateControl donateControl;

        public GPUModeControl gpuControl;
        AutoUpdateControl updateControl;

        Label? _batteryLimitValue;
        RButton? buttonSilentTurbo;   // 静音狂暴（Turbo 静音子模式）
        RButton? buttonCustomMode;    // 自定义性能模式入口（打开 CustomModeForm）
        CustomModeForm? customModeForm;
        TableLayoutPanel? _dashboard;
        BufferedPanel? _dashboardScroll;
        BufferedTableLayoutPanel? _dashboardStack;
        RScrollBar? _dashboardScrollBar;
        RCollapseGroup? _liquidGroup;
        RCollapseGroup? _quickGroup;
        RCollapseGroup? _lightGroup;
        BufferedPanel? _telemetryPanel;
        // GCU 连接状态指示条（页面栈首行，真值 = Program.hw 的连接快照）。
        BufferedPanel? _panelGcuStatus;
        Label? _labelGcuStatus;
        Label? _telemetryCpuName; Label? _telemetryCpuTemp; Label? _telemetryCpuRest;
        Label? _telemetrySeparator;
        Label? _telemetryGpuName; Label? _telemetryGpuTemp; Label? _telemetryGpuRest;
        bool _telemetryLayingOut;
        TableLayoutPanel? _hzSegTable;
        // 屏幕卡的固定行高表（26 行头 / 34 Hz 分段 / 28 亮度）。能力门禁隐藏某行内容时，
        // 必须把对应 RowStyle 高度一起收为 0——只藏子控件会留下固定高的空带（run5 门禁审计）。
        TableLayoutPanel? _screenRowsLayout;
        static readonly int[] ScreenRowHeights = { 26, 34, 28 };
        readonly int[] _screenRowVisibleHeights = new int[3];   // 隐藏前采样到的渲染行高（0=未采样）
        readonly List<RButton> _hzButtons = new();
        string _lastHzSignature = "";
        RCheckBox? _kbPowerSw;
        RCheckBox? _lbPowerSw;
        RCheckBox? _logoPowerSw;
        bool _lightChannelKeyboard;
        bool _lightChannelLightbar;
        bool _lightChannelLogo;
        ComboBox? _kbEffectCombo; ComboBox? _lbEffectCombo; ComboBox? _logoEffectCombo;
        // 键盘控制器状态行（判定为「不支持」时提示已改走官方通道；Supported/Unknown 隐藏）。
        Label? _lblKeyboardControllerStatus;
        bool _syncingEffectCombos;
        // 灯光组头右侧的两个全局灯光设置（用户 2026-09-13：从 RgbForm 移入组头）。
        RCheckBox? _lightOffOnBatteryChk;
        RComboBox? _lightSleepCombo;
        bool _syncingLightHeader;
        // I3：更多开关卡片的拟合动作（BuildQuickSwitchPanel 里以闭包写入），展开后再跑一次。
        Action? _fitQuickCards;
        // 键盘命令代际：快速关→开时用旧代际的续体不得再启动 HID（I2）。
        int _kbCmdGen;
        // 在途的固件键盘电源旁路任务：OFF 路径先有界等待它，避免迟到的补开电把 OFF 覆盖掉（I2）。
        Task? _kbPowerBypassInFlight;   // 效果下拉回显同步中：不触发下发命令
        SettingsDialog? _settingsDialog;
        RCheckBox? _autoHzChk;
        RCheckBox? _overdriveChk;
        // 「显示」组（设置弹窗）里响应加速行的可用性，唯一真相源：能力刷新时更新，
        // 弹窗懒建时按它决定该组标题是否渲染。
        bool _overdriveAvailable;
        RComboBox? _calibCombo;
        Button? _dayModeButton;
        Button? _nightModeButton;
        Panel? _themeModePanel;
        Control[] _dashboardSections = Array.Empty<Control>();
        readonly HashSet<Control> _enabledDashboardSections = new();
        Panel? _officialConsolePanel;
        Label? _officialConsoleStatus;
        RButton? _officialConsoleButton;
        readonly System.Windows.Forms.Timer _officialConsoleStatusTimer = new() { Interval = 3000 };
        bool _arrangingDashboard;
        string _lastStackLayout = "";
        string _lastWindowHeightSignature = "";
        readonly MechrevoDeviceCapabilities _deviceCapabilities = MechrevoDeviceCapabilities.Load();
        string _lastCapabilityLayout = "";

        static IEnumerable<Control> AllDescendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control descendant in AllDescendants(child))
                    yield return descendant;
            }
        }

        /// <summary>
        /// v2 行头（DESIGN.md §0.2）：[图标 16][名称][右侧状态] 三列。
        /// 图标用 PictureBox（Label.Image 与 Text 无法并排，见 UiVisualStyle.RenderGlyph 注释）。
        /// </summary>
        BufferedTableLayoutPanel BuildHeadRow(Control iconBox, Label nameLabel, Control? statusControl, Control? statusControl2 = null)
        {
            int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
            var head = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = (iconBox is null ? 2 : 3) + (statusControl2 is not null ? 1 : 0),
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = UiVisualStyle.Window,
            };
            int col = 0;
            if (iconBox is not null)
            {
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(26)));
                iconBox.Dock = DockStyle.Fill;
                iconBox.Margin = Padding.Empty;
                head.Controls.Add(iconBox, col++, 0);
            }
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nameLabel.Dock = DockStyle.Fill;
            nameLabel.AutoSize = false;
            nameLabel.Margin = Padding.Empty;
            nameLabel.TextAlign = ContentAlignment.MiddleLeft;
            head.Controls.Add(nameLabel, col++, 0);
            // 第二状态控件排在 statusControl **左侧**（屏幕行头：校色下拉 + 自动刷新率开关并排）。
            if (statusControl2 is not null)
            {
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                statusControl2.Dock = DockStyle.Fill;
                statusControl2.Margin = new Padding(0, 0, D(4), 0);
                head.Controls.Add(statusControl2, col++, 0);
            }
            if (statusControl is not null)
            {
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                statusControl.Dock = DockStyle.Fill;
                statusControl.Margin = new Padding(D(6), 0, 0, 0);
                if (statusControl is Label statusLabel) statusLabel.TextAlign = ContentAlignment.MiddleRight;
                else if (statusControl is CheckBox statusCheck) statusCheck.TextAlign = ContentAlignment.MiddleRight;
                head.Controls.Add(statusControl, col++, 0);
            }
            head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return head;
        }

        PictureBox BuildHeadIcon(UiGlyph.Kind kind)
        {
            var pic = new PictureBox
            {
                Name = "headIcon_" + kind,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiVisualStyle.Window,
                Margin = Padding.Empty,
            };
            // 走 ApplyGlyph 把图标种类记到 Tag 上：此后每次主题重刷 ApplyTree 都会
            // 按 Tag 重渲染图标并重算底色；直接 Render 则 Tag 为空，切换后图标位图
            // 和底色都停留在构建期主题（2026-09-13 真机实测的行头残色块之一）。
            UiVisualStyle.ApplyGlyph(pic, kind);
            return pic;
        }

        /// <summary>
        /// v2 行皮肤（DESIGN.md §0.2）：全部递归分区的面板去卡片化，容器递归涂 Window 底。
        /// 构建期调用一次（保证所有窗体/所有捕获一致）；ApplyThemeMode 主题重刷时再调一次。
        /// </summary>
        void ApplyRowSkins()
        {
            foreach (Control section in _dashboardSections)
            {
                // 分区根自身也要刷（AllDescendants 不含根——漏掉它，日间重刷后根底色仍是夜间色）
                UiVisualStyle.ApplyRow(section);
                foreach (Control control in AllDescendants(section))
                {
                    // 所有容器（含普通 Panel/TableLayoutPanel，不只是 BufferedPanel）都递归涂
                    // Window 底——行头/遥测/灯控行是普通 Panel 或 BufferedTableLayoutPanel，
                    // 漏掉就是日间模式下的深色残块（2026-09-13 真机实测）。
                    if (control is Panel or TableLayoutPanel or FlowLayoutPanel)
                    {
                        if (control is BufferedPanel card and not RCollapseGroup) card.CardStyle = false;
                        UiVisualStyle.ApplyRow(control);
                    }
                }
            }
            // 页面宿主（自绘滚动容器）与 footer 在 _dashboardSections 之外，同样跟主题重刷。
            if (_dashboardScroll is not null) UiVisualStyle.ApplyRow(_dashboardScroll);
            if (_dashboardScrollBar is not null) _dashboardScrollBar.BackColor = UiVisualStyle.Window;
            UiVisualStyle.ApplyRow(panelFooter);
        }

        internal void ApplyThemeMode(bool night)
        {
            if (Program.UiAuditMode) UiVisualStyle.SetAuditNightMode(night);
            else UiVisualStyle.SetNightMode(night);
            InitTheme();
            InitContextMenuTheme();
            if (_dashboard is not null) _dashboard.BackColor = UiVisualStyle.Window;
            if (_dashboardStack is not null) _dashboardStack.BackColor = UiVisualStyle.Window;
            if (_liquidGroup is not null) _liquidGroup.BackColor = UiVisualStyle.Window;
            if (_quickGroup is not null) _quickGroup.BackColor = UiVisualStyle.Window;
            UiVisualStyle.ApplyWindow(this);
            // 主题/官方控制台/版本三张卡的标题是内联局部变量，按控件名在这里补线性图标。
            foreach ((string name, UiGlyph.Kind kind) in new[]
            {
                ("labelThemeModeTitle", UiGlyph.Kind.Contrast),
                ("labelOfficialConsoleTitle", UiGlyph.Kind.Console),
            })
            {
                if (Controls.Find(name, true).FirstOrDefault() is Label titleLabel)
                    UiVisualStyle.ApplyGlyph(titleLabel, kind);
            }
            UiVisualStyle.ApplyGlyph(labelVersion, UiGlyph.Kind.Info);
            // 标题图标：统一 GDI+ 线性（DESIGN.md §8 禁 emoji/位图图标）。四张 Designer 卡片的
            // 图标在构建期挂（见 BuildDashboard 里 panelCPUTitle/panelGPUTitle 附近）——
            // 构建期挂的会被 AutoSize 算进首选宽度，构造期挂的会被裁掉（实测差异）。
            // 那四个 PictureBox 实测运行期 vis=False，挂上去也不画，所以直接隐藏、改用 Label.Image。
            picturePerf.Visible = false;
            pictureGPU.Visible = false;
            pictureBattery.Visible = false;
            pictureScreen.Visible = false;
            ApplyRowSkins();
            RefreshThemeButtons();
            // 二级窗体是缓存实例（设置弹窗/校色/灯效/更新/赞助……）：主窗切主题时
            // 它们不会重建，必须逐个重刷，否则日间模式下弹窗/二级窗整窗残留夜间底
            //（2026-09-13 真机实测）。OSD 两窗（Toast/HardwareOverlay）自绘豁免 token，
            // 不继承 RForm 主题路径，天然跳过。
            foreach (Form owned in OwnedForms)
            {
                if (owned is not RForm themed || owned.IsDisposed) continue;
                themed.InitTheme();
                UiVisualStyle.ApplyWindow(themed);
                UiVisualStyle.RetintChrome(themed);
            }
            UpdateDashboardWindowHeight();
            Invalidate(true);
        }

        void RefreshThemeButtons()
        {
            bool night = UiVisualStyle.IsNightMode;
            SetThemeButtonState(_dayModeButton, !night);
            SetThemeButtonState(_nightModeButton, night);
        }

        static void SetThemeButtonState(Button? button, bool selected)
        {
            if (button is null) return;
            button.BackColor = selected ? UiVisualStyle.Accent : UiVisualStyle.SurfaceRaised;
            button.ForeColor = selected ? UiVisualStyle.AccentText : UiVisualStyle.Text;
            button.FlatAppearance.BorderColor = selected ? UiVisualStyle.Accent : UiVisualStyle.Border;
            button.FlatAppearance.BorderSize = selected ? 2 : 1;
            button.AccessibleDescription = selected ? "Selected" : null;
        }
        CheckBox[] _quickSwitches = Array.Empty<CheckBox>();
        CheckBox? _usbChargerBox;
        CheckBox? _fanBoostBox;   // 风扇增强（从性能面板移入快捷开关）
        // 两个单行按钮条各自的「上次实际铺的布局」。相同就不动控件树——
        // Controls.Clear() 加重加会让按钮闪一下，而这几个重排是被周期性调用的。
        string _lastPerformanceLayout = "";
        string _lastGpuLayout = "";
        // 电源指示灯亮度：与开关同一条官方命令族，界面上也放在开关旁边
        /// <summary>
        /// 亮度档位。官方是连续滑块（0..100），这里用离散档避免拖动过程中把
        /// 每个中间值都发出去——每条命令都要等服务端回读确认，连发会互相排队超时。
        /// </summary>

        /// <summary>
        /// 应用任务栏/透明/深色三项。返回是否真的生效（内部已回读确认）。
        /// </summary>
        static bool ApplyWindowsPersonalizationSwitch(string key, bool requested) => key switch
        {
            "taskbarautohide" => ShellPersonalization.SetTaskbarAutoHide(requested),
            "transparency" => ShellPersonalization.SetTransparencyEnabled(requested),
            "darktheme" => ShellPersonalization.SetDarkTheme(requested),
            _ => false,
        };

        /// <summary>
        /// 读取这三项的当前值。返回 null 表示读不到（例如注册表值不存在）。
        /// </summary>
        internal static bool? ReadWindowsPersonalizationSwitch(string key) => key switch
        {
            "taskbarautohide" => ShellPersonalization.IsTaskbarAutoHide(),
            "transparency" => ShellPersonalization.IsTransparencyEnabled(),
            "darktheme" => ShellPersonalization.IsDarkTheme(),
            _ => null,
        };


        enum DgpuPreflightResult
        {
            Continue,
            Cancelled,
            RestartRequested,
        }
        DateTime _lastQuickSwitchUi = DateTime.MinValue;   // 快捷开关最近一次用户点击：抑制回显弹回（命令生效/回读有延迟）
        bool _syncingSwitches;   // 回显同步中：不触发命令/弹窗（防深度睡眠 pending 状态翻转误触发）
        // 深度睡眠：最后一次用户选择的值（-1=无待生效改动，0=已请求关，1=已请求开）。
        // 与 AppConfig["deepsleep_pending"] 同步——该项的回读要重启才更新，跨会话要靠它保持诚实。
        int _deepSleepPendingValue = -1;
        bool _syncingDisplay;    // 刷新率/显示增强回显同步：防回读触发命令循环
        DateTime _lastHzUi = DateTime.MinValue;   // 用户最近一次手选刷新率时间：12s 内不回显覆盖（防写回滞后弹回）
        bool _syncingLc;         // 液冷档位回显同步
        bool _lcConnecting;      // 自动连接流程进行中（防重入 + 定时器跳过状态覆盖）
        bool _lcAutoTried;       // 启动自动连接仅首次 Show 执行（隐藏/重开不重复扫描）
        bool _lcStatusTicking;   // async 定时回调防重入
        bool _lcWasConnected;
        bool _lcManualDisconnect;
        int _lcReconnectAttempts;
        DateTime _lcLastReconnectAttempt = DateTime.MinValue;
        int _lcGcuConnectAttempts;
        DateTime _lcLastGcuConnectAttempt = DateTime.MinValue;
        DateTime _lcLastGcuStatusRequest = DateTime.MinValue;
        int _lcGcuRestoreGeneration = -1;
        int _lcGcuRestoreAttempts;
        DateTime _lcGcuLastRestoreAttempt = DateTime.MinValue;
        SystemBluetoothConnectionObservation _lcSystemBluetoothObservation;
        int _performanceRequest; // Rapid clicks use latest-request-wins semantics.
        readonly BrightnessCommitQueue _brightnessCommitQueue = new(
            (value, token) => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                ScreenBrightness.Set(value);
            }, token),
            TimeSpan.FromMilliseconds(120),
            ex => Logger.WriteLine("Screen brightness update failed: " + ex.Message));
        readonly System.Windows.Forms.Timer _liquidCoolingStatusTimer = new() { Interval = 3000 };
        readonly System.Windows.Forms.Timer _displayStatusTimer = new() { Interval = 3000 };
        readonly System.Windows.Forms.Timer _quickSwitchStatusTimer = new() { Interval = 3000 };
        LiquidCoolingLightMenu? _liquidCoolingLightMenu;
        DateTime _lastBrightnessUi = DateTime.MinValue;
        DateTime _lastCalibUi = DateTime.MinValue;   // 校色下拉用户操作后的回显抑制（同亮度 3s 规则）
        int _runtimeResourcesDisposed;
        int _officialStatusRefreshRunning;
        int _startupStatusLoading;
        bool _syncingStartup;
        int _lastVisualMode = int.MinValue;
        bool _lastVisualSilentTurbo;

        RgbForm? rgbForm;
        LightForm? lightbarForm, logoLightForm;

        void OpenLightForm(string topic, string title, (string Effect, string Name)[]? effects = null)
        {
            var f = topic.Contains("Logo") ? (logoLightForm ??= new LightForm(topic, title, effects)) : (lightbarForm ??= new LightForm(topic, title, effects));
            if (f.IsDisposed) { f = topic.Contains("Logo") ? (logoLightForm = new LightForm(topic, title, effects)) : (lightbarForm = new LightForm(topic, title, effects)); }
            if (f.Visible) f.Close();
            else
            {
                AddOwnedForm(f);
                // 定位在 Show 之前完成：首帧就在主窗旁边，不再先画在默认位置再跳。
                ResponsiveLayout.ShowAdjacentTo(f, this);
            }
        }

        void OpenRgbForm()
        {
            if (rgbForm is null || rgbForm.IsDisposed) { rgbForm = new RgbForm(Program.rgb); AddOwnedForm(rgbForm); }
            if (rgbForm.Visible) rgbForm.Close();
            else ResponsiveLayout.ShowAdjacentTo(rgbForm, this);
        }

        void BuildQuickSwitchPanel()
        {
            if (Controls.ContainsKey("panelQuickSwitch")) return;
            // 卡片化 + 分组（docs/ui-consistency-pass.md §2.6/§2.7）。替换掉的原实现是
            // 「固定 180 高 + 绝对坐标 + 一个平铺 27 项的 FlowLayoutPanel」：卡片没有描边圆角
            // （普通 Panel 拿不到 CardStyle），末行只剩两三顶、右侧一片空洞，而且行内混着
            // 一个下拉框打断列对齐。现在改成：BufferedPanel 卡片 + 每组一个网格 + 组间小标题。
            var panel = new BufferedPanel
            {
                Name = "panelQuickSwitch",
                Dock = DockStyle.Top,
                CardStyle = true,
                BackColor = UiVisualStyle.Surface,
                Padding = new Padding(UiVisualStyle.Space.Lg, UiVisualStyle.Space.Md, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Md),
            };
            var root = new TableLayoutPanel
            {
                Name = "quickSwitchRoot",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = panel.BackColor,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // v2：「快捷开关」内部标题已删除——组头「更多开关」由折叠组件承载（预览 1:1）。
            panel.Controls.Add(root);

            var groupGrids = new Dictionary<string, FlowLayoutPanel>();
            _quickGroupContainers.Clear();
            int rootRow = 0;
            foreach ((string groupTitle, string[] keys) in QuickSwitchGroups)
            {
                var header = new Label
                {
                    Name = "labelQuickGroup_" + keys[0],
                    Text = groupTitle,
                    Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
                    ForeColor = UiVisualStyle.Muted,
                    AutoSize = true,
                    Margin = new Padding(0, rootRow == 1 ? 0 : UiVisualStyle.Space.Md, 0, UiVisualStyle.Space.Xs),
                };
                root.Controls.Add(header, 0, rootRow++);

                // 注意：这里不能给网格开 AutoSize。会换行的 FlowLayoutPanel 一旦 AutoSize，
                // 它按"不换行"报首选宽度，于是每行只放得下一项——真机截图抓到的就是这个
                // （见 docs/ui-consistency-pass.md §3.2 的返工记录）。所以沿用原实现的做法：
                // 宽度由 FitQuickGroups() 显式喂进去，高度按该宽度算首选高度。
                var grid = new FlowLayoutPanel
                {
                    Name = "quickGroup_" + keys[0],
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoScroll = false,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    BackColor = panel.BackColor,
                };
                root.Controls.Add(grid, 0, rootRow++);
                _quickGroupContainers[groupTitle] = (header, grid);
                foreach (string key in keys) groupGrids[key] = grid;
            }

            FlowLayoutPanel GroupOf(string key) => groupGrids.TryGetValue(key, out FlowLayoutPanel? grid)
                ? grid
                : throw new InvalidOperationException($"快捷开关 \"{key}\" 没有归组（见 groupMembership）");
            var items = new (string Key, string Label)[]
            {
                ("touchpad", "触摸板"), ("wifi", "WiFi"), ("bt", "蓝牙"), ("webcam", "摄像头"),
                // numpad 的解析、能力判定、下发动作三段一直都齐，唯独漏了这个界面入口，
                // 于是 Capabilities.Numpad 和 SwitchQuick 里的 "numpad" 分支都成了死代码。
                ("winkey", "Win键锁"), ("fnkey", "Fn键锁"), ("numpad", "小键盘锁"), ("osd", "OSD提示"), ("usb", "USB充电"),
                ("copilot", "Copilot键锁"), ("acrecovery", "来电自启"), ("highperf", "高性能电源"),
                ("fanboost", "风扇增强"),
                // 本轮新增两项（用户要求）：
                // 深度睡眠 = 官方 DeepSleepSwitch（Setting/Control DEEPSLEEP_ON/OFF），改动要重启才写回状态；
                // 息屏 = 一次性动作（压面板亮度 + SetThreadExecutionState 顶住待机），拨到 ON 立即执行并回弹。
                ("deepsleep", "深度睡眠"),
                // 本轮补齐的官方开关。全部按「服务端报过该字段」显示，机型没有就自动隐藏，
                // 所以这里可以无条件列出——可见性由 SyncQuickSwitchVisibility 按能力决定。
                ("touchpadtoggle", "触摸板切换键"),
                // GPU 静音（Whisper）不在这里：命令形状在官方二进制里无从取证，
                // 按猜测实现的开关经真机验证下发后回读毫无变化，是个空头开关，已撤。
                ("gamewhitelist", "游戏白名单"), ("cpuadvperf", "CPU高级性能"),
                // Windows 侧的三项（官方控制台同样有，同样不走 MQTT）。
                ("taskbarautohide", "任务栏自动隐藏"), ("transparency", "透明效果"), ("darktheme", "深色主题"),
                ("monitoroff", "息屏（不睡眠）"),
                // 开机自启动（用户要求补入口）：状态来自系统里真实的用户级计划任务，
                // 不经过 GCU，也没有机型差异——任何机器都能开机自启。
                ("startup", "开机自启动"),
            };
            _usbChargerBox = null;
            _fanBoostBox = null;
            // 面板会被重建，旧控件已随 flow 一起丢弃——不清空的话回显会往已 Dispose 的控件写。
            var boxes = new List<CheckBox>();
            async Task<bool> ApplyQuickSwitchAsync(CheckBox box, Func<bool, Task<bool>> command)
            {
                bool requested = box.Checked;
                // 禁用会把键盘焦点交给 Tab 顺序里的下一个控件，而重新启用**不会**还回来——
                // 用户看到的就是"拨动某个开关后，标注重点跳到它后面那个开关"
                //（2026-09-11 用户报告）。所以先记住它有没有焦点，收尾时还回去。
                bool hadFocus = box.Focused;
                // I4a：先把焦点停到父容器（分组网格：可编程聚焦、不画焦点环、不进 Tab 序），
                // 再禁用——直接禁用持有焦点的控件会让 WinForms 把焦点交给 Tab 序里的下一个
                // 开关，用户看到蓝色高光跳到右侧/下一行的开关，直到命令确认后才回来。
                if (hadFocus && box.Parent is { CanFocus: true } park) park.Focus();
                box.Enabled = false;
                bool confirmed = false;
                try { confirmed = await command(requested); }
                catch (Exception ex) { Logger.WriteLine("Quick switch failed: " + ex.Message); }
                if (!confirmed)
                {
                    // 回滚要按硬件此刻的真实状态，不能简单取反。
                    // 取反的隐含假设是「失败 = 什么都没变、原状态一定是 !requested」，
                    // 这两条都不成立：互斥开关（Uni/Omni）失败时伙伴可能已经被改掉，
                    // 而 UI 与硬件本来就可能不同步（回显有 12s 抑制窗）。
                    // 取反在这种情况下会把一个正确的勾选状态改成错的，
                    // 下一轮回显再纠正回来——用户看到的就是勾选框自己跳两下。
                    bool rollback = ReadQuickSwitchState((string)box.Tag) ?? !requested;
                    _syncingSwitches = true;
                    try { box.Checked = rollback; }
                    finally { _syncingSwitches = false; }
                }
                box.Enabled = true;
                if (hadFocus && !box.Focused && box.CanFocus) box.Focus();
                return confirmed;
            }
            // 回显同步：把 Checked 写回真实状态但不触发动作（动作只在 Click 上）。
            // 破坏性开关（开机自启动/深度睡眠）回滚/回显统一走这里，避免 _syncingSwitches 泄漏。
            void RevertCheck(CheckBox box, bool value)
            {
                _syncingSwitches = true;
                try { box.Checked = value; }
                finally { _syncingSwitches = false; }
            }
            // 宽度交给 RCheckBox 自己量（AutoSize）。曾试过"按最宽标签定宽"以求列对齐，
            // 但那条路要自己算 DPI 缩放，实测在审计缩放下算出的项宽超过半张卡片，反而退化成
            // 每行一项（见 docs/ui-consistency-pass.md §3.2）。列对齐是次要收益，换行不裁剪
            // 与分组清晰才是主要目标，所以这里不自己算宽度。
            foreach (var (key, label) in items)
            {
                // 深潜座舱：快捷开关统一用 RCheckBox 的开关外观（勾选语义不变）。
                var cb = new RCheckBox
                {
                    Name = "quick_" + key,
                    Text = label,
                    Tag = key,
                    ForeColor = UiVisualStyle.Text,
                    AutoSize = true,   // 宽度随内容（DPI 缩放下文字完整不截断）
                    Margin = new Padding(0, UiVisualStyle.Space.Xs, UiVisualStyle.Space.Sm, UiVisualStyle.Space.Xs),
                };
                // Windows 侧三项的初值直接读系统，且必须在挂事件之前设好，
                // 否则这次赋值会被当成用户操作再写回一次。
                bool? windowsInitial = ReadWindowsPersonalizationSwitch(key);
                if (windowsInitial.HasValue) cb.Checked = windowsInitial.Value;
                if (key == "usb")
                {
                    _usbChargerBox = cb;
                    cb.CheckedChanged += async (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        _lastQuickSwitchUi = DateTime.Now;
                        if (Program.service is not null && Program.hw is { IsConnected: true })
                            await ApplyQuickSwitchAsync(cb, Program.service.SwitchUsbCharger);
                    };
                }
                else if (key == "fanboost")
                {
                    // 风扇增强（从性能面板移入）：Fan/Status FanBoostEnable 回显
                    _fanBoostBox = cb;
                    cb.CheckedChanged += async (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        _lastQuickSwitchUi = DateTime.Now;
                        if (Program.service is not null && Program.hw is { IsConnected: true })
                            await ApplyQuickSwitchAsync(cb, Program.service.SwitchFanBoost);
                    };
                }
                else if (key == "deepsleep")
                {
                    // 深度睡眠（BIOS/EC 休眠策略，官方 DeepSleepSwitch）。真机实测：命令下发后
                    // Setting/Status 的回读要等重启才更新（FunctionVerifier 把这一项归为「需重启」，
                    // 官方界面同样弹重启提示），所以不能走 ApplyQuickSwitchAsync 的「未确认就回滚」——
                    // 那会把用户刚拨的开关弹回旧值，看起来像点了没生效。这里把用户的选择记进配置
                    // （deepsleep_pending：-1 无待生效改动，0/1 是待生效的值；测试走 LMECHREVO_CONFIG_FILE
                    // 隔离），回显按它显示，硬件报出同值（重启后）自动清除。
                    int pending = AppConfig.Get("deepsleep_pending", -1);
                    _deepSleepPendingValue = pending;
                    cb.Checked = pending >= 0 ? pending == 1 : ReadQuickSwitchState(key) == true;
                    // 破坏性副作用守卫链（与息屏同源）：深度睡眠是 EC/BIOS 状态写入，命令发出即生效
                    // （重启后写回）。只挂 Click（程序化写 Checked 不触发 Click），并要求
                    // GetLastInputInfo 显示刚刚有真实输入——回读同步、自动化写值都不会刷新它。
                    // 被拒绝时回滚到当前回显值，绝不把用户值留在半开。
                    cb.Click += async (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        bool requested = cb.Checked;
                        if (!NativeMethods.HasFreshUserInput())
                        {
                            Logger.WriteLine("Deep sleep switch ignored: no fresh user input.");
                            RevertCheck(cb, _deepSleepPendingValue >= 0
                                ? _deepSleepPendingValue == 1
                                : ReadQuickSwitchState(key) == true);
                            return;
                        }
                        _lastQuickSwitchUi = DateTime.Now;
                        if (Program.service is not { } svc || Program.hw is not { IsConnected: true }) return;
                        bool hadFocus = cb.Focused;
                        if (hadFocus && cb.Parent is { CanFocus: true } park) park.Focus();
                        cb.Enabled = false;
                        bool confirmed = false;
                        try { confirmed = await svc.SwitchDeepSleep(requested); }
                        catch (Exception ex) { Logger.WriteLine("Deep sleep switch failed: " + ex.Message); }
                        cb.Enabled = true;
                        if (hadFocus && !cb.Focused && cb.CanFocus) cb.Focus();
                        SetDeepSleepPending(requested ? 1 : 0);
                        RevertCheck(cb, requested);
                        Logger.WriteLine($"SwitchDeepSleep({requested}) confirmed={confirmed}; 重启后生效");
                        // 审计/测试模式不弹模态框：会阻塞消息泵、挡住脚本化点击（用户路径不变）。
                        if (!Program.UiAuditMode)
                            MessageBox.Show(
                                requested ? "深度睡眠已开启，重启后生效！" : "深度睡眠已关闭，重启后生效！",
                                "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                    };
                }
                else if (key == "monitoroff")
                {
                    // 一次性动作，不是持久状态：真实点击拨到 ON 才息屏，随后自动回到 OFF。
                    // 语义 =「息屏（不睡眠）」：把面板亮度压到最低做黑屏，同时用 SetThreadExecutionState
                    // 顶住 Modern Standby（ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED），首次真实输入或
                    // watchdog 到期即自动亮回。绝不广播 WM_SYSCOMMAND/SC_MONITORPOWER——本机只报
                    // S0 低功耗待机，那会让系统进 Connected Standby（真机已睡过一次，见
                    // Display/ScreenBlankController.cs 的注释）。不落配置、不回读。
                    //
                    // 此控件同样绝不能被程序化驱动：只挂 Click（程序化写 Checked 只触发 CheckedChanged、
                    // 不触发 Click），并要求 GetLastInputInfo 显示刚刚有真实输入：
                    //   1) _syncingSwitches 期间直接返回（回显同步不触发动作）；
                    //   2) 只有 OFF→ON 的翻转才动作；
                    //   3) 500ms 内确有真实输入才息屏；
                    //   4) 无论是否息屏都回弹 OFF、不落配置。
                    cb.Click += (_, _) =>
                    {
                        if (_syncingSwitches || !cb.Checked) return;
                        cb.Enabled = false;
                        try
                        {
                            if (NativeMethods.HasFreshUserInput())
                                ScreenBlankController.Dim();
                            else
                                Logger.WriteLine("Screen blank ignored: no fresh user input.");
                        }
                        catch (Exception ex) { Logger.WriteLine("Screen blank failed: " + ex.Message); }
                        finally
                        {
                            _syncingSwitches = true;
                            try { cb.Checked = false; }
                            finally { _syncingSwitches = false; }
                            cb.Enabled = true;
                        }
                    };
                }
                else if (key is "taskbarautohide" or "transparency" or "darktheme")
                {
                    // 这三项是 Windows 的设置，不经过 GCU：不检查连接、不用
                    // ApplyQuickSwitchAsync（它会等服务端回读确认），改完立刻自己回读。
                    string windowsKey = key;
                    cb.CheckedChanged += (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        bool requested = cb.Checked;
                        if (cb.Focused && cb.Parent is { CanFocus: true } cbPark) cbPark.Focus();   // I4a：同款停放，避免高光跳走
                        cb.Enabled = false;
                        try
                        {
                            bool applied = ApplyWindowsPersonalizationSwitch(windowsKey, requested);
                            if (!applied)
                            {
                                _syncingSwitches = true;
                                try { cb.Checked = !requested; }
                                finally { _syncingSwitches = false; }
                            }
                        }
                        finally { cb.Enabled = true; }
                    };
                    boxes.Add(cb);
                    GroupOf(key).Controls.Add(cb);
                    continue;
                }
                else if (key == "startup")
                {
                    // 开机自启动：真实状态来自系统里用户级自启动计划任务（复用 Helpers/Startup.cs），
                    // 不经过 GCU，也不需要管理员。初值必须在挂事件之前读，否则这次赋值会被当成用户
                    // 操作再写回一次。
                    //
                    // 破坏性副作用守卫链（与息屏同源）：加/删计划任务是不可逆的系统状态写入，只挂
                    // Click（程序化写 Checked 不触发 Click），并要求 GetLastInputInfo 显示刚刚有真实
                    // 输入——回读同步、自动化写值都不会刷新它。写入失败/被拒绝一律回滚到系统真实状态。
                    bool? initial = Startup.ReadScheduledState();
                    if (initial.HasValue) cb.Checked = initial.Value;
                    cb.Click += (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        bool requested = cb.Checked;
                        if (!NativeMethods.HasFreshUserInput())
                        {
                            Logger.WriteLine("Autostart switch ignored: no fresh user input.");
                            RevertCheck(cb, Startup.ReadScheduledState() ?? !requested);
                            return;
                        }
                        if (cb.Focused && cb.Parent is { CanFocus: true } park) park.Focus();   // I4a：同款停放，避免高光跳走
                        cb.Enabled = false;
                        try
                        {
                            bool applied = Startup.ApplyScheduledState(requested);
                            if (applied)
                            {
                                // 供 StartupCheckCore 的「任务缺失时自动重建」判据使用；不是回显来源。
                                AppConfig.Set("startup_enabled", requested ? 1 : 0);
                            }
                            else
                            {
                                RevertCheck(cb, Startup.ReadScheduledState() ?? !requested);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLine("Autostart switch failed: " + ex.Message);
                            RevertCheck(cb, Startup.ReadScheduledState() ?? !requested);
                        }
                        finally { cb.Enabled = true; }
                    };
                    boxes.Add(cb);
                    GroupOf(key).Controls.Add(cb);
                    continue;
                }
                else if (key is "gamewhitelist" or "cpuadvperf")
                {
                    // 这两项走 Fan/Control：开关值在载荷字段里而不是动作名后缀，
                    // 而且状态回读在 Fan/Status，所以不能并进通用的 SwitchQuick。
                    // service 在建界面时可能还没有，所以延迟到点击时再取。
                    bool whitelist = key == "gamewhitelist";
                    cb.CheckedChanged += async (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        _lastQuickSwitchUi = DateTime.Now;
                        if (Program.service is not { } svc || Program.hw is not { IsConnected: true }) return;
                        await ApplyQuickSwitchAsync(cb, whitelist
                            ? svc.SwitchGameWhitelist
                            : svc.SwitchCpuAdvancedPerformance);
                    };
                }
                else
                {
                    cb.CheckedChanged += async (_, _) =>
                    {
                        if (_syncingSwitches) return;
                        _lastQuickSwitchUi = DateTime.Now;
                        if (Program.service is not null && Program.hw is { IsConnected: true })
                            await ApplyQuickSwitchAsync(cb, requested => Program.service.SwitchQuick(key, requested));
                    };
                }
                GroupOf(key).Controls.Add(cb);
                boxes.Add(cb);
            }
            _quickSwitches = boxes.ToArray();
            // 换行的 FlowLayoutPanel 必须显式喂宽度（见上面网格处的注释），所以这里保留
            // 原实现那套「宽度变化后重算高度」的做法，只是从"一个 flow"改成"每组一个网格"。
            bool fittingQuickGroups = false;
            void FitQuickGroups()
            {
                if (fittingQuickGroups || panel.IsDisposed) return;
                fittingQuickGroups = true;
                try
                {
                    int width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal);
                    foreach (FlowLayoutPanel grid in groupGrids.Values.Distinct())
                    {
                        grid.Width = width;
                        grid.Height = grid.GetPreferredSize(new Size(width, 0)).Height;
                    }
                    root.PerformLayout();   // I3：先让根布局收敛，再按它的底边算卡片高度
                    panel.Height = root.Bottom + panel.Padding.Bottom;
                }
                finally { fittingQuickGroups = false; }
            }
            _fitQuickCards = FitQuickGroups;   // I3：供展开后的确定性重排调用
            panel.ClientSizeChanged += (_, _) => FitQuickGroups();
            foreach (FlowLayoutPanel grid in groupGrids.Values.Distinct())
                grid.Layout += (_, _) => FitQuickGroups();
            FitQuickGroups();
            // === 屏幕 行部件（v2 合并行，预览 1:1）===
            // 「屏幕」= [行头（标题 + 自动刷新率开关）] + [刷新率分段按钮] + [亮度滑条]，全部承载在 panelBrightness；
            // 响应加速/屏幕校色迁入设置弹窗（footer ⚙）。
            // 状态同步：每帧 SyncHzButtons() 按能力时钟/频率列表重建分段并点亮当前值。

            // 自动刷新率开关（用户 2026-09-13：从设置弹窗移入「屏幕」行头右侧；逻辑沿用）
            var autoHzChk = new RCheckBox
            {
                Name = "checkAutoRefreshRate",
                Text = "自动刷新率",
                ForeColor = UiVisualStyle.Text,
                AutoSize = true,
                Checked = Program.hw?.DcHz ?? false,
            };
            autoHzChk.CheckedChanged += async (_, _) =>
            {
                if (_syncingDisplay) return;
                if (Program.service is not null && Program.hw is { IsConnected: true })
                {
                    bool requested = autoHzChk.Checked;
                    autoHzChk.Enabled = false;
                    if (!await Program.service.SwitchAutoRefreshRate(requested))
                    {
                        _syncingDisplay = true;
                        autoHzChk.Checked = !requested;
                        _syncingDisplay = false;
                    }
                    autoHzChk.Enabled = true;
                }
            };
            _autoHzChk = autoHzChk;
            _hzSegTable = new BufferedTableLayoutPanel
            {
                Name = "tableHzButtons",
                Dock = DockStyle.Fill,
                ColumnCount = 0,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = UiVisualStyle.Window,
            };


            // 亮度滑条（Windows 标准亮度控制）+ 显示增强开关行（响应加速，收到状态字段后才显示）。
            // 行高 = 未缩放字面量：TLP 的 RowStyle 不参与审计的 form.Scale 相对缩放
            //（Control.Scale 只缩放控件尺寸不缩放布局样式——r6 diag 实测 D() 行高在
            // 100pct 视口残留 1.75×），而行内容（字体）由审计统一缩放；Percent 行吸收差值。
            // 这是本文件既有纪律（紧凑化轮 §3.3），不是新发明。
            // 屏幕 行（预览 1:1）：行头「屏幕」+ 自动刷新率开关 + 刷新率分段按钮 + 亮度滑条；
            // 响应加速/校色迁入设置弹窗（footer ⚙）。
            // 行高 = 字面量（RowStyle 不参与 form.Scale——紧凑化轮 §3.3 纪律）+ Percent 行吸收差值。
            var brightPanel = new BufferedPanel { Name = "panelBrightness", Dock = DockStyle.Top, Width = 414, BackColor = UiVisualStyle.Window };
            brightPanel.Padding = new Padding(
                ResponsiveLayout.LogicalToDevice(this, 16),
                ResponsiveLayout.LogicalToDevice(this, 2),
                ResponsiveLayout.LogicalToDevice(this, 12),
                0);
            var brightLayout = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = brightPanel.BackColor,
            };
            brightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, BrightnessColumnPercentages.Label));
            brightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, BrightnessColumnPercentages.Slider));
            brightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, BrightnessColumnPercentages.Value));
            brightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 行头
            brightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Hz 分段行
            brightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // 亮度滑条行
            _screenRowsLayout = brightLayout;
            brightPanel.Controls.Add(brightLayout);
            var brightLabel = new Label { Name = "labelScreenBrightness", Text = "亮度", Font = UiStyleBody(), ForeColor = UiStyleXXX(), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, BackColor = brightPanel.BackColor };
            // 深潜座舱：亮度滑条用自绘 RSlider（4px 轨道 + Accent 填充 + 圆环滑块），不再用原生 TrackBar。
            var brightSlider = new RSlider { Name = "sliderScreenBrightness", Minimum = 10, Maximum = 100, Dock = DockStyle.Fill, Margin = Padding.Empty };
            var brightPercent = new Label { Name = "labelScreenBrightnessValue", Text = "100%", ForeColor = UiVisualStyle.Muted, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", UiStyleCaption(), FontStyle.Regular, GraphicsUnit.Point), MinimumSize = new Size(44, 0), Margin = Padding.Empty, BackColor = brightPanel.BackColor };
            brightSlider.ValueChanged += (_, _) =>
            {
                int v = brightSlider.Value;
                brightPercent.Text = v + "%";   // 当前亮度百分比（拖动与回显同步更新）
                if (_syncingDisplay) return;   // 回显赋值不得触发 WMI 写
                _lastBrightnessUi = DateTime.UtcNow;
                if (Program.hw is null || Program.UiAuditMode) return;
                _brightnessCommitQueue.Submit(v);
            };
            brightSlider.KeyUp += (_, _) => _brightnessCommitQueue.Flush();
            // 行头「屏幕」+ 刷新率分段行（此前只装了滑条行，头两行一直空着）
            var screenTitle = new Label { Name = "labelScreenRow", Text = "屏幕", Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiVisualStyle.Text, BackColor = brightPanel.BackColor, Margin = Padding.Empty };
            UiVisualStyle.ApplyTitle(screenTitle);
            // 屏幕校色（2026-09-14 用户要求）：P3/AdobeRGB 在本机不生效，整个删除；二级校色弹窗
            // 移除，改为行头内联圆角下拉（RComboBox，与行头控件同皮肤），
            // 只有 默认/sRGB 两档，写入沿用原弹窗的 MechrevoService.SetColorCalibration
            //（原版协议 mode 1=默认 2=sRGB），回显读注册表/同帧上报的 CurrentColorCalibration。
            int DHead(int value) => ResponsiveLayout.LogicalToDevice(this, value);
            var calibCombo = new RComboBox
            {
                Name = "comboColorCalibration",
                AccessibleName = "屏幕校色",
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = "Key",
                // 宽度收窄（默认/sRGB 都很短）：给同行的「屏幕」标题留足宽度，
                // 下拉展开宽度单独放宽（DropDownWidth），不挤标题列。
                Width = DHead(52),
                DropDownWidth = DHead(110),
                NativeHeight = true,   // 不做 44 逻辑高的条目校准：行头只有 26 逻辑高，装不下会溢出
                Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
                BackColor = UiVisualStyle.Window,
                ForeColor = UiVisualStyle.Text,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
            };
            calibCombo.Items.Add(new KeyValuePair<string, int>("默认", 1));
            calibCombo.Items.Add(new KeyValuePair<string, int>("sRGB", 2));
            calibCombo.SelectedIndex = CalibComboIndexFor(ResolveColorCalibrationMode());
            calibCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (_syncingDisplay) return;
                if (calibCombo.SelectedItem is not KeyValuePair<string, int> kv) return;
                if (Program.service is null || Program.hw is not { IsConnected: true }) return;
                int requestedMode = kv.Value;
                int acceptedIndex = calibCombo.SelectedIndex;
                calibCombo.Enabled = false;
                bool ok = await Program.service.SetColorCalibration(requestedMode);
                calibCombo.Enabled = true;
                if (ok)
                {
                    _lastCalibUi = DateTime.UtcNow;
                    AppConfig.Set("calib_mode", ResolveColorCalibrationMode());
                }
                else
                {
                    _syncingDisplay = true;
                    calibCombo.SelectedIndex = acceptedIndex;
                    _syncingDisplay = false;
                }
            };
            _calibCombo = calibCombo;
            // 行头右侧两控件并排：校色下拉（statusControl2）在 自动刷新率 开关（statusControl）左侧，
            // 都直接进 BuildHeadRow 的 AutoSize 列（与开关同一套 TLP 单元格布局，审计无溢出）。
            var screenHead = BuildHeadRow(BuildHeadIcon(UiGlyph.Kind.Display), screenTitle, autoHzChk, calibCombo);
            brightLayout.SetColumnSpan(screenHead, 3);
            brightLayout.Controls.Add(screenHead, 0, 0);
            _hzSegTable.Dock = DockStyle.Fill;
            _hzSegTable.Margin = Padding.Empty;
            brightLayout.SetColumnSpan(_hzSegTable, 3);
            brightLayout.Controls.Add(_hzSegTable, 0, 1);
            brightLayout.Controls.Add(brightLabel, 0, 2);
            brightLayout.Controls.Add(brightSlider, 1, 2);
            brightLayout.Controls.Add(brightPercent, 2, 2);
            // 深潜座舱：开关用 RCheckBox 的开关外观（其余行为与 CheckBox 一致）。
            var overdriveChk = new RCheckBox
            {
                Name = "checkLcdOverdrive",
                Text = "响应加速",
                ForeColor = UiVisualStyle.Text,
                AutoSize = true,
                Margin = new Padding(0, 8, 14, 0),
                Visible = false,   // 收到 LCDOverdriveSwitch 字段（机型支持）后才显示
            };
            overdriveChk.CheckedChanged += async (_, _) =>
            {
                if (_syncingDisplay) return;
                if (Program.service is not null && Program.hw is { IsConnected: true })
                {
                    bool requested = overdriveChk.Checked;
                    overdriveChk.Enabled = false;
                    if (!await Program.service.SwitchLcdOverdrive(requested))
                    {
                        _syncingDisplay = true;
                        overdriveChk.Checked = !requested;
                        _syncingDisplay = false;
                    }
                    overdriveChk.Enabled = true;
                }
            };
            // 响应加速存储为字段，宿主 = 设置弹窗（footer ⚙）；校色已改为屏幕行头内联下拉。
            // 本卡只负数据两类可见的高频行（刷新率分段 + 亮度滑条）。
            _overdriveChk = overdriveChk;

            // 液冷系统面板（外接水冷机：状态 + 泵速/风扇档位，原版 BT_LC 协议）
            var lcPanel = new BufferedPanel { Name = "panelLc", CardStyle = true, Dock = DockStyle.Top, Width = 414, Height = 96, BackColor = UiVisualStyle.Surface, Padding = new Padding(12, 4, 12, 4) };
            var lcTitle = new Label { Text = "液冷系统", Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body, FontStyle.Bold), ForeColor = UiVisualStyle.Text, AutoSize = true, Location = new Point(20, 16) };
            UiVisualStyle.ApplyGlyph(lcTitle, UiGlyph.Kind.Droplet);
            // 连接状态按钮：点击打开蓝牙水冷连接窗口（原版匹配流程）
            // 迷你圆角读数（预览 .mini）：RButton 幽灵态（ctor 已置 BorderSize=0）+
            // 12 半逻辑圆角 + Border 描边；MouseOverBackColor 在 BackColor 之后赋值，
            // 否则会被 RButton 的 BackColorChanged 悬停重算覆盖。mini-chip 标记让
            // ApplyTree 重刷时保留幽灵语义（底色 Surface + 悬停抬亮），不套 Secondary 皮肤。
            var lcStatus = new RButton
            {
                Tag = "mini-chip",
                Text = "未连接",
                ForeColor = UiVisualStyle.Muted,
                // 无边框幽灵按钮：底色随父卡片（不能用 Color.Transparent——WinForms 的
                // "透明"要求父容器支持透明绘制，自绘容器上会画成黑条，见 DESIGN.md 日间模式缺陷）
                BackColor = UiVisualStyle.Surface,
                BorderColor = UiVisualStyle.Border,
                BorderRadius = 12,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0, MouseOverBackColor = UiVisualStyle.SurfaceRaised },
                AutoSize = false,
                Size = new Size(240, 28),
                Location = new Point(136, 17),
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand,
                Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
                AutoEllipsis = true,
            };
            lcStatus.TextChanged += (_, _) =>
            {
                toolTip.SetToolTip(lcStatus, lcStatus.Text);
                // v2 折叠组摘要：组头右侧只放一句话（长句+设备名会溢出被截断，真机首验）。
                // 完整状态仍显示在液冷行自己的 lcStatus 上，tooltip 可见全貌。
                if (_liquidGroup is not null)
                    _liquidGroup.Summary = LcSummaryText(lcStatus.Text);
            };
            // GCU 和本进程的 BLE 直连不能同时拥有同一个水冷设备。
            int CurrentCoolingTemperature()
            {
                var hw = Program.hw;
                if (hw is null) return 0;
                return LiquidCoolingAutoPolicy.ResolveCoolingTemperature(
                    hw.CpuTemp, hw.GpuTemp, hw.IsTemperatureFresh(LiquidCoolingAutoPolicy.TemperatureFreshness));
            }

            LiquidCoolingControlRoute GetLiquidCoolingRoute()
            {
                var hw = Program.hw;
                bool gcuControllable = hw is not null && hw.LcGcuControllable &&
                    hw.IsLcStatusFresh(TimeSpan.FromSeconds(15));
                return LiquidCoolingConnectionPolicy.ResolveRoute(
                    Program.ble is { IsConnected: true },
                    gcuControllable,
                    _lcSystemBluetoothObservation.IsConnected);
            }
            // GCU 通道（无 BLE 直连）下的自动模式 = 厂商固件曲线：
            // 下发 BT_LC/Control LC_FanCtrl=4 让设备进入 LC_CoolingAuto（实测回读 LC_CoolingAuto=true），
            // 固件自己按温度驱动泵与风扇（GCUService PumpFanAuto/RunCoolingAuto）。厂商泵协议没有独立
            // 自动档，官方界面同样只在风扇档位里提供「自动」，所以泵自动也走这条通道。
            //
            // 这里曾经是客户端按温度挑一个具体泵速档（0..2）周期下发：3 秒一次的具体档位会把固件曲线
            // 钉死在那一刻的读数上，负载升高时泵不再跟随固件升速——这正是「自动不升速」的根因。
            // 现在自动意图下只补发「进入自动」这一条命令，绝不下发具体档位（见 LiquidCoolingAutoPolicy）。
            async Task UpdateGcuAutomaticCoolingAsync()
            {
                if (Program.service is null || Program.hw is not { IsConnected: true } hw) return;
                var action = LiquidCoolingAutoPolicy.DecideGcuRefresh(
                    AppConfig.Get("lc_pump_profile", WaterCoolerBle.ProfileUnset) == WaterCoolerBle.ProfileAutomatic,
                    AppConfig.Get("lc_fan_profile", WaterCoolerBle.ProfileUnset) == WaterCoolerBle.ProfileAutomatic,
                    hw.LcFanControl);
                if (action == LiquidCoolingAutoPolicy.RefreshAction.EnableVendorAuto)
                    await Program.service.SwitchLcFanAuto();
            }
            string DescribeGcuLiquidCoolingState()
            {
                if (Program.hw is not { LcStatusSeen: true } hw) return "";
                string state = hw.LcConnectString switch
                {
                    "DeviceNotReady" => "设备未就绪",
                    "DeviceIsReady" => "设备就绪",
                    "Scanning" => "正在扫描",
                    "Connecting" => "正在连接",
                    "IsConnectable" => "等待连接",
                    "Connected" => "已连接",
                    "Disconnected" => "已断开",
                    _ => string.IsNullOrWhiteSpace(hw.LcConnectString) ? "等待状态" : hw.LcConnectString,
                };
                if (hw.LcConnected && hw.LcActionSupportReported && !hw.LcActionSupported)
                    state += "（只读）";
                // 行内只放短状态：设备名/MAC 会超出 lcStatus 列宽（AutoEllipsis 会截断），
                // 完整信息由 DescribeGcuLiquidCoolingDetail 走 tooltip。
                return "GCU 水冷 · " + state;
            }
            string DescribeGcuLiquidCoolingDetail()
            {
                if (Program.hw is not { LcStatusSeen: true } hw) return "";
                string state = DescribeGcuLiquidCoolingState();
                string device = string.IsNullOrWhiteSpace(hw.LcCurrentMac) ? hw.LcFwVersion : hw.LcCurrentMac;
                return string.IsNullOrWhiteSpace(device) ? state : state + " · " + device;
            }
            async Task ProbeSystemBluetoothConnectionAsync(bool force = false)
            {
                if (Program.ble is null)
                {
                    _lcSystemBluetoothObservation = default;
                    return;
                }
                _lcSystemBluetoothObservation = await Program.ble.ProbeSystemConnectionAsync(
                    Program.hw?.LcDeviceMacs, force);
            }
            async Task<bool> RequestGcuLiquidCoolingStatusAsync(bool force = false)
            {
                if (Program.service is null || Program.hw is not { IsConnected: true }) return false;
                if (!force && DateTime.UtcNow - _lcLastGcuStatusRequest < TimeSpan.FromSeconds(6)) return true;
                _lcLastGcuStatusRequest = DateTime.UtcNow;
                return await Program.service.RefreshLiquidCoolingStatus();
            }
            async Task<bool> TryConnectGcuLiquidCoolingAsync(TimeSpan timeout)
            {
                if (Program.service is null || Program.hw is not { IsConnected: true } hw) return false;

                long statusBeforeRequest = hw.LcStatusVersion;
                await RequestGcuLiquidCoolingStatusAsync(force: true);
                if (hw.LcGcuControllable && hw.LcStatusVersion > statusBeforeRequest) return true;
                if (hw.LcActionSupportReported && !hw.LcActionSupported) return false;
                if (!await Program.service.LcConnect()) return false;
                await RequestGcuLiquidCoolingStatusAsync(force: true);
                return await hw.WaitForStateAsync(
                    () => hw.LcGcuControllable && hw.LcStatusVersion > statusBeforeRequest,
                    timeout);
            }
            async Task<bool> TryDirectLiquidCoolingAsync()
            {
                bool ok = Program.ble is not null && await Program.ble.AutoConnectAsync();
                if (!ok) return false;

                await Program.ble!.WaitForStartupStabilizationAsync(TimeSpan.FromSeconds(8));
                await Program.ble.RestoreSavedSettingsAsync(CurrentCoolingTemperature());
                _lcWasConnected = true;
                _lcReconnectAttempts = 0;
                lcStatus.Text = "已直连";
                toolTip.SetToolTip(lcStatus, "已直连 " + Program.ble.ConnectedName);
                lcStatus.ForeColor = UiVisualStyle.Ok;
                return true;
            }
            async Task RestoreSavedGcuLightingAsync()
            {
                if (Program.service is null || Program.hw is not { IsConnected: true } hw)
                    return;

                string profile = AppConfig.GetString("lc_light_profile", "") ?? "";
                bool directBleConnected = Program.ble is { IsConnected: true };
                if (!LiquidCoolingConnectionPolicy.ShouldRestoreSavedGcuLighting(
                        directBleConnected,
                        hw.LcGcuControllable,
                        hw.IsLcStatusFresh(TimeSpan.FromSeconds(15)),
                        profile))
                    return;

                int generation = hw.ConnectionGeneration;
                if (_lcGcuRestoreGeneration != generation)
                {
                    _lcGcuRestoreGeneration = generation;
                    _lcGcuRestoreAttempts = 0;
                    _lcGcuLastRestoreAttempt = DateTime.MinValue;
                }
                if (_lcGcuRestoreAttempts >= 2 ||
                    DateTime.UtcNow - _lcGcuLastRestoreAttempt < TimeSpan.FromSeconds(10))
                    return;

                if (profile is WaterCoolerBle.LightFanRotate or WaterCoolerBle.LightFanRainbow &&
                    !hw.LcFanLightingSupported)
                {
                    _lcGcuRestoreAttempts = 2;
                    Logger.WriteLine($"GCU 液冷灯效恢复跳过：当前设备不支持风扇灯效，profile={profile}");
                    return;
                }

                _lcGcuRestoreAttempts++;
                _lcGcuLastRestoreAttempt = DateTime.UtcNow;
                Color color = Color.FromArgb(
                    AppConfig.Get("lc_light_color", Color.FromArgb(0, 255, 255).ToArgb()));
                bool restored = await Program.service.LcApplyLightProfile(
                    profile, color, persist: false, requireReadback: true);
                if (restored) _lcGcuRestoreAttempts = 2;
                Logger.WriteLine($"GCU 液冷灯效恢复：generation={generation}, profile={profile}, sent={restored}, attempt={_lcGcuRestoreAttempts}");
            }
            async Task AutoConnectLc(bool silent)
            {
                if (_lcConnecting) return;
                _lcConnecting = true;
                if (!silent)
                {
                    lcStatus.Text = "连接水冷箱中…";
                    lcStatus.ForeColor = UiVisualStyle.Warn;
                }
                try
                {
                    await ProbeSystemBluetoothConnectionAsync(force: true);
                    if (GetLiquidCoolingRoute() == LiquidCoolingControlRoute.Gcu)
                    {
                        await RestoreSavedGcuLightingAsync();
                        lcStatus.Text = "GCU 已连接水冷";
                        lcStatus.ForeColor = UiVisualStyle.Ok;
                        return;
                    }

                    bool gcuAttempted = Program.hw is { IsConnected: true } && Program.service is not null;
                    if (gcuAttempted && await TryConnectGcuLiquidCoolingAsync(
                        silent ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4)))
                    {
                        await RestoreSavedGcuLightingAsync();
                        lcStatus.Text = "GCU 已连接水冷";
                        lcStatus.ForeColor = UiVisualStyle.Ok;
                        return;
                    }

                    // 本应用替代官方控制中心：GCU 通道没拿到控制权就自动回落我们自己的直连，
                    // 不询问，也不因 GCU/官方服务在线或报告了状态而放弃直连（跨切原则见 beta18 设计稿）。
                    bool gcuReportedLiquidCooling = Program.hw is { LcStatusSeen: true };
                    bool systemAlreadyConnected = _lcSystemBluetoothObservation.IsConnected;
                    if (LiquidCoolingConnectionPolicy.ShouldUseAutomaticDirectFallback(
                            Program.ble is { IsConnected: true }) &&
                        await TryDirectLiquidCoolingAsync())
                        return;

                    if (!silent)
                    {
                        lcStatus.Text = gcuReportedLiquidCooling || systemAlreadyConnected
                            ? "水冷连接失败（点击重试）"
                            : "未找到水冷箱（点击重试）";
                        lcStatus.ForeColor = UiVisualStyle.Warn;
                    }
                }
                catch (Exception ex)
                {
                    // BLE WinRT 异常不得穿透 async void（蓝牙关闭/被占用时会抛）
                    Logger.WriteLine("AutoConnectLc fail: " + ex.Message);
                    if (!silent)
                    {
                        lcStatus.Text = "连接失败（点击重试）";
                        lcStatus.ForeColor = UiVisualStyle.Danger;
                    }
                }
                finally { _lcConnecting = false; }
            }
            lcStatus.Click += async (_, _) =>
            {
                if (_lcConnecting) return;
                LiquidCoolingControlRoute route = GetLiquidCoolingRoute();
                if (route == LiquidCoolingControlRoute.DirectBle && Program.ble is { IsConnected: true })
                {
                    if (MessageBox.Show("断开蓝牙水冷连接？", "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        _lcManualDisconnect = true;
                        _lcReconnectAttempts = 3;
                        Program.ble.Disconnect();
                    }
                    return;
                }
                if (route == LiquidCoolingControlRoute.Gcu)
                {
                    lcStatus.Text = "正在刷新 GCU 水冷状态…";
                    lcStatus.ForeColor = UiVisualStyle.Warn;
                    await RequestGcuLiquidCoolingStatusAsync(force: true);
                    return;
                }
                _lcManualDisconnect = false;
                _lcReconnectAttempts = 0;
                await AutoConnectLc(false);
            };
            lcPanel.Controls.Add(lcTitle);
            lcPanel.Controls.Add(lcStatus);
            var lcPumpCombo = new ComboBox { Name = "comboLcPump", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Location = new Point(20, 55), DisplayMember = "Key", Enabled = false };
            // 泵速档同时设置 duty 与电压；仅改电压而把 duty 固定在 100% 会造成降档不生效。
            lcPumpCombo.Items.Add(new KeyValuePair<string, int>("自动", WaterCoolerBle.ProfileAutomatic));
            // 档位名与厂商协议对齐：官方泵速只有 3 档（45/60/90%），90% 即最高档（显示为「最大」）。
            // 「最高 100%」只是 BLE 直连协议的 12V 档，GCU 通道下发会被拒——不放进表里。
            // 百分比文本与组摘要共用 LiquidCoolingDisplayPolicy.PumpGearLabels 一张表。
            for (int idx = 0; idx < LiquidCoolingDisplayPolicy.PumpGearLabels.Length; idx++)
                lcPumpCombo.Items.Add(new KeyValuePair<string, int>(
                    LiquidCoolingDisplayPolicy.GearLabel(LiquidCoolingDisplayPolicy.PumpGearLabels[idx], idx, WaterCoolerBle.TopPumpProfile), idx));
            int FindProfileIndex(ComboBox combo, int profile)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                    if (combo.Items[i] is KeyValuePair<string, int> item && item.Value == profile) return i;
                return 0;
            }
            lcPumpCombo.SelectedIndex = FindProfileIndex(lcPumpCombo,
                Program.ble?.SavedPumpProfile ?? WaterCoolerBle.ProfileUnset);
            int acceptedPumpSelection = lcPumpCombo.SelectedIndex;
            lcPumpCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (_syncingLc) return;
                if (lcPumpCombo.SelectedItem is not KeyValuePair<string, int> kv) return;
                if (kv.Value == WaterCoolerBle.ProfileUnset) return;
                LiquidCoolingControlRoute route = GetLiquidCoolingRoute();
                lcPumpCombo.Enabled = false;
                bool ok = route switch
                {
                    LiquidCoolingControlRoute.DirectBle when Program.ble is { IsConnected: true } ble =>
                        await ble.ApplyPumpProfileAsync(kv.Value, CurrentCoolingTemperature()),
                    LiquidCoolingControlRoute.Gcu when Program.service is not null &&
                        LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(kv.Value) =>
                        await Program.service.SwitchLcFanAuto(),
                    LiquidCoolingControlRoute.Gcu when Program.service is not null && kv.Value is >= 0 and <= 2 =>
                        await Program.service.SwitchLcPump(kv.Value),
                    _ => false,
                };
                if (ok && route == LiquidCoolingControlRoute.Gcu)
                {
                    // GCU 通道原先不落配置，重启后自动/手动档位丢失、自动循环也读不到用户意图。
                    AppConfig.Set("lc_pump_profile", kv.Value);
                    await RequestGcuLiquidCoolingStatusAsync(force: true);
                }

                _syncingLc = true;
                try
                {
                    if (ok)
                    {
                        acceptedPumpSelection = lcPumpCombo.SelectedIndex;
                        lcStatus.Text = route == LiquidCoolingControlRoute.Gcu
                            ? $"泵速已确认：{kv.Key}"
                            : $"泵速命令已发送：{kv.Key}";
                    }
                    else
                    {
                        lcPumpCombo.SelectedIndex = acceptedPumpSelection;
                        lcStatus.Text = route == LiquidCoolingControlRoute.Gcu && kv.Value is WaterCoolerBle.ProfileAutomatic or 3
                            ? "GCU 未提供该泵速档位，已恢复原档位"
                            : "泵速写入失败，已恢复原档位";
                    }
                }
                finally
                {
                    _syncingLc = false;
                    lcPumpCombo.Enabled = GetLiquidCoolingRoute() is LiquidCoolingControlRoute.DirectBle or LiquidCoolingControlRoute.Gcu;
                }
            };
            lcPanel.Controls.Add(lcPumpCombo);
            var lcFanCombo = new ComboBox { Name = "comboLcFan", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Location = new Point(130, 55), DisplayMember = "Key", Enabled = false };
            lcFanCombo.Items.Add(new KeyValuePair<string, int>("自动", WaterCoolerBle.ProfileAutomatic));
            // 同上：官方风扇只有 4 档（40/50/60/90%），90% 即最大档（显示为「最大」）；值 4 在 GCU 通道是厂商
            // 自动档（LC_FanCtrl=4），不可能当手动 100% 用，所以表里不出现 100%。
            for (int idx = 0; idx < LiquidCoolingDisplayPolicy.FanGearLabels.Length; idx++)
                lcFanCombo.Items.Add(new KeyValuePair<string, int>(
                    LiquidCoolingDisplayPolicy.GearLabel(LiquidCoolingDisplayPolicy.FanGearLabels[idx], idx, WaterCoolerBle.TopFanProfile), idx));
            lcFanCombo.SelectedIndex = FindProfileIndex(lcFanCombo,
                Program.ble?.SavedFanProfile ?? WaterCoolerBle.ProfileUnset);
            int acceptedFanSelection = lcFanCombo.SelectedIndex;
            lcFanCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (_syncingLc) return;
                if (lcFanCombo.SelectedItem is not KeyValuePair<string, int> kv) return;
                if (kv.Value == WaterCoolerBle.ProfileUnset) return;
                LiquidCoolingControlRoute route = GetLiquidCoolingRoute();
                lcFanCombo.Enabled = false;
                bool ok = route switch
                {
                    LiquidCoolingControlRoute.DirectBle when Program.ble is { IsConnected: true } ble =>
                        await ble.ApplyFanProfileAsync(kv.Value, CurrentCoolingTemperature()),
                    LiquidCoolingControlRoute.Gcu when Program.service is not null &&
                        LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(kv.Value) =>
                        await Program.service.SwitchLcFanAuto(),
                    LiquidCoolingControlRoute.Gcu when Program.service is not null && kv.Value is >= 0 and <= 3 =>
                        await Program.service.SwitchLcFan(kv.Value),
                    _ => false,
                };
                if (ok && route == LiquidCoolingControlRoute.Gcu)
                {
                    AppConfig.Set("lc_fan_profile", kv.Value);
                    await RequestGcuLiquidCoolingStatusAsync(force: true);
                }

                _syncingLc = true;
                try
                {
                    if (ok)
                    {
                        acceptedFanSelection = lcFanCombo.SelectedIndex;
                        lcStatus.Text = route == LiquidCoolingControlRoute.Gcu
                            ? $"风扇档位已确认：{kv.Key}"
                            : $"风扇命令已发送：{kv.Key}";
                    }
                    else
                    {
                        lcFanCombo.SelectedIndex = acceptedFanSelection;
                        lcStatus.Text = route == LiquidCoolingControlRoute.Gcu && kv.Value is WaterCoolerBle.ProfileAutomatic or 4
                            ? "GCU 未提供该风扇档位，已恢复原档位"
                            : "风扇写入失败，已恢复原档位";
                    }
                }
                finally
                {
                    _syncingLc = false;
                    lcFanCombo.Enabled = GetLiquidCoolingRoute() is LiquidCoolingControlRoute.DirectBle or LiquidCoolingControlRoute.Gcu;
                }
            };
            lcPanel.Controls.Add(lcFanCombo);
            // 水冷灯光按钮：点击弹出效果选择菜单（原版 BT_LC LED 协议：呼吸/多彩/旋转色/彩虹/关闭）
            // 迷你圆角键（预览 .mini）：Surface 底 + 1px Border 描边，悬停提亮由 RButton 在
            // BackColor 变化后重算——所以 MouseOverBackColor 必须写在 BackColor 之后。
            // mini-chip 标记让 ApplyTree 重刷时按当前色板补迷你皮肤，而不是套 Secondary
            // 皮肤（SurfaceRaised 重刷会盖掉 Surface 底，见 UiVisualStyle.ApplyTree）。
            var lcLightBtn = new RButton
            {
                Name = "buttonLcLight",
                Tag = "mini-chip",
                Text = "水冷灯光",
                TextAlign = ContentAlignment.MiddleCenter,
                FlatStyle = FlatStyle.Flat,
                ForeColor = UiVisualStyle.Text,
                BackColor = UiVisualStyle.Surface,
                BorderColor = UiVisualStyle.Border,
                BorderRadius = 12,
                FlatAppearance = { BorderColor = UiVisualStyle.Border, MouseOverBackColor = UiVisualStyle.SurfaceRaised },
                AutoSize = false,
                AutoEllipsis = true,
                Font = UiStyleCaptionFont(),
                Cursor = Cursors.Hand,
                Enabled = true,
            };
            _liquidCoolingLightMenu = new LiquidCoolingLightMenu { Name = "liquidCoolingLightMenu" };
            LiquidCoolingLightMenu lcLightMenu = _liquidCoolingLightMenu;
                        Color CurrentLiquidCoolingLightColor()
            {
                if (GetLiquidCoolingRoute() == LiquidCoolingControlRoute.Gcu && Program.hw is
                    { LcLedRed: >= 0, LcLedGreen: >= 0, LcLedBlue: >= 0 } hw)
                    return Color.FromArgb(
                        Math.Clamp(hw.LcLedRed, 0, 255),
                        Math.Clamp(hw.LcLedGreen, 0, 255),
                        Math.Clamp(hw.LcLedBlue, 0, 255));
                return Program.ble?.SavedLightColor ??
                    Color.FromArgb(AppConfig.Get("lc_light_color", Color.FromArgb(0, 255, 255).ToArgb()));
            }
            bool SupportsLiquidCoolingFanLighting(LiquidCoolingControlRoute route) => route switch
            {
                LiquidCoolingControlRoute.DirectBle => Program.ble is { IsConnected: true, SupportsFanLed: true },
                LiquidCoolingControlRoute.Gcu => Program.hw?.LcFanLightingSupported == true,
                _ => false,
            };
            async Task ApplyLiquidCoolingLightAsync(string text, string profile, bool requiresFanLed, Color? color = null)
            {
                LiquidCoolingControlRoute route = GetLiquidCoolingRoute();
                if (route is LiquidCoolingControlRoute.None or LiquidCoolingControlRoute.BluetoothObserved)
                {
                    string gcuState = DescribeGcuLiquidCoolingState();
                    MessageBox.Show(route == LiquidCoolingControlRoute.BluetoothObserved
                            ? "Windows 已检测到水冷蓝牙连接，但 GCU 尚未报告可控制状态。请稍候刷新连接状态后重试。"
                            : string.IsNullOrWhiteSpace(gcuState)
                                ? "液冷系统未连接。"
                                : gcuState + "，暂不可控制。请等待 GCU 完成连接后重试。",
                        "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (requiresFanLed && !SupportsLiquidCoolingFanLighting(route))
                {
                    MessageBox.Show("该效果需要 LCT22002（Mk2）的风扇灯。当前水冷机仅支持头部灯效。",
                        "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Color effectiveColor = color ?? CurrentLiquidCoolingLightColor();
                bool ok = route switch
                {
                    LiquidCoolingControlRoute.DirectBle when Program.ble is { IsConnected: true } ble =>
                        await ble.ApplyLightProfileAsync(profile, effectiveColor),
                    LiquidCoolingControlRoute.Gcu when Program.service is not null =>
                        await Program.service.LcApplyLightProfile(profile, effectiveColor, requireReadback: true),
                    _ => false,
                };
                if (ok && route == LiquidCoolingControlRoute.Gcu)
                    await RequestGcuLiquidCoolingStatusAsync(force: true);
                lcStatus.Text = ok
                    ? route == LiquidCoolingControlRoute.Gcu && Program.hw?.LcLightingStatusSeen == true
                        ? $"灯效已确认：{text}"
                        : route == LiquidCoolingControlRoute.Gcu
                            ? $"灯效命令已发送：{text}"
                            : $"已应用灯效：{text}"
                    : "灯光写入失败";
            }
            void AddLightItem(string text, string profile, bool requiresFanLed = false)
            {
                // 走菜单工厂：与 LiquidCoolingLightMenuTests 的高度/分组契约同一条路径（B1 修复）。
                ToolStripMenuItem item = null!;
                item = lcLightMenu.AddItem(text, async () =>
                {
                    item.Enabled = false;
                    try { await ApplyLiquidCoolingLightAsync(text, profile, requiresFanLed); }
                    finally { item.Enabled = true; }
                }, requiresFanLed);
            }
            void AddCustomLightItem(string text, string profile)
            {
                lcLightMenu.AddItem(text, async () =>
                {
                    using var picker = new RColorPicker(CurrentLiquidCoolingLightColor()) { Text = text };
                    if (picker.ShowDialog(this) == DialogResult.OK)
                        await ApplyLiquidCoolingLightAsync(text, profile, requiresFanLed: false, picker.Color);
                });
            }
            // 分组（T5）：头部灯效 / 自定义颜色 / 风扇灯效（仅 Mk2）/ 关闭全部灯光。
            // 条目文案与 profile 保持不变，仅重排并加分组标题（旧版是一条平铺列表，观感差）。
            lcLightMenu.AddHeader("头部灯效");
            AddLightItem("青色常亮", WaterCoolerBle.LightCyanStatic);
            AddLightItem("青色呼吸", WaterCoolerBle.LightCyanBreath);
            AddLightItem("多彩", WaterCoolerBle.LightColorful);
            AddLightItem("彩色呼吸", WaterCoolerBle.LightColorfulBreath);
            lcLightMenu.AddHeader("自定义颜色");
            AddCustomLightItem("自定义颜色常亮…", WaterCoolerBle.LightCustomStatic);
            AddCustomLightItem("自定义颜色呼吸…", WaterCoolerBle.LightCustomBreath);
            lcLightMenu.AddHeader("风扇灯效（仅 Mk2）");
            AddLightItem("风扇旋转色（Mk2）", WaterCoolerBle.LightFanRotate, true);
            AddLightItem("风扇彩虹（Mk2）", WaterCoolerBle.LightFanRainbow, true);
            lcLightMenu.AddSeparator();
            AddLightItem("关闭全部灯光", WaterCoolerBle.LightOff);
            lcLightMenu.Opening += (_, _) =>
            {
                bool fanLedSupported = SupportsLiquidCoolingFanLighting(GetLiquidCoolingRoute());
                foreach (var item in lcLightMenu.FanLedItems) item.Enabled = fanLedSupported;
            };
            lcLightBtn.Click += async (_, _) =>
            {
                if (GetLiquidCoolingRoute() is LiquidCoolingControlRoute.None or LiquidCoolingControlRoute.BluetoothObserved)
                {
                    await ProbeSystemBluetoothConnectionAsync(force: true);
                }
                if (GetLiquidCoolingRoute() is LiquidCoolingControlRoute.None or LiquidCoolingControlRoute.BluetoothObserved)
                {
                    string gcuState = DescribeGcuLiquidCoolingState();
                    MessageBox.Show(_lcSystemBluetoothObservation.IsConnected
                            ? "Windows 已检测到水冷蓝牙连接，请等待 GCU 完成连接后再设置灯光。"
                            : string.IsNullOrWhiteSpace(gcuState)
                                ? "请先点击上方连接状态，连接水冷箱后再设置灯光。"
                                : gcuState + "，请等待连接完成后再设置灯光。",
                        "L-Mechrevo",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                lcLightMenu.Show(lcLightBtn, new Point(0, lcLightBtn.Height));
            };
            lcPanel.Controls.Add(lcLightBtn);
            lcPanel.Controls.Clear();
            // v2 液冷组内容（预览 1:1）：三子行 [名称][控件]；连接状态在组头摘要
            //（lcStatus.TextChanged 已接线）。内部标题行由组头「液冷」取代，lcTitle 停用。
            // 行高 = 字面量（RowStyle 不参与 form.Scale——紧凑化轮 §3.3 纪律）。
            var lcLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = lcPanel.BackColor,
            };
            // 紧凑三行（预览 .subrow 32px）：[行名 52][灯光键固定列 D(84)][下拉/状态填充列]。
            lcLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            lcLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResponsiveLayout.LogicalToDevice(this, 84)));
            lcLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lcLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            lcLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            lcLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Label MakeRowName(string name)
            {
                return new Label
                {
                    Text = name, AutoSize = true, Dock = DockStyle.Fill,
                    ForeColor = UiVisualStyle.Muted, BackColor = lcPanel.BackColor,
                    Font = UiStyleCaptionFont(), TextAlign = ContentAlignment.MiddleLeft,
                    Margin = Padding.Empty,
                };
            }
            lcTitle.Visible = false;   // 组头已承担名称语义
            lcPumpCombo.Dock = DockStyle.Fill;
            lcPumpCombo.Margin = new Padding(0, 3, 0, 3);
            lcFanCombo.Dock = DockStyle.Fill;
            lcFanCombo.Margin = new Padding(0, 3, 0, 3);
            lcLayout.Controls.Add(MakeRowName("泵速"), 0, 0);
            lcLayout.Controls.Add(lcPumpCombo, 1, 0);
            lcLayout.SetColumnSpan(lcPumpCombo, 2);
            lcLayout.Controls.Add(MakeRowName("风扇"), 0, 1);
            lcLayout.Controls.Add(lcFanCombo, 1, 1);
            lcLayout.SetColumnSpan(lcFanCombo, 2);
            // 灯光行（真机验收：原实现按钮吃满整行、状态多行换行像一块公告板）：
            // 固定窄列小按钮 + 单行省略号状态；完整文本仍在 tooltip、点击重试逻辑保留。
            lcLightBtn.Dock = DockStyle.Fill;
            lcLightBtn.Margin = new Padding(0, 4, 0, 4);
            lcLayout.Controls.Add(MakeRowName("灯光"), 0, 2);
            lcLayout.Controls.Add(lcLightBtn, 1, 2);
            lcStatus.Font = UiStyleCaptionFont();
            lcStatus.AutoEllipsis = true;
            lcStatus.Dock = DockStyle.Fill;
            lcStatus.Location = Point.Empty;
            lcStatus.Margin = new Padding(0, 4, 0, 4);
            lcLayout.Controls.Add(lcStatus, 2, 2);
            lcPanel.Controls.Add(lcLayout);
            // 液冷状态回显（3s）：未连接时只禁用硬件档位；灯光按钮保持可读并负责提示连接。
            _liquidCoolingStatusTimer.Tick += async (_, _) =>
            {
                if (_lcStatusTicking) return;
                _lcStatusTicking = true;
                try
                {
                    await RequestGcuLiquidCoolingStatusAsync();
                    await ProbeSystemBluetoothConnectionAsync();
                    _syncingLc = true;
                    try
                    {
                        LiquidCoolingControlRoute route = GetLiquidCoolingRoute();
                        bool directBleConnected = route == LiquidCoolingControlRoute.DirectBle &&
                            Program.ble is { IsConnected: true };
                        bool gcuConnected = route == LiquidCoolingControlRoute.Gcu && Program.hw is not null;
                        bool connectedForUi = directBleConnected || gcuConnected;
                        lcPumpCombo.Enabled = lcFanCombo.Enabled = connectedForUi;
                        if (directBleConnected && !_lcConnecting)
                        {
                            int pumpIndex = FindProfileIndex(lcPumpCombo, Program.ble!.SavedPumpProfile);
                            int fanIndex = FindProfileIndex(lcFanCombo, Program.ble.SavedFanProfile);
                            if (lcPumpCombo.SelectedIndex != pumpIndex) lcPumpCombo.SelectedIndex = pumpIndex;
                            if (lcFanCombo.SelectedIndex != fanIndex) lcFanCombo.SelectedIndex = fanIndex;
                            acceptedPumpSelection = pumpIndex;
                            acceptedFanSelection = fanIndex;
                        }
                        else if (gcuConnected && !_lcConnecting)
                        {
                            // 档位回显必须带上用户意图：厂商回读里没有「自动」（泵回读的是客户端
                            // 挑出的具体档位，风扇回读的 4 在 BLE 通道里是 100%），只按回读同步
                            // 会把「自动」挤成手动档，表现为选完自动几秒后又跳回去。
                            int pumpIndex = FindProfileIndex(lcPumpCombo, LiquidCoolingDisplayPolicy.PumpProfile(
                                AppConfig.Get("lc_pump_profile", WaterCoolerBle.ProfileUnset), Program.hw!.LcPumpControl));
                            int fanIndex = FindProfileIndex(lcFanCombo, LiquidCoolingDisplayPolicy.FanProfile(
                                AppConfig.Get("lc_fan_profile", WaterCoolerBle.ProfileUnset), Program.hw.LcFanControl));
                            if (lcPumpCombo.SelectedIndex != pumpIndex) lcPumpCombo.SelectedIndex = pumpIndex;
                            if (lcFanCombo.SelectedIndex != fanIndex) lcFanCombo.SelectedIndex = fanIndex;
                            acceptedPumpSelection = pumpIndex;
                            acceptedFanSelection = fanIndex;
                        }

                        if (directBleConnected)
                        {
                            bool meterPending = !Program.ble!.IsFlowMeterReady;
                            bool meterError = Program.ble.IsFlowFaultConfirmed;
                            lcStatus.Text = meterError
                                ? "流量异常"
                                : meterPending ? "正在确认水流"
                                : "已直连";
                            toolTip.SetToolTip(lcStatus, (meterError
                                ? "流量异常"
                                : meterPending ? "正在确认水流"
                                : "已直连") + " · " + Program.ble.ConnectedName);
                            lcStatus.ForeColor = meterError
                                ? UiVisualStyle.Danger
                                : meterPending ? UiVisualStyle.Warn
                                : UiVisualStyle.Ok;
                        }
                        else if (gcuConnected)
                        {
                            string device = string.IsNullOrWhiteSpace(Program.hw!.LcCurrentMac)
                                ? Program.hw.LcFwVersion
                                : Program.hw.LcCurrentMac;
                            bool meterError = Program.hw.LcMeterFaultConfirmed;
                            lcStatus.Text = meterError ? "GCU 流量异常" : "GCU 已连接水冷";
                            toolTip.SetToolTip(lcStatus, (meterError ? "GCU 报告流量异常" : "GCU 已连接水冷")
                                + (string.IsNullOrWhiteSpace(device) ? "" : " · " + device));
                            lcStatus.ForeColor = meterError
                                ? UiVisualStyle.Danger
                                : UiVisualStyle.Ok;
                        }
                        else if (route == LiquidCoolingControlRoute.BluetoothObserved)
                        {
                            string device = _lcSystemBluetoothObservation.Name;
                            lcStatus.Text = "蓝牙已连接";
                            toolTip.SetToolTip(lcStatus, "蓝牙已连接，等待 GCU 接管" +
                                (string.IsNullOrWhiteSpace(device) ? "" : " · " + device));
                            lcStatus.ForeColor = UiVisualStyle.Warn;
                        }
                        else if (!string.IsNullOrWhiteSpace(DescribeGcuLiquidCoolingState()))
                        {
                            lcStatus.Text = DescribeGcuLiquidCoolingState();
                            toolTip.SetToolTip(lcStatus, DescribeGcuLiquidCoolingDetail());
                            lcStatus.ForeColor = Program.hw?.LcReportedConnected == true
                                ? UiVisualStyle.Warn
                                : UiVisualStyle.Muted;
                        }
                        else
                        {
                            lcStatus.Text = "未连接（点击连接）";
                            lcStatus.ForeColor = UiVisualStyle.Muted;
                        }
                    }
                    finally { _syncingLc = false; }

                    if (GetLiquidCoolingRoute() == LiquidCoolingControlRoute.Gcu)
                        await RestoreSavedGcuLightingAsync();

                    LiquidCoolingControlRoute routeAfterProbe = GetLiquidCoolingRoute();
                    if (routeAfterProbe == LiquidCoolingControlRoute.Gcu)
                    {
                        _lcGcuConnectAttempts = 0;
                    }
                    else if (!_lcConnecting && LiquidCoolingConnectionPolicy.ShouldRetryGcuConnection(
                        Program.hw is { IsConnected: true },
                        Program.service is not null,
                        routeAfterProbe,
                        Program.hw?.LcActionSupportReported == true,
                        Program.hw?.LcActionSupported == true,
                        _lcGcuConnectAttempts,
                        (long)(DateTime.UtcNow - _lcLastGcuConnectAttempt).TotalMilliseconds))
                    {
                        _lcGcuConnectAttempts++;
                        _lcLastGcuConnectAttempt = DateTime.UtcNow;
                        await AutoConnectLc(true);
                        routeAfterProbe = GetLiquidCoolingRoute();
                    }

                    bool bleOn = Program.ble is { IsConnected: true };
                    if (bleOn)
                    {
                        _lcWasConnected = true;
                        _lcReconnectAttempts = 0;
                        await Program.ble!.UpdateAutomaticCoolingAsync(CurrentCoolingTemperature());
                    }
                    else if (routeAfterProbe == LiquidCoolingControlRoute.Gcu)
                    {
                        await UpdateGcuAutomaticCoolingAsync();
                    }
                    else if (routeAfterProbe == LiquidCoolingControlRoute.None &&
                        !_lcManualDisconnect && !_lcConnecting && Program.ble is { HasSavedDevice: true } &&
                        _lcReconnectAttempts < 3 &&
                        (DateTime.UtcNow - _lcLastReconnectAttempt).TotalSeconds >= 8)
                    {
                        if (_lcWasConnected) _lcReconnectAttempts = 0;
                        _lcWasConnected = false;
                        _lcReconnectAttempts++;
                        _lcLastReconnectAttempt = DateTime.UtcNow;
                        await AutoConnectLc(true);
                    }
                }
                catch (Exception ex) { Logger.WriteLine("Liquid cooling status update failed: " + ex.Message); }
                finally { _lcStatusTicking = false; }
            };
            if (!Program.UiAuditMode) _liquidCoolingStatusTimer.Start();
            // 启动时静默自动连接一次（失败保持未连接，用户可手动点击重试；Shown 在隐藏/重开时重复触发，仅首次生效）
            Shown += async (_, _) =>
            {
                if (Program.UiAuditMode) return;
                if (_lcAutoTried) return;
                _lcAutoTried = true;
                await Task.Delay(2000);
                await AutoConnectLc(true);
            };
            foreach (Control dynamicPanel in new Control[] { panel, brightPanel, lcPanel })
                ResponsiveLayout.ScaleFrom96(dynamicPanel, this);
            Controls.Add(brightPanel);
            Controls.Add(lcPanel);
            Controls.Add(panel);
            // 面板顺序（Dock=Top 自下而上显示）：性能 → 液冷 → 显卡 → 快捷 → 电池 → 刷新率 → 亮度
            Controls.SetChildIndex(panel, 5);
            Controls.SetChildIndex(brightPanel, 2);

            // 亮度状态回显
            _displayStatusTimer.Tick += (_, _) =>
            {
                _syncingDisplay = true;   // 抑制标志必须覆盖整个回显块（brightSlider 赋值会触发其命令发送）
                try
                {
                    if (Program.hw is { ScreenBrightness: >= 0 } &&
                        !_brightnessCommitQueue.HasPendingOrRunning &&
                        (DateTime.UtcNow - _lastBrightnessUi).TotalSeconds >= 3)
                    {
                        int v = Program.hw.ScreenBrightness;
                        if (v >= 10 && v <= 100)
                        {
                            if (brightSlider.Value != v) brightSlider.Value = v;
                            brightPercent.Text = v + "%";
                        }
                        SyncHzButtons();
                    }
                    if (_autoHzChk is not null && Program.hw is { DcHzSeen: true } &&
                        _autoHzChk.Checked != Program.hw.DcHz)
                    {
                        _autoHzChk.Checked = Program.hw.DcHz;
                    }
                    // 校色下拉回显：注册表/同帧上报的当前档 → 下拉索引（tick 已在 _syncingDisplay 内，不触发下发）。
                    if (_calibCombo is not null && (DateTime.UtcNow - _lastCalibUi).TotalSeconds >= 3)
                    {
                        int idx = CalibComboIndexFor(ResolveColorCalibrationMode());
                        if (_calibCombo.SelectedIndex != idx) _calibCombo.SelectedIndex = idx;
                    }
                    if (Program.hw is { LcdOverdriveSeen: true })
                    {
                        if (!overdriveChk.Visible) overdriveChk.Visible = true;
                        if (overdriveChk.Checked != Program.hw.LcdOverdrive) overdriveChk.Checked = Program.hw.LcdOverdrive;
                    }
                }
                finally { _syncingDisplay = false; }
            };
            if (!Program.UiAuditMode) _displayStatusTimer.Start();
        }

        void UpdateQuickSwitches()
        {
            // 任务栏/透明/深色这三项来自 Windows 而不是 GCU，所以 hw 为空时也要继续刷——
            // 只有硬件开关部分需要 hw。
            if (_quickSwitches.Length == 0) return;
            if ((DateTime.Now - _lastQuickSwitchUi).TotalSeconds < 12) return;   // 用户操作后 12s 抑制回显（命令生效/回读有延迟，防弹回）
            _syncingSwitches = true;   // 回显同步期间不触发命令/弹窗（深度睡眠 pending 状态翻转会误触发）
            try
            {
                foreach (var cb in _quickSwitches)
                {
                    var key = (string)cb.Tag;
                    bool? windowsState = ReadWindowsPersonalizationSwitch(key);
                    if (windowsState.HasValue)
                    {
                        cb.Checked = windowsState.Value;
                        continue;
                    }
                    if (key == "startup")
                    {
                        // 自启动状态来自计划任务，读一次比注册表重；组收起时开关不可见，跳过刷新。
                        if (_quickGroup is null || _quickGroup.Expanded)
                        {
                            bool? scheduled = Startup.ReadScheduledState();
                            if (scheduled.HasValue) cb.Checked = scheduled.Value;
                        }
                        continue;
                    }
                    if (Program.hw is not null && Program.hw.QuickSwitches.TryGetValue(key, out bool state))
                    {
                        if (key == "deepsleep" && _deepSleepPendingValue >= 0)
                        {
                            // 深度睡眠的改动要重启后才在 Setting/Status 里刷新：硬件报出同值
                            // = 已生效，清除 pending；否则保持用户最后一次选择，别把开关弹回旧值。
                            if (state == (_deepSleepPendingValue == 1)) SetDeepSleepPending(-1);
                            else cb.Checked = _deepSleepPendingValue == 1;
                            continue;
                        }
                        cb.Checked = state;
                    }
                }
                if (_usbChargerBox is not null && Program.hw is not null)
                    _usbChargerBox.Checked = Program.hw.UsbCharger;
                if (_fanBoostBox is not null && Program.hw is not null)
                    _fanBoostBox.Checked = Program.hw.FanBoost;
            }
            finally { _syncingSwitches = false; }
            RefreshQuickGroupSummary();
        }

        /// <summary>更多开关摘要（预览「6/25 开启」）：开启数/总数。</summary>
        void RefreshQuickGroupSummary()
        {
            if (_quickGroup is null || _quickSwitches.Length == 0) return;
            int on = _quickSwitches.Count(cb => cb.Checked);
            _quickGroup.Summary = $"{on}/{_quickSwitches.Length} 开启";
        }

        /// <summary>
        /// 记录/清除「深度睡眠待重启生效」的值（-1=无，0/1=待生效的值）。
        /// 持久化走 AppConfig：测试环境由 LMECHREVO_CONFIG_FILE 指向临时文件，不会碰真实用户配置。
        /// </summary>
        void SetDeepSleepPending(int value)
        {
            _deepSleepPendingValue = value;
            if (value < 0) AppConfig.Remove("deepsleep_pending");
            else AppConfig.Set("deepsleep_pending", value);
        }

        /// <summary>
        /// 某个快捷开关此刻的真实状态，null = 读不到。
        ///
        /// 这里的分支必须与 <see cref="UpdateQuickSwitches"/> 的回显来源一致，
        /// 否则命令失败后的回滚值会和下一轮回显打架：Windows 三项来自系统，
        /// USB 充电与风扇增强各有独立属性（不进 QuickSwitches 字典），其余查字典。
        /// </summary>
        internal static bool? ReadQuickSwitchState(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            bool? windowsState = ReadWindowsPersonalizationSwitch(key);
            if (windowsState.HasValue) return windowsState;
            if (key == "startup") return Startup.ReadScheduledState();
            if (Program.hw is not { } hw) return null;
            return key switch
            {
                "usb" => hw.UsbChargerSeen ? hw.UsbCharger : null,
                "fanboost" => hw.SupportsFanBoost ? hw.FanBoost : null,
                _ => hw.QuickSwitches.TryGetValue(key, out bool state) ? state : null,
            };
        }


        private readonly System.Timers.Timer _sensorTimer;
        private readonly System.Windows.Forms.Timer batteryTimer = new() { Interval = 200 };

        public Updates? updatesForm;

        static long lastRefresh;
        static long lastBatteryRefresh;
        static long lastLostFocus;

        bool isGpuSection = true;
        bool isMuxGpu = true;

        bool batteryMouseOver = false;
        bool batteryFullMouseOver = false;

        bool activateCheck = false;

        public SettingsForm()
        {
            BackColor = UiVisualStyle.Window;

            InitializeComponent();
            labelVersion.Text = Program.ReleaseLabel;
            InitTheme(true);
            // 自绘滚动的滚轮转发（AutoScroll 关闭后系统不再路由滚轮）。
            if (!Program.UiAuditMode) Application.AddMessageFilter(new DashboardWheelFilter(this));

            gpuControl = new GPUModeControl(this);
            updateControl = new AutoUpdateControl(this);

            buttonSilent.Text = Properties.Strings.Silent;
            buttonBalanced.Text = Properties.Strings.Balanced;
            buttonTurbo.Text = Properties.Strings.Turbo;

            buttonEco.Text = Properties.Strings.EcoMode;
            buttonUltimate.Text = Properties.Strings.UltimateMode;
            buttonStandard.Text = Properties.Strings.StandardMode;
            buttonOptimized.Text = Properties.Strings.Optimized;
            buttonStopGPU.Text = Properties.Strings.StopGPUApps;

            buttonScreenAuto.Text = Properties.Strings.AutoMode;
            buttonMiniled.Text = Properties.Strings.Multizone;

            buttonKeyboardColor.Text = Properties.Strings.Color;
            buttonKeyboard.Text = Properties.Strings.Extra;

            labelPerf.Text = Properties.Strings.PerformanceMode;
            labelGPU.Text = Properties.Strings.GPUMode;
            labelSreen.Text = Properties.Strings.LaptopScreen;
            UpdateKeyboardLabel();
            labelBatteryTitle.Text = Properties.Strings.BatteryChargeLimit;

            checkStartup.Text = Properties.Strings.RunOnStartup;

            buttonQuit.Text = Properties.Strings.Quit;
            buttonUpdates.Text = Properties.Strings.Updates;
            buttonDonate.Text = Properties.Strings.Donate;

            // Accessible Labels

            sliderBattery.AccessibleName = Properties.Strings.BatteryChargeLimit;
            buttonQuit.AccessibleName = Properties.Strings.Quit;
            buttonUpdates.AccessibleName = "检查更新";   // 按钮已改为检查更新（不再是 BIOS/驱动更新入口）
            panelPerformance.AccessibleName = Properties.Strings.PerformanceMode;
            buttonSilent.AccessibleName = Properties.Strings.Silent;
            buttonBalanced.AccessibleName = Properties.Strings.Balanced;
            buttonTurbo.AccessibleName = Properties.Strings.Turbo;
            panelGPU.AccessibleName = Properties.Strings.GPUMode;
            buttonEco.AccessibleName = Properties.Strings.EcoMode;
            buttonStandard.AccessibleName = Properties.Strings.StandardMode;
            buttonOptimized.AccessibleName = Properties.Strings.Optimized;
            buttonUltimate.AccessibleName = Properties.Strings.UltimateMode;
            panelScreen.AccessibleName = Properties.Strings.LaptopScreen;

            buttonScreenAuto.AccessibleName = Properties.Strings.AutoMode;
            //button60Hz.AccessibleName = "60Hz Refresh Rate";
            //button120Hz.AccessibleName = "Maximum Refresh Rate";

            panelKeyboard.AccessibleName = Properties.Strings.LaptopKeyboard;
            buttonKeyboard.AccessibleName = Properties.Strings.ExtraSettings;
            buttonKeyboardColor.AccessibleName = Properties.Strings.LaptopKeyboard + " " + Properties.Strings.Color;
            comboKeyboard.AccessibleName = Properties.Strings.LaptopBacklight;

            FormClosing += SettingsForm_FormClosing;
            Deactivate += SettingsForm_LostFocus;
            Activated += SettingsForm_Focused;
            contextMenuStrip.Opening += (_, _) => SetContextMenu();   // 每次右键打开菜单重建——勾选永远与当前状态同步

            buttonSilent.BorderColor = colorEco;
            buttonBalanced.BorderColor = colorStandard;
            buttonTurbo.BorderColor = colorTurbo;

            buttonEco.BorderColor = colorEco;
            buttonStandard.BorderColor = colorStandard;
            buttonUltimate.BorderColor = colorTurbo;
            buttonOptimized.BorderColor = colorEco;

            button60Hz.BorderColor = colorGray;
            button120Hz.BorderColor = colorGray;
            buttonScreenAuto.BorderColor = colorGray;
            buttonMiniled.BorderColor = colorTurbo;

            buttonEnergySaver.BackColor = colorEco;
            buttonEnergySaver.ForeColor = SystemColors.ControlLightLight;
            buttonEnergySaver.Click += ButtonEnergySaver_Click;

            buttonAmdOled.BackColor = colorTurbo;
            buttonAmdOled.ForeColor = SystemColors.ControlLightLight;
            buttonAmdOled.Click += ButtonAmdOled_Click;

            buttonSilent.Click += ButtonSilent_Click;
            buttonBalanced.Click += ButtonBalanced_Click;
            buttonTurbo.Click += ButtonTurbo_Click;

            // Mechrevo：删除 G-Helper 图标，纯文字居中统一
            buttonSilent.Image = null;
            buttonSilent.TextAlign = ContentAlignment.MiddleCenter;
            buttonBalanced.Image = null;
            buttonBalanced.TextAlign = ContentAlignment.MiddleCenter;
            buttonTurbo.Image = null;
            buttonTurbo.TextAlign = ContentAlignment.MiddleCenter;

            // Mechrevo：性能面板 5 列 = 静音(0) 平衡(1) 静音狂暴(2) 狂暴(3) 风扇增强(4)
            buttonTurbo.Text = "狂暴";
            // 可访问名与可见文字保持一致：默认的 Strings.Turbo 是「增强模式」，与「狂暴」不一致。
            buttonTurbo.AccessibleName = "狂暴";
            tablePerf.Controls.Remove(buttonTurbo);
            tablePerf.Controls.Add(buttonTurbo, 3, 0);   // 狂暴移到第 4 列

            buttonSilentTurbo = new RButton
            {
                Text = "静音狂暴",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                ForeColor = UiVisualStyle.Text,
                BackColor = UiVisualStyle.SurfaceRaised,
            };
            buttonSilentTurbo.FlatAppearance.BorderColor = UiVisualStyle.Border;
            buttonSilentTurbo.BorderColor = colorTurbo;
            buttonSilentTurbo.Click += async (_, _) =>
            {
                await SwitchPerformanceModeAsync(MechrevoService.ModeTurbo, silentTurbo: true);
            };
            tablePerf.Controls.Add(buttonSilentTurbo, 2, 0);

            // Mechrevo：第 5 列「自定义」——打开自定义性能模式（4 档 + 功耗墙），激活时高亮
            // （原「风扇增强」按钮移入快捷开关面板）
            buttonCustomMode = new RButton
            {
                Text = "自定义",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                ForeColor = UiVisualStyle.Text,
                BackColor = UiVisualStyle.SurfaceRaised,
            };
            buttonCustomMode.FlatAppearance.BorderColor = UiVisualStyle.Border;
            buttonCustomMode.BorderColor = colorCustom;
            buttonCustomMode.Click += async (_, _) =>
            {
                if (customModeForm is null || customModeForm.IsDisposed) { customModeForm = new CustomModeForm(); AddOwnedForm(customModeForm); }
                if (customModeForm.Visible) { customModeForm.Hide(); return; }

                // Finish the first native paint and final placement while effectively
                // transparent. Otherwise WinForms can expose its default light surface
                // for one DWM frame before the dark child controls have painted.
                customModeForm.Opacity = 0.01;
                ResponsiveLayout.PlaceAdjacent(customModeForm, this);
                try
                {
                    customModeForm.Show();
                    customModeForm.Update();
                }
                finally
                {
                    if (!customModeForm.IsDisposed) customModeForm.Opacity = 1;
                }
                if (!Program.UiAuditMode && !customModeForm.IsDisposed)
                {
                    if (await customModeForm.ActivateProfileAsync(0))
                    {
                        ShowMode(MechrevoService.ToVisualMode(MechrevoService.ModeCustom));
                        SetContextMenu();
                    }
                }
            };
            tablePerf.Controls.Add(buttonCustomMode, 4, 0);

            // 深潜座舱：初始布局（未走 Reflow 前）也按分段控件成型；状态仍由 Activated 驱动。
            ApplySegmentRow(new List<Control> { buttonSilent, buttonBalanced, buttonSilentTurbo, buttonTurbo, buttonCustomMode });
            ApplySegmentRow(new List<Control> { buttonEco, buttonStandard, buttonUltimate });

            buttonEco.Click += ButtonEco_Click;
            buttonStandard.Click += ButtonStandard_Click;
            buttonUltimate.Click += ButtonUltimate_Click;
            // buttonOptimized.Click += ButtonOptimized_Click; // 已移除自动模式
            buttonStopGPU.Click += ButtonStopGPU_Click;
            pictureGPU.Click += PictureGPU_Click;

            VisibleChanged += SettingsForm_VisibleChanged;

            button60Hz.Click += Button60Hz_Click;
            button120Hz.Click += Button120Hz_Click;
            buttonScreenAuto.Click += ButtonScreenAuto_Click;
            buttonMiniled.Click += ButtonMiniled_Click;
            buttonFHD.Click += ButtonFHD_Click;
            buttonHDRControl.Click += ButtonHDRControl_Click;

            buttonQuit.Click += ButtonQuit_Click;
            buttonUpdates.Text = "检查更新";
            buttonUpdates.Click += (_, _) => ShowUpdateDialog();   // 更新按钮 → 检查更新窗口

            buttonKeyboardColor.Click += ButtonKeyboardColor_Click;
            buttonKeyboardColor.Swatch2Click += ButtonKeyboardColor2_Click;


            labelCPUFan.Click += LabelCPUFan_Click;
            labelGPUFan.Click += LabelCPUFan_Click;

            checkStartup.Checked = false;
            checkStartup.Enabled = Program.UiAuditMode;
            // 破坏性副作用守卫链（与息屏同源）：只挂 Click，程序化回显（RefreshStartupStatusAsync
            // 写 Checked）不触发计划任务写入；并要求刚刚有真实输入。
            checkStartup.Click += CheckStartup_Click;
            Shown += (_, _) =>
            {
                if (!Program.UiAuditMode) _ = RefreshStartupStatusAsync();
            };

            labelVersion.Click += LabelVersion_Click;
            labelVersion.ForeColor = UiVisualStyle.Muted;

            // buttonOptimized.MouseMove += ButtonOptimized_MouseHover; // 已移除自动模式
            // buttonOptimized.MouseLeave += ButtonGPU_MouseLeave;

            buttonEco.MouseMove += ButtonEco_MouseHover;
            buttonEco.MouseLeave += ButtonGPU_MouseLeave;

            buttonStandard.MouseMove += ButtonStandard_MouseHover;
            buttonStandard.MouseLeave += ButtonGPU_MouseLeave;

            buttonUltimate.MouseMove += ButtonUltimate_MouseHover;
            buttonUltimate.MouseLeave += ButtonGPU_MouseLeave;

            tableGPU.MouseLeave += ButtonGPU_MouseLeave;

            buttonScreenAuto.MouseMove += ButtonScreenAuto_MouseHover;
            buttonScreenAuto.MouseLeave += ButtonScreen_MouseLeave;

            button60Hz.MouseMove += Button60Hz_MouseHover;
            button60Hz.MouseLeave += ButtonScreen_MouseLeave;

            button120Hz.MouseMove += Button120Hz_MouseHover;
            button120Hz.MouseLeave += ButtonScreen_MouseLeave;

            buttonFHD.MouseMove += ButtonFHD_MouseHover;
            buttonFHD.MouseLeave += ButtonScreen_MouseLeave;

            // Mechrevo：电池保护改三档按钮（性能/平衡/健康），彻底移除 ASUS 滑条（含其值标签）
            panelBatteryTitle.Controls.Clear();
            labelBattery.ForeColor = UiVisualStyle.Muted;
            labelBattery.Font = UiStyleCaptionFont();
            panelBatteryTitle.Controls.Add(BuildHeadRow(pictureBattery, labelBatteryTitle, labelBattery));

                        // 充电上限是连续滑条：真机实测 EC 的阈值寄存器接受任意百分比（0x7B9/0x7D0，
            // 写 0x64/0x50/0x3C/0x28/0x14/0x5F 全部原值读回、不钳位），官方三档只写模式位、
            // 不设上限且已整体摘除。v2：滑条换成 RSlider（与亮度同款），键盘/滚轮步长 1%。
            // 滑条 + 右侧百分比读数，形态对齐预览 .sliderline（Consolas muted 小字右对齐）。
            var batterySliderRow = new TableLayoutPanel
            {
                Name = "tableBatterySlider",
                ColumnCount = 3,
                RowCount = 1,
                Location = new Point(
                    panelBattery.Padding.Left,
                    panelBatteryTitle.Bottom + ResponsiveLayout.LogicalToDevice(this, 2)),
                Size = new Size(
                    Math.Max(1, panelBattery.ClientSize.Width - panelBattery.Padding.Horizontal),
                    ResponsiveLayout.LogicalToDevice(this, 28)),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                BackColor = panelBattery.BackColor,
            };
            // 列结构对齐预览：左「限充」标签 + 滑条 + 右侧读数。全部用**百分比**列——
            // 绝对列在审计的多次缩放里会被算小（实测 125% 视口下只剩 21px，而 "100%" 要 41px；
            // 32px 的「限充」列在 100pct 视口下短到换行），百分比列天然免疫缩放叠加。
            batterySliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10F));
            batterySliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76F));
            batterySliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
            batterySliderRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var batteryLimitLabel = new Label
            {
                Name = "labelBatteryLimit",
                Text = "限充",
                ForeColor = UiVisualStyle.Text,
                BackColor = panelBattery.BackColor,
                // AutoSize 标签是审计裁剪检查的白名单（同行头状态标签）——「限充」两字在 10% 列里
                // 实际放得下，但审计对固定尺寸标签会再扣 8px 保留宽，100pct 视口下误报换行。
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                Font = UiStyleCaptionFont(),
            };
            sliderBattery.Dock = DockStyle.Fill;
            sliderBattery.Margin = Padding.Empty;
            _batteryLimitValue = new Label
            {
                Name = "labelBatteryLimitValue",
                Text = "100%",
                ForeColor = UiVisualStyle.Muted,
                BackColor = panelBattery.BackColor,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = Padding.Empty,
                Font = new Font("Consolas", UiStyleCaption(), FontStyle.Regular, GraphicsUnit.Point),
            };
            batterySliderRow.Controls.Add(batteryLimitLabel, 0, 0);
            batterySliderRow.Controls.Add(sliderBattery, 1, 0);
            batterySliderRow.Controls.Add(_batteryLimitValue, 2, 0);
            panelBattery.Controls.Add(batterySliderRow);
            panelBattery.MinimumSize = new Size(0, batterySliderRow.Bottom + panelBattery.Padding.Bottom);

            sliderBattery.MouseUp += SliderBattery_MouseUp;
            sliderBattery.KeyUp += SliderBattery_KeyUp;
            sliderBattery.ValueChanged += SliderBattery_ValueChanged;
            batteryTimer.Tick += (_, _) => { batteryTimer.Stop(); BatteryControl.ApplyChargeLimitFromUserGesture(sliderBattery.Value); };
            // 拖动是连续的 40..100：EC 的 CGLM 接受任意百分比（真机实测 73%、54% 都原值生效）。

            _sensorTimer = new System.Timers.Timer(AppConfig.Get("sensor_timer", 1000));
            _sensorTimer.Elapsed += OnTimedEvent;
            InitQuickSwitchRefresh();

            labelCharge.MouseEnter += PanelBattery_MouseEnter;
            labelCharge.MouseLeave += PanelBattery_MouseLeave;
            labelBattery.Click += LabelBattery_Click;

            buttonBatteryFull.MouseEnter += ButtonBatteryFull_MouseEnter;
            buttonBatteryFull.MouseLeave += ButtonBatteryFull_MouseLeave;
            buttonBatteryFull.Click += ButtonBatteryFull_Click;

            // buttonBacklight / buttonFPS / buttonAutoTDP 的点击绑定已删除：
            // 它们是 ROG Ally 掌机的三个按钮，宿主面板 panelAlly 恒不可见，
            // 处理器背后的 AllyControl 也是空实现。
            buttonOverlay.Click += ButtonOverlay_Click;
            buttonOverlay.BorderColor = colorStandard;

            string modelLabel = string.IsNullOrWhiteSpace(_deviceCapabilities.Model)
                ? _deviceCapabilities.SystemFamily
                : _deviceCapabilities.Model;
            Text = "L-Mechrevo " + (ProcessHelper.IsUserAdministrator() ? "—" : "-") + " " + modelLabel;

            buttonFnLock.Visible = false;   // FnLock 为华硕残留空实现（InputDispatcher 存根），入口隐藏

            labelCharge.Click += LabelCharge_Click;

            donateControl = new DonateControl(this, buttonDonate);
            donateControl.Init();

            labelBacklight.ForeColor = colorStandard;
            labelBacklight.Click += LabelBacklight_Click;

            BuildOfficialConsolePanel();
            BuildDashboardLayout();
            ConfigureResponsiveWindow();
            RefreshDeviceCapabilities();
        }

        private void BuildOfficialConsolePanel()
        {
            int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
            _officialConsolePanel = new BufferedPanel
            {
                Name = "panelOfficialConsole",
                CardStyle = true,   // 主题重刷在面板创建之前跑过，卡片外观必须自带（见 ApplyTheme 的说明）
                Height = D(CompactOfficialConsoleLogicalHeight),
                Padding = new Padding(D(12), D(8), D(12), D(8)),
                BackColor = UiVisualStyle.Surface,
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = _officialConsolePanel.BackColor,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(126)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(150)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new Label
            {
                Name = "labelOfficialConsoleTitle",
                Text = "官方控制台",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
            };
            UiVisualStyle.ApplyGlyph(title, UiGlyph.Kind.Console);   // 构建期挂：AutoSize 会把图标算进首选宽度
            _officialConsoleStatus = new Label
            {
                Text = "正在检测...",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                Margin = Padding.Empty,
            };
            _officialConsoleButton = new RButton
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(D(8), 0, 0, 0),
                BackColor = UiVisualStyle.SurfaceRaised,
                ForeColor = UiVisualStyle.Text,
                Cursor = Cursors.Hand,
            };
            _officialConsoleButton.Click += OfficialConsoleButton_Click;
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(_officialConsoleStatus, 1, 0);
            layout.Controls.Add(_officialConsoleButton, 2, 0);
            _officialConsolePanel.Controls.Add(layout);
            UiVisualStyle.ApplyTitle(title);
            UiVisualStyle.ApplyMuted(_officialConsoleStatus);
            if (!Program.UiAuditMode) RefreshOfficialConsolePanel();
            _officialConsoleStatusTimer.Tick += (_, _) => RefreshOfficialConsolePanel();
            Shown += (_, _) =>
            {
                RefreshOfficialConsolePanel();
                if (!Program.UiAuditMode) _officialConsoleStatusTimer.Start();
            };
        }

        private async void OfficialConsoleButton_Click(object? sender, EventArgs e)
        {
            if (_officialConsoleButton is null || Program.UiAuditMode) return;
            _officialConsoleButton.Enabled = false;
            OfficialConsoleIsolation.IsolationStatus status;
            try
            {
                bool gcuConnected = Program.hw is { IsConnected: true };
                status = await Task.Run(() => OfficialConsoleIsolation.GetStatus(gcuConnected));
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Official console status check failed: " + ex.Message);
                _officialConsoleButton.Enabled = true;
                return;
            }
            bool isolate = !status.Isolated;
            string prompt = isolate
                ? "隔离官方界面和托盘？\n\n无需卸载官方控制台。GCUBridge、GCUService、UWACPI 驱动及机型配置都会保留。"
                : "恢复官方控制台的原始启动状态？";
            if (MessageBox.Show(prompt, "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                ApplyOfficialConsoleStatus(status);
                return;
            }

            _officialConsoleStatus!.Text = isolate ? "正在隔离..." : "正在恢复...";
            if (!isolate) OfficialConsoleIsolation.StopGuard();
            OfficialConsoleIsolation.OperationResult result = await OfficialConsoleIsolation.SetIsolationAsync(isolate);
            if (result.Success)
            {
                if (isolate)
                    OfficialConsoleIsolation.StartGuardIfNeeded();
                else
                    OfficialConsoleIsolation.LaunchOfficialUi();
            }
            else if (!isolate)
            {
                OfficialConsoleIsolation.StartGuardIfNeeded();
            }
            RefreshOfficialConsolePanel();
            _officialConsoleButton.Enabled = true;
            MessageBox.Show(result.Message, "L-Mechrevo", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void RefreshOfficialConsolePanel()
        {
            if (Program.UiAuditMode || _officialConsoleStatus is null || _officialConsoleButton is null) return;
            if (Interlocked.Exchange(ref _officialStatusRefreshRunning, 1) != 0) return;
            bool gcuConnected = Program.hw is { IsConnected: true };
            _ = RefreshOfficialConsoleStatusAsync(gcuConnected);
        }

        private async Task RefreshOfficialConsoleStatusAsync(bool gcuConnected)
        {
            try
            {
                OfficialConsoleIsolation.IsolationStatus status =
                    await Task.Run(() => OfficialConsoleIsolation.GetStatus(gcuConnected)).ConfigureAwait(false);
                Volatile.Write(ref _officialStatusRefreshRunning, 0);
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(() => ApplyOfficialConsoleStatus(status)); }
                catch (Exception ex) { Logger.WriteLine("Official console status UI update failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _officialStatusRefreshRunning, 0);
                Logger.WriteLine("Official console background status failed: " + ex.GetBaseException().Message);
            }
        }

        private void ApplyOfficialConsoleStatus(OfficialConsoleIsolation.IsolationStatus status)
        {
            if (_officialConsoleStatus is null || _officialConsoleButton is null || IsDisposed) return;
            _officialConsoleStatus.Text = status.Detail;
            _officialConsoleStatus.ForeColor = status.Isolated && !status.OfficialUiRunning && status.GcuRunning
                ? UiVisualStyle.Accent
                : status.OfficialUiRunning && status.Isolated ? UiVisualStyle.Danger : UiVisualStyle.Muted;
            _officialConsoleButton.Text = status.Isolated ? "恢复官方控制台" : "隔离官方界面与托盘";
            _officialConsoleButton.BorderColor = status.Isolated ? UiVisualStyle.Accent : UiVisualStyle.Border;
            _officialConsoleButton.Activated = status.Isolated;
            _officialConsoleButton.Enabled = status.OfficialUiInstalled || status.Isolated;
        }

        private void BuildDashboardLayout()
        {
            Control? lc = Controls.Find("panelLc", true).FirstOrDefault();
            Control? quick = Controls.Find("panelQuickSwitch", true).FirstOrDefault();
            Control? brightness = Controls.Find("panelBrightness", true).FirstOrDefault();
            if (lc is null || quick is null || brightness is null) return;

            int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
            BackColor = UiVisualStyle.Window;

            // The primary modes are a compact segmented control. The title already
            // establishes context, so repeated 48px illustrations only add height.
            tablePerf.AutoSize = false;
            tablePerf.ColumnCount = 5;
            tablePerf.RowCount = 1;
            tablePerf.ColumnStyles.Clear();
            for (int i = 0; i < 5; i++) tablePerf.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            tablePerf.RowStyles.Clear();
            tablePerf.RowStyles.Add(new RowStyle(SizeType.Absolute, D(34)));
            tablePerf.Height = D(34);
            foreach (Button button in tablePerf.Controls.OfType<Button>()) MakeCompactModeButton(button);
            // v2 行头：图标 + 名称 + 右侧状态（复用现有 Label，接线不动）
            picturePerf.Image = UiGlyph.Render(UiGlyph.Kind.Gauge, D(16), UiVisualStyle.Muted, 0);
            picturePerf.SizeMode = PictureBoxSizeMode.Zoom;
            picturePerf.Visible = true;
            var perfHead = BuildHeadRow(picturePerf, labelPerf, labelCPUFan);
            labelCPUFan.ForeColor = UiVisualStyle.Muted;
            labelCPUFan.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
            panelCPUTitle.AutoSize = false;
            panelCPUTitle.Height = D(26);
            panelCPUTitle.Controls.Clear();
            panelCPUTitle.Controls.Add(perfHead);
            panelPerformance.AutoSize = false;
            panelPerformance.Padding = new Padding(D(16), D(2), D(12), 0);
            panelPerformance.Height = D(66);

            tableGPU.AutoSize = false;
            tableGPU.RowStyles.Clear();
            tableGPU.RowCount = 1;
            tableGPU.RowStyles.Add(new RowStyle(SizeType.Absolute, D(34)));
            tableGPU.Height = D(34);
            foreach (Button button in tableGPU.Controls.OfType<Button>()) MakeCompactModeButton(button);
            buttonEco.Text = "集显";
            buttonStandard.Text = "标准";
            buttonOptimized.Text = "自动";
            buttonUltimate.Text = "直连";
            toolTip.SetToolTip(buttonEco, "集显模式");
            toolTip.SetToolTip(buttonStandard, "标准模式");
            toolTip.SetToolTip(buttonOptimized, "自动切换");
            toolTip.SetToolTip(buttonUltimate, "独显直连");
            panelGPUTitle.AutoSize = false;
            panelGPUTitle.Height = D(26);
            pictureGPU.Image = UiGlyph.Render(UiGlyph.Kind.VideoCard, D(16), UiVisualStyle.Muted, 0);
            pictureGPU.SizeMode = PictureBoxSizeMode.Zoom;
            pictureGPU.Visible = true;
            panelGPUTitle.Controls.Clear();
            labelGPUFan.ForeColor = UiVisualStyle.Muted;
            labelGPUFan.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
            panelGPUTitle.Controls.Add(BuildHeadRow(pictureGPU, labelGPU, labelGPUFan));
            labelTipGPU.Visible = false;
            panelGPU.AutoSize = false;
            panelGPU.Padding = new Padding(D(16), D(2), D(12), 0);
            panelGPU.Height = D(66);
            // 「自动模式离电切核显」「允许关闭独显程序」两个选项已按用户要求整体移除
            // （2026-09-11）：前者只在自动显卡模式下生效、本机也从未默认开启，后者会强杀
            // 正在用独显的程序（有丢数据风险）。移除后自动模式一律走服务端自己的自动切换。

            panelBatteryTitle.AutoSize = false;
            panelBatteryTitle.Height = D(26);
            pictureBattery.Image = UiGlyph.Render(UiGlyph.Kind.Battery, D(16), UiVisualStyle.Muted, 0);
            pictureBattery.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBattery.Visible = true;
            labelBatteryTitle.Text = "电池";
            panelBattery.Padding = new Padding(D(16), D(2), D(12), 0);
            panelBattery.MinimumSize = Size.Empty;
            panelBattery.Height = D(56);
            lc.Height = D(LiquidCoolingLogicalHeight);
            brightness.Height = D(94);
            panelVersion.Height = D(40);
            panelFooter.AutoSize = false;
            panelFooter.Padding = new Padding(D(6), D(3), D(6), D(3));
            panelFooter.Height = D(44);
            tableButtons.AutoSize = false;
            tableButtons.Dock = DockStyle.Fill;
            tableButtons.Margin = Padding.Empty;
            foreach (Button button in tableButtons.Controls.OfType<Button>())
            {
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(D(3));
                button.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
            }

            // These inherited G-Helper sections were never part of the shipped
            // Mechrevo surface. Their active replacements live above.
            // panelMatrix（Anime Matrix 点阵屏）与 panelAlly（ROG Ally 掌机）已从设计器里删除，
            // 不再需要在这里关掉。剩下这几个还留着，是因为它们的替代实现共用了里面的控件。
            foreach (Control legacy in new Control[]
            {
                panelScreen, panelKeyboard,
                panelPeripherals, panelStartup,
            }) legacy.Visible = false;

            if (_officialConsolePanel is null) return;
            _officialConsolePanel.Height = D(48);
            BuildThemeModePanel(D);
            BuildTelemetryRow();

            // ===== 单页纵排（预览 1:1）：性能 / 遥测 / 显卡 / 屏幕 / 电池 / 三组收放 =====
            lc.Dock = DockStyle.Top;
            _liquidGroup = new RCollapseGroup("液冷", UiGlyph.Kind.Droplet, "group_lc_open", defaultExpanded: false, showDivider: true);
            _liquidGroup.SetContent(lc);
            _liquidGroup.Toggled += (_, _) =>
            {
                UpdateDashboardWindowHeight();
                UpdateDashboardScroll();   // 内容高度变了：重算滚动量（窗口高度固定时不会自动重算）
            };
            quick.Dock = DockStyle.Top;
            _quickGroup = new RCollapseGroup("更多开关", UiGlyph.Kind.Chevrons, "group_quick_open", defaultExpanded: false);
            _quickGroup.SetContent(quick);
            _quickGroup.Toggled += (_, _) =>
            {
                UpdateDashboardWindowHeight();
                if (_quickGroup.Expanded) _fitQuickCards?.Invoke();   // I3：展开后确定性重排
                UpdateDashboardScroll();   // 展开的快捷开关越多，滚动量必须跟着长（否则滚不到底）
            };
            _lightGroup = new RCollapseGroup("灯光", UiGlyph.Kind.Gear, "group_light_open", defaultExpanded: true);
            _lightGroup.Toggled += (_, _) =>
            {
                UpdateDashboardWindowHeight();
                UpdateDashboardScroll();
            };
            BuildLightRowsPanel();

            BuildGcuStatusStrip(D);

            _dashboardSections = new Control[]
            {
                _panelGcuStatus!, panelPerformance, _telemetryPanel!, panelGPU, brightness, panelBattery,
                _liquidGroup, _lightGroup, _quickGroup,
            };
            _enabledDashboardSections.Clear();
            foreach (Control section in _dashboardSections) _enabledDashboardSections.Add(section);

            _dashboardScroll = new BufferedPanel
            {
                Name = "dashboardPageHost",
                Dock = DockStyle.Fill,
                // 自绘滚动条（预览 8px 深色细轨）：原生 AutoScroll 的滚动条是系统绘制的白条，
                // 在深色面板上割裂（真机验收）；这里关闭原生滚动，由 RScrollBar + 偏移驱动。
                AutoScroll = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = UiVisualStyle.Window,
            };
            _dashboardStack = new BufferedTableLayoutPanel
            {
                Name = "dashboardStack",
                // 不用 Dock=Top：自绘滚动要手动设 Top=-offset（Dock 会在布局时把它按回 0）。
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(D(16), D(10), D(16), D(12)),
                BackColor = UiVisualStyle.Window,
            };
            _dashboardStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _dashboardScroll.Controls.Add(_dashboardStack);
            _dashboardScrollBar = new RScrollBar
            {
                Name = "dashboardScrollBar",
                Dock = DockStyle.Right,
                Width = D(8),
                BackColor = UiVisualStyle.Window,
            };
            _dashboardScroll.Controls.Add(_dashboardScrollBar);
            _dashboardScrollBar.BringToFront();   // 栈（先加入=更高 z 序）会盖住右缘的滚动条
            _dashboardScrollBar.ValueChanged += (_, _) => UpdateDashboardScroll(rangeChanged: false);
            _dashboardStack.SizeChanged += (_, _) => UpdateDashboardScroll();
            _dashboardScroll.SizeChanged += (_, _) => UpdateDashboardScroll();

            RefreshThemeButtons();

            _dashboard = new BufferedTableLayoutPanel
            {
                Name = "dashboardLayout",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = UiVisualStyle.Window,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            _dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _dashboard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _dashboard.RowStyles.Add(new RowStyle(SizeType.Absolute, D(51)));
            AttachDashboardChrome();
            foreach (Control section in _dashboardSections)
            {
                section.Dock = DockStyle.Fill;
                // v2 行间距：行间垂直 Sm(8)，无卡片描边后的分隔感来自留白。
                section.Margin = new Padding(0, 0, 0, D(6));
            }
            // 预览：电池 → 液冷之间是「10px + 分隔线 + 10px」的显式分段（其余组之间只有留白）。
            _liquidGroup.Margin = new Padding(0, D(4), 0, D(6));
            ApplyRowSkins();

            // v2：低频项随 ⚙ 设置弹窗承载——面板先隐藏，弹窗懒建时再托管（模式同校色/自定义）。
            foreach (Control deferred in new Control[] { _themeModePanel!, _officialConsolePanel, panelVersion })
                deferred.Visible = false;
            checkStartup.Visible = false;   // 开机自启随 ⚙ 弹窗承载（真机首验发现其浮在页首）
            BuildFooterV2(D);

            UiVisualStyle.ApplyTitle(labelPerf);
            UiVisualStyle.ApplyTitle(labelGPU);
            UiVisualStyle.ApplyTitle(labelBatteryTitle);
            UiVisualStyle.ApplyWindow(this);

            Controls.Add(_dashboard);
            _dashboard.BringToFront();
            UiVisualStyle.ApplyWindow(this);
            ArrangeDashboard(force: true);
            RefreshThemeButtons();

            void MakeCompactModeButton(Button button)
            {
                button.Image = null;
                button.BackgroundImage = null;
                button.TextImageRelation = TextImageRelation.Overlay;
                button.TextAlign = ContentAlignment.MiddleCenter;
                button.ImageAlign = ContentAlignment.MiddleCenter;
                button.Font = UiStyleBody();   // 预览 .seg 字号 12px ≈ Body(9pt)
                // 深潜座舱：分段控件各段无缝相连，边距必须为零（分隔线由分段边线画）。
                button.Margin = Padding.Empty;
                button.Padding = Padding.Empty;
            }
        }

        /// <summary>
        /// GCU 连接状态指示条：页面栈第一行（性能卡之上），默认/最小窗口与审计视口都落在
        /// 滚动顶部，恒可见。状态真值 = Program.hw 连接快照（MechrevoHw.IsConnected /
        /// IsReconnecting / ConnectionGeneration），映射见 UI.GcuConnectionStatus。
        /// 颜色只是强调，文本标签 + tooltip 承载状态语义（非仅色差）。
        /// </summary>
        void BuildGcuStatusStrip(Func<int, int> scale)
        {
            _panelGcuStatus = new BufferedPanel
            {
                Name = "panelGcuStatus",
                CardStyle = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(scale(12), scale(7), scale(12), scale(7)),
                BackColor = UiVisualStyle.Surface,
                Margin = new Padding(0, 0, 0, scale(6)),
                AccessibleRole = AccessibleRole.StatusBar,
            };
            _labelGcuStatus = new Label
            {
                Name = "labelGcuStatus",
                AutoSize = true,
                Text = "● GCU 状态未知",
                ForeColor = UiVisualStyle.Muted,
                BackColor = _panelGcuStatus.BackColor,
                Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
            };
            _labelGcuStatus.Click += (_, _) => RefreshGcuStatus();
            _panelGcuStatus.Controls.Add(_labelGcuStatus);
            UpdateGcuStatus();
        }

        /// <summary>按当前硬件快照刷新指示条（文本/前景色/tooltip）。UI 线程调用。</summary>
        internal void UpdateGcuStatus()
        {
            if (_labelGcuStatus is null || _panelGcuStatus is null || IsDisposed) return;
            MechrevoLite.Hardware.MechrevoHw? hw = Program.hw;
            var state = UI.GcuConnectionStatus.Resolve(
                hw is not null, hw?.IsConnected == true, hw?.IsReconnecting == true,
                hw is { ConnectionGeneration: > 0 });
            (Color fore, string text, string tooltip) = UI.GcuConnectionStatus.Describe(state);
            if (_labelGcuStatus.Text != text) _labelGcuStatus.Text = text;
            if (_labelGcuStatus.ForeColor != fore) _labelGcuStatus.ForeColor = fore;
            toolTip.SetToolTip(_labelGcuStatus, tooltip);
        }

        /// <summary>测试缝：指示条当前 tooltip 文本（toolTip 字段是 Designer 私有）。</summary>
        internal string GcuStatusTooltip => _labelGcuStatus is null ? "" : toolTip.GetToolTip(_labelGcuStatus);

        /// <summary>ConnectionReady 等后台线程事件的入口：封送到 UI 线程刷新。</summary>
        internal void RefreshGcuStatus()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(() => UpdateGcuStatus()); return; }
            UpdateGcuStatus();
        }

        private void BuildThemeModePanel(Func<int, int> scale)
        {
            if (_themeModePanel is not null) return;

            _themeModePanel = new BufferedPanel
            {
                Name = "panelThemeMode",
                CardStyle = true,   // 同上：面板创建晚于主题重刷
                Height = scale(54),
                Padding = new Padding(scale(12), scale(8), scale(12), scale(8)),
                BackColor = UiVisualStyle.Surface,
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = _themeModePanel.BackColor,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ThemeColumnPercentages.Title));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ThemeColumnPercentages.Day));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ThemeColumnPercentages.Night));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new Label
            {
                Name = "labelThemeModeTitle",
                Text = "界面外观",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                ForeColor = UiVisualStyle.Text,
                Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body, FontStyle.Bold),
            };
            UiVisualStyle.ApplyGlyph(title, UiGlyph.Kind.Contrast);   // 构建期挂：AutoSize 会把图标算进首选宽度
            _dayModeButton = CreateThemeButton("日间", false);
            _nightModeButton = CreateThemeButton("夜间", true);
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(_dayModeButton, 1, 0);
            layout.Controls.Add(_nightModeButton, 2, 0);
            _themeModePanel.Controls.Add(layout);

            Button CreateThemeButton(string text, bool night)
            {
                var button = new Button
                {
                    Name = night ? "buttonNightMode" : "buttonDayMode",
                    Text = text,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(scale(4), 0, 0, 0),
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                };
                button.Click += (_, _) => ApplyThemeMode(night);
                return button;
            }
        }

        private static readonly string[] DashboardAuditPageNames = { "Main" };
        internal int DashboardPageCount => 1;
        internal string DashboardPageAuditName(int index) => DashboardAuditPageNames[0];
        internal void SelectDashboardPageForAudit(int index) => SelectDashboardPage(index, persist: false);

        internal void ShowFirstRunGuideIfNeeded()
        {
            const int guideVersion = 1;
            if (Program.UiAuditMode || IsDisposed || AppConfig.Get("onboarding_version", 0) >= guideVersion) return;

            using var guide = new FirstRunGuideForm();
            DialogResult result = guide.ShowDialog(this);
            AppConfig.Set("onboarding_version", guideVersion);
            if (result == DialogResult.OK)
                SelectDashboardPage(2, persist: true);
        }

        /// <summary>
        /// 单页纵排（DESIGN.md v2）后标签页不存在了；保留空实现给审计与引导流程的旧调用点，
        /// 这些调用点在 P2/P3 迁到新的导航方式后删除。
        /// </summary>
        private void SelectDashboardPage(int index, bool persist)
        {
        }

        public void RefreshDeviceCapabilities()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(RefreshDeviceCapabilities); return; }

            MechrevoHw? hw = Program.hw;
            MechrevoDeviceCapabilities caps = hw?.Capabilities ?? _deviceCapabilities;
            bool audit = Program.UiAuditMode && !Program.UiAuditUseReportedCapabilities;
            bool Show(bool supported) => audit || supported;

            bool StaticQuick(string key) => key switch
            {
                "numpad" => caps.Numpad,
                "acrecovery" => caps.AcRecovery,
                "fanboost" => caps.FanBoost,
                // 任务栏、主题、「息屏（不睡眠）」与「开机自启动」是 Windows/系统能力，不是这台机器的
                // 硬件能力：既不依赖 GCU 连接，也没有机型差异，所以恒可见。
                "taskbarautohide" or "transparency" or "darktheme" or "monitoroff" or "startup" => true,
                _ => false,
            };

            var quickVisibility = new List<bool>(_quickSwitches.Length);
            var visibleByKey = new Dictionary<string, bool>(_quickSwitches.Length);
            foreach (CheckBox box in _quickSwitches)
            {
                string key = box.Tag as string ?? "";
                bool visible = audit || hw?.SupportsQuickSwitch(key) == true || StaticQuick(key);
                box.Visible = visible;
                visibleByKey[key] = visible;
                quickVisibility.Add(visible);
            }
            // 整组都被机型/能力挡住时，连组标题一起收掉——否则会留下一个只有小标题的空块。
            // 判组要用刚算出的 visibleByKey，不能读 child.Visible：那个属性的 getter 把父容器的
            // 隐藏也算作不可见，页面没选中时整页为 false，会把所有组误收。
            foreach ((string groupTitle, string[] keys) in QuickSwitchGroups)
            {
                if (!_quickGroupContainers.TryGetValue(groupTitle, out (Control Header, Control Grid) containers)) continue;
                bool anyVisible = keys.Any(key => visibleByKey.TryGetValue(key, out bool visible) && visible);
                containers.Header.Visible = anyVisible;
                containers.Grid.Visible = anyVisible;
            }
            // 快捷开关面板现在装在「更多开关」折叠组里：可见性由组容器统一承担，
            // 不再直接对 panelQuickSwitch 设 Visible（否则组收起时控制链路会打架）。
            bool quick = audit || quickVisibility.Any(visible => visible);
            if (_quickGroup is not null)
            {
                RefreshQuickGroupSummary();
            }

            bool keyboard = Show(caps.Keyboard || hw?.SupportsKeyboard == true);
            bool lightbar = Show(caps.Lightbar || hw?.SupportsLightbar == true);
            bool logo = Show(caps.LogoLight || hw?.SupportsLogoLight == true);
            bool refresh = Show(caps.DisplayRefresh || hw?.SupportsDisplayRefresh == true);
            // v2 屏幕行 = 屏幕行头 + 刷新率分段 + 亮度滑条（灯效/自动刷新率已入弹窗/灯光组）
            bool anyLighting = keyboard || lightbar || logo;
            if (_lightGroup is not null)
            {
                foreach (Control descendant in AllDescendants(_lightGroup))
                {
                    if (descendant.Name == "rowKeyboard") descendant.Visible = keyboard;
                    if (descendant.Name == "rowLightbar") descendant.Visible = lightbar;
                    if (descendant.Name == "rowLogo") descendant.Visible = logo;
                }
            }
            if (_lightGroup is not null)
            {
                _lightChannelKeyboard = keyboard;
                _lightChannelLightbar = lightbar;
                _lightChannelLogo = logo;
                EnableSection(_lightGroup, audit || anyLighting);
                SyncLightRows();
            }
            // 键盘控制器状态行（设计 §5）：只有确定性「不支持」才提示已改走官方通道；Supported/Unknown 隐藏，
            // 现有用户布局不变。判定只读缓存——**绝不**在 UI 线程（这里）启动探测；审计模式下判定保持
            // Unknown，标签因审计而可见（布局演练），路径仍走 HID 分支、不碰硬件。
            if (_lblKeyboardControllerStatus is not null)
            {
                bool controllerUnsupported = Program.rgb?.ControllerAvailability == FeatureAvailability.Unsupported;
                _lblKeyboardControllerStatus.Visible = keyboard && (audit || controllerUnsupported);
            }

            bool brightness = Show(hw?.ScreenBrightnessSeen == true);
            bool calibration = Show(caps.ColorCalibration || hw?.SupportsColorCalibration == true);
            bool overdrive = Show(caps.LcdOverdrive || hw?.SupportsLcdOverdrive == true);
            _overdriveAvailable = audit || overdrive;
            if (_overdriveChk is not null) _overdriveChk.Visible = _overdriveAvailable;
            // 设置弹窗的「显示」组标题只在响应加速行可见时渲染：空组不保留标题。
            _settingsDialog?.SetDisplayGroupAvailable(_overdriveAvailable);
            if (_calibCombo is not null) _calibCombo.Visible = audit || calibration;
            bool display = brightness || calibration || overdrive;
            bool liquidCooling = Show(caps.LiquidCooling || hw?.SupportsLiquidCooling == true);
            // 亮度行/Hz 行是固定行高（26/34/28 字面量）：隐藏内容时必须把行高一起收为 0，
            // 否则留下空带（例如「有校色无刷新率」的机型会留 34px 空行）。
            SetScreenRowVisible(2, brightness, "labelScreenBrightness", "sliderScreenBrightness", "labelScreenBrightnessValue");
            if (_hzSegTable is not null) SetScreenRowControls(1, _hzButtons.Count > 0, _hzSegTable);
            SetVisible("panelBrightness", display);

            bool turbo = Show(caps.TurboMode);
            // 静音狂暴入口只在证据确认支持时出现（Unknown/Unsupported 一律隐藏，见
            // MechrevoDeviceCapabilities.SilentTurboAvailability 与官方判据 CCUWinUI.decompiled.cs:57721）。
            bool silentTurbo = Show(caps.SilentTurboAvailability == FeatureAvailability.Supported);
            bool custom = Show(caps.CpuPerformanceTuning || caps.FanSettings || hw?.HasAnyCustomRange == true);
            if (buttonSilentTurbo is not null) buttonSilentTurbo.Visible = silentTurbo;
            buttonTurbo.Visible = turbo;
            if (buttonCustomMode is not null) buttonCustomMode.Visible = custom;
            ReflowPerformanceButtons(silentTurbo, turbo, custom);

            // Runtime support flags are authoritative when GCU reports them. Do
            // not resurrect a stale registry capability with an OR expression.
            bool eco = Show(hw?.IgpuOnlyStatusSupport ?? caps.IgpuOnly);
            bool ultimate = Show(hw?.DgpuDirectStatusSupport ?? caps.DgpuDirect);
            bool gpuCapabilitiesKnown = caps.ProfileAvailable || hw?.SettingStatusSeen == true;
            if (!audit && gpuCapabilitiesKnown && !eco && AppConfig.Is("gpu_auto"))
            {
                AppConfig.Set("gpu_auto", 0);
                AppConfig.Set("gpu_mode", MechrevoService.GpuStandard);
            }
            bool gpu = audit || eco || ultimate;
            panelGPU.Visible = gpu;
            VisualiseGPUButtons(eco || ultimate, ultimate, eco);

            _enabledDashboardSections.Clear();
            EnableSection(panelPerformance, true);
            // GCU 状态条不依赖机型能力，恒可见（能力门禁 Clear 后必须重发，同 panelPerformance）。
            EnableSection(_panelGcuStatus!, true);
            EnableSection(panelGPU, gpu);
            EnableSection(_telemetryPanel, true);
            // 液冷/快捷/灯光装在折叠组里：能力位决定**组**的出现与否。
            EnableSection(_liquidGroup, liquidCooling);
            EnableSection(_quickGroup, quick);
            // 灯光组的 EnableSection 必须在 Clear 之后重发：上方 2650 行的调用发生在
            // 本次 Clear 之前，set 成员会被这里清掉，ArrangeDashboard 就不会把
            // 灯光组装进页面栈（审计/真机都实测过整组消失）。
            EnableSection(_lightGroup, audit || anyLighting);
            EnableSection(panelBattery, true);
            EnableSection(Controls.Find("panelBrightness", true).FirstOrDefault(), display || refresh);
            // v2：主题/官方控制台/版本面板由 ⚙ 设置弹窗承载（懒建+托管），
            // 此处不能再 EnableSection——否则面板会在页面顶部重新浮动出现（真机首验实证）。
            // footer 是页面固定底栏，仍需启用。
            EnableSection(panelFooter, true);

            string fingerprint = string.Join('|', _dashboardSections.Select(section => _enabledDashboardSections.Contains(section))) + '|' +
                string.Join('|', quickVisibility) + $"|{keyboard}|{lightbar}|{logo}|{refresh}|{turbo}|{silentTurbo}|{custom}|{eco}|{ultimate}|{_lightGroup?.Summary}";
            if (fingerprint == _lastCapabilityLayout) return;
            _lastCapabilityLayout = fingerprint;
            // 刷新率分段按钮随 Hz 列表重建（签名守卫内部处理）
            SyncHzButtons();
            ArrangeDashboard(force: true);
            PerformLayout();
            UpdateTelemetryText();

            void SetVisible(string name, bool visible)
            {
                Control? control = Controls.Find(name, true).FirstOrDefault();
                if (control is not null) control.Visible = visible;
            }

            void SetScreenRowVisible(int row, bool visible, params string[] controlNames)
            {
                if (_screenRowsLayout is null) return;
                var controls = controlNames
                    .Select(name => Controls.Find(name, true).FirstOrDefault())
                    .OfType<Control>()
                    .ToArray();
                SetScreenRowControls(row, visible, controls);
            }

            void EnableSection(Control? section, bool enabled)
            {
                if (section is null) return;
                section.Visible = enabled;
                if (enabled) _enabledDashboardSections.Add(section);
            }
        }

        /// <summary>
        /// 屏幕卡固定行的显隐：内容隐藏时把该行 RowStyle 高度收为 0（恢复时还原字面量行高）。
        /// 只藏子控件不动行高会留下固定高的空带——这是能力门禁「隐藏必须收拢」的关键路径。
        /// </summary>
        void SetScreenRowControls(int row, bool visible, params Control[] controls)
        {
            if (_screenRowsLayout is null || row < 0 || row >= ScreenRowHeights.Length) return;
            foreach (Control control in controls) control.Visible = visible;
            RowStyle style = _screenRowsLayout.RowStyles[row];
            if (_screenRowsLayout.Parent is not Control card) return;
            // 幂等增量：只在状态真的翻转时动样式/卡高。RefreshDeviceCapabilities 会在审计的
            // form.Scale 之后再次排队执行——若每次都按字面量重写行高，会把已缩放的样式打回
            // 未缩放值，行高与卡片失配（实测 100pct 视口 labelScreenBrightness 假裁切）。
            // 改 RowStyle.Height 不会使 TableLayoutPanel 布局失效（WinForms 已知行为），
            // 所以翻转后必须显式重排。
            if (!visible)
            {
                if (style.Height <= 0) return;
                // 采样当前渲染行高（缩放后与字面量不同），恢复时按采样值还原。
                int rendered = _screenRowsLayout.GetRowHeights()[row];
                _screenRowVisibleHeights[row] = rendered > 0 ? rendered : ScreenRowHeights[row];
                style.Height = 0;
                card.Height -= _screenRowVisibleHeights[row];
            }
            else
            {
                if (style.Height > 0) return;
                int restore = _screenRowVisibleHeights[row] > 0 ? _screenRowVisibleHeights[row] : ScreenRowHeights[row];
                style.Height = restore;
                card.Height += restore;
            }
            _screenRowsLayout.SuspendLayout();
            _screenRowsLayout.ResumeLayout(true);
        }

        /// <summary>
        /// 把一组按钮平均铺进单行 TableLayoutPanel，**仅在布局真的变了时才动控件树**。
        ///
        /// 为什么必须有这个守卫：`Controls.Clear()` 加重新添加会让子控件短暂脱离父容器，
        /// 屏幕上就是一次可见的闪烁。而这几个重排的调用方 `RefreshDeviceCapabilities`
        /// 挂在 `CapabilitiesChanged` 上，那个事件在十二个状态主题上都会触发，
        /// 其中液冷/设置/风扇状态每三秒就被 GETSTATUS 轮询回来一次——
        /// 于是按钮每三秒被拆掉重建一次，表现就是「正常使用时按钮经常闪」。
        /// 布局签名用控件名按顺序拼，能精确区分「哪几个按钮、什么次序」。
        /// </summary>
        static bool ReflowSingleRow(TableLayoutPanel table, List<Control> buttons, ref string lastLayout)
        {
            // 带上数量前缀：空行的名字拼接是空串，会和「从未布局过」的初值撞在一起，
            // 于是首次就摊到空行时会跳过布局、把 ColumnCount 留在 0。
            string signature = buttons.Count + ":" + string.Join('|', buttons.Select(button => button.Name));
            if (signature == lastLayout) return false;
            lastLayout = signature;

            table.SuspendLayout();
            table.Controls.Clear();
            table.ColumnStyles.Clear();
            table.ColumnCount = Math.Max(1, buttons.Count);
            for (int i = 0; i < buttons.Count; i++)
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / Math.Max(1, buttons.Count)));
                table.Controls.Add(buttons[i], i, 0);
            }
            table.ResumeLayout(true);
            return true;
        }

        void ReflowPerformanceButtons(bool silentTurbo, bool turbo, bool custom)
        {
            var buttons = new List<Control> { buttonSilent, buttonBalanced };
            if (silentTurbo && buttonSilentTurbo is not null) buttons.Add(buttonSilentTurbo);
            if (turbo) buttons.Add(buttonTurbo);
            if (custom && buttonCustomMode is not null) buttons.Add(buttonCustomMode);
            if (ReflowSingleRow(tablePerf, buttons, ref _lastPerformanceLayout))
                ApplySegmentRow(buttons);
        }

        /// <summary>
        /// 分段控件整合（DESIGN.md）：按当前实际可见的按钮集合分配首/中/尾与零间距。
        /// 状态仍由既有 Activated 状态机驱动，这里只定形。
        /// </summary>
        static void ApplySegmentRow(List<Control> buttons)
        {
            foreach (Control control in buttons) control.Margin = Padding.Empty;
            UiVisualStyle.ApplySegmentGroup(buttons.OfType<RButton>().ToArray());
        }

        /// <summary>
        /// 把内容宿主与固定底栏挂进仪表盘网格，并保证全树同名控件唯一：若本路径被重入
        /// （主题/能力/硬件刷新误触发），先移除并释放陈旧同名实例，避免重复 Add 产生多个
        /// 底栏/内容宿主（run8 报告的多重 footer 重影根因）。可安全重复调用。
        /// </summary>
        internal void AttachDashboardChrome()
        {
            if (_dashboard is null || _dashboardScroll is null || panelFooter is null) return;
            AttachGridChildOnce(_dashboard, _dashboardScroll, 0, 0);
            AttachGridChildOnce(_dashboard, panelFooter, 0, 1);
        }

        static void AttachGridChildOnce(TableLayoutPanel host, Control child, int column, int row)
        {
            foreach (Control stale in host.Controls.Cast<Control>()
                         .Where(c => !ReferenceEquals(c, child) && c.Name == child.Name).ToArray())
            {
                host.Controls.Remove(stale);
                stale.Dispose();
            }
            if (ReferenceEquals(child.Parent, host))
                host.SetCellPosition(child, new TableLayoutPanelCellPosition(column, row));
            else
                host.Controls.Add(child, column, row);
        }

        private void ArrangeDashboard(bool force = false)
        {
            if (_dashboardStack is null || _arrangingDashboard || IsDisposed) return;
            _arrangingDashboard = true;
            try
            {
                // 布局签名守卫：RefreshDeviceCapabilities 与 ApplyResponsiveBounds 都会调到这里，
                // 不签名的 Clear+Add 会闪。签名 = 启用分区的名字有序拼接。
                string signature = string.Join('|', _dashboardSections
                    .Where(section => _enabledDashboardSections.Contains(section))
                    .Select(section => section.Name));
                if (!force && signature == _lastStackLayout) return;
                _lastStackLayout = signature;

                _dashboardStack.SuspendLayout();
                _dashboardStack.Controls.Clear();
                _dashboardStack.RowStyles.Clear();
                int row = 0;
                foreach (Control section in _dashboardSections)
                {
                    if (!_enabledDashboardSections.Contains(section)) continue;
                    _dashboardStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    _dashboardStack.Controls.Add(section, 0, row++);
                }
                _dashboardStack.RowCount = row;
                _dashboardStack.ResumeLayout(true);
            }
            finally { _arrangingDashboard = false; }
            UpdateDashboardWindowHeight();
            UpdateDashboardScroll();
        }

                /// <summary>
        /// 窗口高度固定（预览 1:1）：高 = min(529 逻辑, 工作区上限)，内容超出由内嵌滚动条滚动。
        /// 不再随内容变化——否则展开/收起折叠组会「先出内容、再整体上移」，真机可见两段式跳动。
        /// 签名守卫防止「改高度→触发布局→再改高度」的反馈环（紧凑化轮 248+288 条溢出的教训）。
        /// </summary>
        void UpdateDashboardWindowHeight()
        {
            if (_dashboardStack is null || _dashboard is null || panelFooter is null || IsDisposed) return;
            // 最大化/最小化时客户区由系统裁决，绝不能把紧凑高度写回去——回写会把内容宿主钉在
            // 旧高、底栏停在中途与内容重叠（run8 布局缺陷的 108px 重叠）。还原为 Normal 后
            // 才重新按紧凑高度校正。
            if (WindowState != FormWindowState.Normal) return;
            float scale = EffectiveLayoutScale;
            int Scale(int logical) => (int)Math.Round(logical * scale);
            Rectangle area = _lastWorkingArea.IsEmpty
                ? Screen.FromControl(this).WorkingArea
                : _lastWorkingArea;
            int frameHeight = Height - ClientSize.Height;
            int maxHeight = Math.Max(Scale(300), area.Height - frameHeight);
            int desired = Math.Min(Scale(CompactDashboardLogicalClientSize.Height), maxHeight);   // 固定窗高（预览 529 逻辑），超出由内嵌滚动
            // 签名必须包含目标工作区高度：审计逐视口换工作区时，栈签名不变但封顶值
            // 变了——不含 area 的话第二次起就再也不重算（200% 视口 window-overflow 实证）。
            string signature = $"{area.Height}|{desired}|{ClientSize.Width}";
            if (signature == _lastWindowHeightSignature) return;
            _lastWindowHeightSignature = signature;
            ClientSize = new Size(ClientSize.Width, desired);
            // 高度变化后重新贴右下角（真机验收：顶边固定时增长朝下、窗口底边掉出工作区；
            // 收起后又不回底、下方露出桌面）。底边始终贴工作区底部，高度增长向上展开。
            // 只在窗口已经正式呈现过、工作区已记录后才重贴——构造期的 FromControl 会落到
            // 多显示器里 OS 给的默认落点（真机三屏实证：窗口被挪去了副屏）。
            if (_hasPresentedWindow && !_lastWorkingArea.IsEmpty)
                ResponsiveLayout.PlaceBottomRight(this, _lastWorkingArea);
            UpdateDashboardScroll();
        }

        private bool _applyingResponsiveBounds;
        private int _dashboardWindowDpi;
        private bool _hasPresentedWindow;
        Rectangle _lastWorkingArea;

        private void ApplyDashboardWindowMetrics(bool force = false)
        {
            // 必须用 EffectiveLayoutScale（审计下取 AuditLayoutScale、实机取真实 DPI 倍率），
            // 不能读 UiDpi.Layout(宿主真实 DPI)：UI 审计把窗体按视口 DPI 缩放后，
            // 这里若按宿主 DPI 重算，窗口会缩回 100% 而字体停在放大后的尺寸，
            // 结果是「文字溢出压到相邻行」的假缺陷（125% 系统页截图即此形态）。
            float scale = EffectiveLayoutScale;
            int scaleKey = (int)Math.Round(scale * 96F);
            if (!force && _dashboardWindowDpi == scaleKey) return;

            _dashboardWindowDpi = scaleKey;
            int Scale(int logical) => (int)Math.Round(logical * scale);
            MinimumSize = new Size(Scale(CompactDashboardLogicalClientSize.Width), Scale(300));
            // 宽度固定（DESIGN.md v2 §5：420 逻辑）；高度只是初值，内容高度由
            // UpdateDashboardWindowHeight 在布局之后校正。
            _lastWindowHeightSignature = "";
            // 最大化/最小化时不回写客户区尺寸（DPI 变化落在最大化态时尤其重要）。
            if (WindowState == FormWindowState.Normal)
                ClientSize = new Size(
                    Scale(CompactDashboardLogicalClientSize.Width),
                    Scale(CompactDashboardLogicalClientSize.Height));
        }

        private void ConfigureResponsiveWindow()
        {
            AutoSize = false;
            AutoSizeMode = AutoSizeMode.GrowOnly;
            AutoScroll = false;
            AutoScrollMargin = Size.Empty;
            ApplyDashboardWindowMetrics(force: true);
            ApplyResponsiveBounds();
        }

        private FormWindowState _lastWindowState = FormWindowState.Normal;

        /// <summary>
        /// 最大化/还原会切换窗口状态：此时必须按新的客户区重排仪表盘并整窗重绘，否则内容宿主
        /// 可能停在旧几何、最大化出来的客户区残留上一帧的底栏重影（run8 layout 缺陷）。紧凑
        /// 高度回写只属于 Normal 态（见 <see cref="UpdateDashboardWindowHeight"/>）。
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_dashboard is null || IsDisposed) return;
            if (WindowState == _lastWindowState) return;
            _lastWindowState = WindowState;
            _dashboard.PerformLayout();
            UpdateDashboardScroll();
            Invalidate(true);
        }

        public override void ApplyResponsiveBounds(Rectangle? workingArea = null)
        {
            if (_applyingResponsiveBounds || IsDisposed) return;
            _applyingResponsiveBounds = true;
            try
            {
                // The constructor runs before the window is assigned to its final
                // monitor. Reapply the logical client size once DeviceDpi is known.
                // 记住目标工作区：窗口高度封顶要用它（审计=视口工作区；实机=真实屏幕）。
                // Screen.FromControl 在窗体尚未显示时返回的是主屏，会把审计视口的
                // 封顶算错——这也是第一轮审计 window-overflow 报了 34 条的根因。
                _lastWorkingArea = workingArea
                    ?? (IsHandleCreated ? Screen.FromControl(this).WorkingArea : Rectangle.Empty);
                ApplyDashboardWindowMetrics();
                ArrangeDashboard();
                PerformLayout();
                base.ApplyResponsiveBounds(workingArea);
                ArrangeDashboard();
                PerformLayout();
                // 最大化/最小化时窗口位置由系统裁决，不要贴角挪窗（否则会破坏最大化几何）。
                if (WindowState == FormWindowState.Normal)
                    ResponsiveLayout.PlaceBottomRight(this, workingArea);
                UpdateDashboardWindowHeight();
                NormalizeScreenCardMetrics();
            }
            finally { _applyingResponsiveBounds = false; }
        }

        /// <summary>
        /// 屏幕卡与「屏幕」行头的规范度量（构建期兜底）。
        /// DPI 自动缩放会给构建期创建的控件/布局样式再乘一次缩放系数：实测 panelBrightness
        /// 的内边距 28 → 49、屏幕行头图标列 46 → 80，导致整卡右移 21px、图标再右移 16px。
        /// 这里在布局完成后按逻辑值重设一次（幂等），使其与 性能模式/显卡模式/电池 三张卡一致。
        /// </summary>
        void NormalizeScreenCardMetrics()
        {
            int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
            if (Controls.Find("panelBrightness", true).FirstOrDefault() is Control brightCard)
                brightCard.Padding = new Padding(D(16), D(2), D(12), 0);
            if (Controls.Find("headIcon_Display", true).FirstOrDefault() is Control icon
                && icon.Parent is TableLayoutPanel head
                && head.ColumnStyles.Count > 0)
                head.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, D(26));
        }

        private void ButtonAmdOled_Click(object? sender, EventArgs e)
        {
            AmdDisplay.RunAdrenaline();
            activateCheck = true;
        }

        private void LabelBattery_Click(object? sender, EventArgs e)
        {
            HardwareControl.chargeWatt = !HardwareControl.chargeWatt;
            RefreshSensors(true);
        }

        private void ButtonEnergySaver_Click(object? sender, EventArgs e)
        {
            KeyboardHook.KeyKeyPress(Keys.LWin, Keys.A);
        }

        private void LabelBacklight_Click(object? sender, EventArgs e)
        {
            if (AppConfig.IsDynamicLighting() && DynamicLightingHelper.IsEnabled()) DynamicLightingHelper.OpenSettings();
        }

        private void ButtonFHD_Click(object? sender, EventArgs e)
        {
            ScreenControl.ToogleFHD();
        }

        private void ButtonHDRControl_Click(object? sender, EventArgs e)
        {
            ScreenControl.ToogleHDRControl();
        }

        private void SliderBattery_ValueChanged(object? sender, EventArgs e)
        {
            VisualiseBatteryTitle(sliderBattery.Value);
        }

        private void SliderBattery_KeyUp(object? sender, KeyEventArgs e)
        {
            // 真实键盘手势当刻捕获凭证：随后的去抖计时器/EC 写入即使超过 500ms 也不会把这次设置丢掉。
            BatteryControl.CaptureChargeLimitGesture();
            batteryTimer.Stop();
            batteryTimer.Start();
        }

        private void SliderBattery_MouseUp(object? sender, MouseEventArgs e)
        {
            // 真实鼠标手势当刻捕获凭证（同上）。
            BatteryControl.CaptureChargeLimitGesture();
            batteryTimer.Stop();
            batteryTimer.Start();
        }

        private void LabelCharge_Click(object? sender, EventArgs e)
        {
            BatteryControl.BatteryReport();
        }

        // ASUS Splendid（GameVisual）色域 / 视觉模式 / OLED 假调光的整块界面已删除：
        //   LabelVisual_Click / InitVisual / CycleVisualMode /
        //   ComboGamut_SelectedValueChanged / ComboVisual_SelectedValueChanged /
        //   VisualiseBrightness / VisualiseDisabled / VisualiseGamut / SliderGamma_ValueChanged
        //
        // 背后的 Display/VisualControl.cs 与 Display/ColorProfileHelper.cs 一并删除。
        // 那条通路的唯一执行入口是 AsusSplendid.exe（只能经 ASUS 的 ATKWMIACPIIO 驱动定位），
        // 色域 ICC 也只存在于 ASUS 的 GameVisual 目录——机械革命机型上这些前提全部不成立。
        // 宿主面板 panelGamma 本来就没有被 Add 到窗体上，整块界面从来没有渲染过。
        //
        // 本机的色彩校正走另一条路：屏幕行头内联下拉 + MechrevoService.SetColorCalibration；
        // 真实屏幕亮度走 Settings/DeviceSwitchItemStatus 的 ScreenBrightness + BrightnessCommitQueue。
        // 注意别把两者混淆：删掉的 sliderGamma 调的是 Splendid 的 gamma 假调光，不是屏幕亮度。

        public void VisualiseAmdOled(bool status = false)
        {
            if (InvokeRequired) { Invoke(() => VisualiseAmdOled(status)); return; }
            buttonAmdOled.Visible = status;
        }

        private void ButtonOverlay_Click(object? sender, EventArgs e)
        {
            ToggleOverlay();
        }

        // ROG Ally 掌机的四个可视化方法（VisualiseAlly / VisualiseBacklight /
        // VisualiseFPSLimit / VisualiseAutoTDP）已删除。它们全部零调用方，
        // 而 VisualiseAlly 本身的签名就是 `bool visible = false` 加首行 `if (!visible) return;`
        // ——连自己都默认不做事。宿主面板 panelAlly 一并删除。

        private void SettingsForm_Focused(object? sender, EventArgs e)
        {
            if (activateCheck)
            {
                buttonAmdOled.Visible = AmdDisplay.IsOledPowerOptimization();
                activateCheck = false;
            }
        }
        private void SettingsForm_LostFocus(object? sender, EventArgs e)
        {
            lastLostFocus = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        private void ButtonBatteryFull_Click(object? sender, EventArgs e)
        {
            BatteryControl.ToggleBatteryLimitFull();
        }

        private void ButtonBatteryFull_MouseLeave(object? sender, EventArgs e)
        {
            batteryFullMouseOver = false;
            RefreshSensors(true);
        }

        private void ButtonBatteryFull_MouseEnter(object? sender, EventArgs e)
        {
            batteryFullMouseOver = true;
            labelCharge.Text = Properties.Strings.BatteryLimitFull;
        }

        private void PanelBattery_MouseEnter(object? sender, EventArgs e)
        {
            batteryMouseOver = true;
            ShowBatteryWear();
        }

        private void PanelBattery_MouseLeave(object? sender, EventArgs e)
        {
            batteryMouseOver = false;
            RefreshSensors(true);
        }

        private void ShowBatteryWear()
        {
            //Refresh again only after 15 Minutes since the last refresh
            if (lastBatteryRefresh == 0 || Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastBatteryRefresh) > 15 * 60_000)
            {
                lastBatteryRefresh = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            }

            if (HardwareControl.batteryHealth != -1)
            {
                labelCharge.Text = Properties.Strings.BatteryHealth + ": " + Math.Round(HardwareControl.batteryHealth, 1) + "%";
            }
        }

        private void SettingsForm_VisibleChanged(object? sender, EventArgs e)
        {
            if (Program.UiAuditMode) return;
            _sensorTimer.Enabled = this.Visible;
            if (this.Visible)
            {
                Task.Run((Action)RefreshPeripheralsBattery);
                updateControl.CheckForUpdates();
            }
        }

        private void RefreshPeripheralsBattery()
        {
            PeripheralsProvider.RefreshBatteryForAllDevices(true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_POWERBROADCAST)
            {
                // 电源/恢复路径绝不能让 UI 线程卡死：处理体只做轻量、非阻塞的调度
                //（重活各自 Task.Run / 定时器），且任何异常都吞掉留痕，不中断消息泵。
                try { HandlePowerBroadcast(ref m); }
                catch (Exception ex) { Logger.WriteLine("Power broadcast handling failed: " + ex.Message); }
            }

            if (m.Msg == Program.WM_TASKBARCREATED)
            {
                Logger.WriteLine("Taskbar created, re-creating tray icon");
                if (Program.trayIcon is not null) Program.trayIcon.Visible = true;
            }

            try
            {
                base.WndProc(ref m);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        private void HandlePowerBroadcast(ref Message m)
        {
            if (m.WParam == (IntPtr)NativeMethods.PBT_APMSUSPEND)
            {
                Logger.WriteLine("System Suspend");
                m.Result = (IntPtr)1;
            }

            if (m.WParam == (IntPtr)NativeMethods.PBT_APMRESUMEAUTOMATIC)
            {
                Logger.WriteLine("System Resume");
                BatteryControl.AutoBattery();
                m.Result = (IntPtr)1;
            }

            if (m.WParam == (IntPtr)NativeMethods.PBT_POWERSETTINGCHANGE)
            {
                var settings = (NativeMethods.POWERBROADCAST_SETTING)m.GetLParam(typeof(NativeMethods.POWERBROADCAST_SETTING));
                if (settings.PowerSetting == NativeMethods.PowerSettingGuid.LIDSWITCH_STATE_CHANGE)
                {
                    switch (settings.Data)
                    {
                        case 0:
                            Logger.WriteLine("Lid Closed");
                            BatteryControl.AutoBattery();
                            InputDispatcher.lidClose = true;
                            Aura.ApplyBrightness(0, "Lid");
                            break;
                        case 1:
                            Logger.WriteLine("Lid Open");
                            InputDispatcher.InitFNLock();
                            InputDispatcher.lidClose = false;
                            Aura.ApplyBrightness(InputDispatcher.GetBacklight(), "Lid");
                            break;
                    }

                }
                else if (settings.PowerSetting == NativeMethods.PowerSettingGuid.EnergySaverStatus)
                {
                    Logger.WriteLine("Battery Saver: " + settings.Data);
                    buttonEnergySaver.Visible = settings.Data != 0;
                }
                else
                {
                    switch (settings.Data)
                    {
                        case 0:
                            Logger.WriteLine("Monitor Power Off");
                            Aura.SleepBrightness();
                            Program.hardwareOverlay?.SuspendForDisplayOff();
                            break;
                        case 1:
                            Logger.WriteLine("Monitor Power On");
                            if (!Program.SetAutoModes(wakeup: true)) BatteryControl.AutoBattery();
                            Program.hardwareOverlay?.ResumeForDisplayOn();
                            break;
                        case 2:
                            Logger.WriteLine("Monitor Dimmed");
                            break;
                    }
                }
                m.Result = (IntPtr)1;
            }
        }

        public void SetContextMenu()
        {
            int opMode = Program.hw?.OperatingMode ?? -1;   // 0=办公 1=游戏 2=狂暴
            bool silentTurbo = MechrevoLite.Hardware.MechrevoService.IsSilentTurboActive;

            foreach (ToolStripItem item in contextMenuStrip.Items.Cast<ToolStripItem>().ToList())
            {
                if (item is ToolStripMenuItem menuItem) menuItem.Dispose();
            }
            contextMenuStrip.Items.Clear();
            contextMenuStrip.ShowCheckMargin = true;
            contextMenuStrip.ImageScalingSize = new Size(16, 16);
            contextMenuStrip.ShowImageMargin = false;
            Padding padding = new Padding(5, 5, 5, 5);

            void AddDisabledTitle(string text)
            {
                var t = new ToolStripMenuItem(text) { Margin = padding, Enabled = false };
                contextMenuStrip.Items.Add(t);
            }
            void AddAction(string text, bool checked_, Action onClick)
            {
                var item = new ToolStripMenuItem(text) { Margin = padding, Checked = checked_ };
                item.Click += (_, _) => onClick();
                contextMenuStrip.Items.Add(item);
            }
            void AddAsyncAction(string text, bool checked_, Func<Task> onClick)
            {
                var item = new ToolStripMenuItem(text) { Margin = padding, Checked = checked_ };
                item.Click += async (_, _) =>
                {
                    item.Enabled = false;
                    try { await onClick(); }
                    catch (Exception ex) { Logger.WriteLine("Tray action failed: " + ex.Message); }
                    finally { if (!item.IsDisposed) item.Enabled = true; }
                };
                contextMenuStrip.Items.Add(item);
            }

            // ---- 性能模式（含静音狂暴）----
            AddDisabledTitle("性能模式");
            AddAsyncAction("静音 (办公)", opMode == 0, async () => { if (Program.service is not null) await Program.service.SwitchMode(MechrevoLite.Hardware.MechrevoService.ModeOffice); });
            AddAsyncAction("平衡 (游戏)", opMode == 1, async () => { if (Program.service is not null) await Program.service.SwitchMode(MechrevoLite.Hardware.MechrevoService.ModeGaming); });
            if (Program.hw?.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported)
            {
                AddAsyncAction("静音狂暴", opMode == 2 && silentTurbo, async () =>
                {
                    if (Program.service is null || !await Program.service.SwitchMode(MechrevoLite.Hardware.MechrevoService.ModeTurbo)) return;
                    await Program.service.SwitchTurboSubMode(true);
                    ShowMode(AsusACPI.PerformanceTurbo);
                    SetContextMenu();
                });
            }
            if (Program.hw?.Capabilities.TurboMode == true)
            {
                AddAsyncAction("狂暴 (增强)", opMode == 2 && !silentTurbo, async () =>
                {
                    if (Program.service is null || !await Program.service.SwitchMode(MechrevoLite.Hardware.MechrevoService.ModeTurbo)) return;
                    if (Program.hw?.Capabilities.TurboSubMode == true)
                        await Program.service.SwitchTurboSubMode(false);
                    ShowMode(AsusACPI.PerformanceTurbo);
                    SetContextMenu();
                });
            }
            AddAsyncAction("风扇增强", Program.hw?.FanBoost ?? false, async () =>
            {
                bool target = !(Program.hw?.FanBoost ?? false);
                if (Program.service is not null) await Program.service.SwitchFanBoost(target);
            });

            // ---- 自定义模式 4 档（当前激活档勾选）----
            int cpi = Program.hw?.CustomProfileIndex ?? -1;
            bool customActive = opMode == 3 && cpi is >= 0 and <= 3;
            var customMenu = new ToolStripMenuItem(customActive ? $"自定义模式（当前 {cpi + 1}）" : "自定义模式")
            {
                Margin = padding,
                Checked = customActive,
                ForeColor = customActive ? UiVisualStyle.Ok : contextMenuStrip.ForeColor,
                Font = new Font(contextMenuStrip.Font, customActive ? FontStyle.Bold : FontStyle.Regular),
            };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var ci = new ToolStripMenuItem("自定义 " + (i + 1)) { Margin = padding, Checked = opMode == 3 && cpi == idx };
                ci.Click += async (_, _) =>
                {
                        if (Program.service is null || !await Program.service.SwitchCustomProfile(idx)) return;
                        if (!MechrevoLite.Hardware.WinPowerPlan.ApplyProfile(idx))
                            Logger.WriteLine($"Tray custom profile {idx + 1}: Windows power settings were not confirmed.");
                        ShowMode(MechrevoLite.Hardware.MechrevoService.ToVisualMode(
                            MechrevoLite.Hardware.MechrevoService.ModeCustom));
                        SetContextMenu();
                };
                customMenu.DropDownItems.Add(ci);
            }
            contextMenuStrip.Items.Add(customMenu);

            contextMenuStrip.Items.Add("-");

            if (isGpuSection)
            {
                var titleGPU = new ToolStripMenuItem(Properties.Strings.GPUMode);
                titleGPU.Margin = padding;
                titleGPU.Enabled = false;
                contextMenuStrip.Items.Add(titleGPU);

                menuEco = new ToolStripMenuItem(Properties.Strings.EcoMode);
                menuEco.Click += ButtonEco_Click;
                menuEco.Margin = padding;
                menuEco.Checked = buttonEco.Activated;
                contextMenuStrip.Items.Add(menuEco);

                menuStandard = new ToolStripMenuItem(Properties.Strings.StandardMode);
                menuStandard.Click += ButtonStandard_Click;
                menuStandard.Margin = padding;
                menuStandard.Checked = buttonStandard.Activated;
                contextMenuStrip.Items.Add(menuStandard);

                menuUltimate = new ToolStripMenuItem(Properties.Strings.UltimateMode);
                menuUltimate.Click += ButtonUltimate_Click;
                menuUltimate.Margin = padding;
                menuUltimate.Checked = buttonUltimate.Activated;
                menuUltimate.Visible = isMuxGpu;
                contextMenuStrip.Items.Add(menuUltimate);

                menuOptimized = new ToolStripMenuItem(Properties.Strings.Optimized);
                menuOptimized.Click += ButtonOptimized_Click;
                menuOptimized.Margin = padding;
                menuOptimized.Checked = buttonOptimized.Activated;
                contextMenuStrip.Items.Add(menuOptimized);

                contextMenuStrip.Items.Add("-");
            }

            // ---- 电池三档 ----
            AddDisabledTitle("电池");
            int bp = Program.hw?.BatteryProtection ?? -1;
            AddAsyncAction("性能 (满充)", bp == 0, async () => { if (Program.hw is not null) await Program.hw.SetBatteryProtection(0); });
            AddAsyncAction("平衡 (80%)", bp == 1, async () => { if (Program.hw is not null) await Program.hw.SetBatteryProtection(1); });
            AddAsyncAction("健康 (60%)", bp == 2, async () => { if (Program.hw is not null) await Program.hw.SetBatteryProtection(2); });

            contextMenuStrip.Items.Add("-");

            AddAction("键盘灯效", false, () => OpenRgbForm());
            AddAction("悬浮监控", AppConfig.IsOverlay(), () => ToggleOverlay());   // Overlay 硬件状态悬浮窗
            AddAction("打开主界面", false, () => Program.SettingsToggle(false, true));
            // 导出诊断包：与 footer「诊断」键同一入口（本地打包，不联网）。
            AddAsyncAction("导出诊断包", false, () => MechrevoLite.Diagnostics.DiagnosticPackCommand.RunAsync(this, null));

            contextMenuStrip.Items.Add("-");

            var quit = new ToolStripMenuItem(Properties.Strings.Quit);
            quit.Click += ButtonQuit_Click;
            quit.Margin = padding;
            contextMenuStrip.Items.Add(quit);

            //contextMenuStrip.ShowCheckMargin = true;
            contextMenuStrip.Renderer = new CustomMenuRenderer();

            InitContextMenuTheme();

            if (Program.trayIcon is not null) Program.trayIcon.ContextMenuStrip = contextMenuStrip;


        }

        public void InitContextMenuTheme()
        {
        _liquidCoolingLightMenu?.ApplyTheme();   // T5：日夜主题切换时同步重读菜单令牌
            if (contextMenuStrip is not null)
            {
                contextMenuStrip.BackColor = this.BackColor;
                contextMenuStrip.ForeColor = this.ForeColor;
            }

            donateControl?.ApplyTheme();
        }

        private void LabelVersion_Click(object? sender, EventArgs e)
        {
            ShowTestFeedback();
        }

        /// <summary>测试反馈弹窗：小范围测试阶段，欢迎加 QQ 提意见。</summary>
        /// <summary>测试反馈弹窗（更新窗口里的「反馈」按钮转到这里，避免 QQ 号写两遍）。</summary>
        internal static void ShowTestFeedback()
        {
            MessageBox.Show(
                "L-Mechrevo 目前处于小范围测试阶段。\n\n如有好的建议或反馈，欢迎添加 QQ：2871715852 与我交流。",
                "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>打开更新窗口（手动检查：绕过跨会话节流）。</summary>
        void ShowUpdateDialog()
        {
            try
            {
                using var form = new MechrevoLite.Update.UpdateForm(autoCheck: false);
                AddOwnedForm(form);
                form.ShowDialog(this);
                // 窗口里可能已经装了新版（程序即将退出），这里不再改按钮状态。
            }
            catch (Exception ex)
            {
                Logger.WriteLine("更新窗口打开失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 静默检测发现新版时只做这一件事：给按钮加角标。不弹窗、不下载、不安装。
        /// </summary>
        internal void MarkUpdateAvailable(MechrevoLite.Update.UpdateInfo info)
        {
            if (IsDisposed) return;
            try
            {
                buttonUpdates.Text = "检查更新 ●";
                buttonUpdates.ForeColor = UiVisualStyle.Accent;
                toolTip.SetToolTip(buttonUpdates, $"发现新版本 {info.LatestVersion}（点击查看）");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("更新角标设置失败：" + ex.Message);
            }
        }


        private static void OnTimedEvent(Object? source, ElapsedEventArgs? e)
        {
            Program.settingsForm.RefreshSensors();
        }

        private void ButtonFHD_MouseHover(object? sender, EventArgs e)
        {
            labelTipScreen.Text = "Switch to " + ((buttonFHD.Text == "FHD") ? "UHD" : "FHD") + " Mode";
        }

        private void Button120Hz_MouseHover(object? sender, EventArgs e)
        {
            labelTipScreen.Text = Properties.Strings.MaxRefreshTooltip;
        }

        private void Button60Hz_MouseHover(object? sender, EventArgs e)
        {
            labelTipScreen.Text = Properties.Strings.MinRefreshTooltip.Replace("60", ScreenControl.MIN_RATE.ToString());
        }

        private void ButtonScreen_MouseLeave(object? sender, EventArgs e)
        {
            labelTipScreen.Text = "";
        }

        private void ButtonScreenAuto_MouseHover(object? sender, EventArgs e)
        {
            labelTipScreen.Text = Properties.Strings.AutoRefreshTooltip.Replace("60", ScreenControl.MIN_RATE.ToString());
        }

        private void ButtonUltimate_MouseHover(object? sender, EventArgs e)
        {
            labelTipGPU.Text = Properties.Strings.UltimateGPUTooltip;
        }

        private void ButtonStandard_MouseHover(object? sender, EventArgs e)
        {
            labelTipGPU.Text = Properties.Strings.StandardGPUTooltip;
        }

        private void ButtonEco_MouseHover(object? sender, EventArgs e)
        {
            labelTipGPU.Text = Properties.Strings.EcoGPUTooltip;
        }

        private void ButtonOptimized_MouseHover(object? sender, EventArgs e)
        {
            labelTipGPU.Text = Properties.Strings.OptimizedGPUTooltip;
        }

        private void ButtonGPU_MouseLeave(object? sender, EventArgs e)
        {
            labelTipGPU.Text = HasPendingGpuRestart() ? "显卡模式已更改，重启后生效。" : "";
        }


        private void ButtonScreenAuto_Click(object? sender, EventArgs e)
        {
            ScreenControl.SetAutoRefresh(1);
            ScreenControl.AutoScreen();
        }


        private async Task RefreshStartupStatusAsync()
        {
            if (Interlocked.Exchange(ref _startupStatusLoading, 1) != 0) return;
            try
            {
                bool? scheduledState = await Task.Run(Startup.ReadScheduledState);
                if (scheduledState is null) throw new InvalidOperationException("Can't read autostart state.");
                bool scheduled = scheduledState.Value;
                if (IsDisposed) return;
                _syncingStartup = true;
                try { checkStartup.Checked = scheduled; }
                finally { _syncingStartup = false; }
                if (scheduled && !AppConfig.Exists("startup_enabled")) AppConfig.Set("startup_enabled", 1);
                checkStartup.Enabled = true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Startup status refresh failed: " + ex.Message);
                if (!IsDisposed) checkStartup.Enabled = true;
            }
            finally { Volatile.Write(ref _startupStatusLoading, 0); }
        }

        private async void CheckStartup_Click(object? sender, EventArgs e)
        {
            // 只挂 Click：程序化回显（RefreshStartupStatusAsync 写 Checked）不再触发计划任务写入。
            if (sender is not CheckBox chk || _syncingStartup || Program.UiAuditMode) return;
            bool requested = chk.Checked;
            if (!NativeMethods.HasFreshUserInput())
            {
                Logger.WriteLine("Autostart (footer) ignored: no fresh user input.");
                await RefreshStartupStatusAsync();
                return;
            }
            chk.Enabled = false;
            try
            {
                bool completed = await Task.Run(() =>
                {
                    return Startup.WriteScheduledState(requested);
                });
                if (completed) AppConfig.Set("startup_enabled", requested ? 1 : 0);
                if (!completed && !IsDisposed)
                {
                    string action = requested ? "创建" : "删除";
                    MessageBox.Show($"无法{action}开机自启动任务。请重新打开控制台后重试。",
                        "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Startup schedule change failed: " + ex.Message);
            }
            finally
            {
                await RefreshStartupStatusAsync();
                if (!IsDisposed) chk.Enabled = true;
            }
        }

        // Anime Matrix / Slash 点阵屏的七个处理器已删除
        // （CheckMatrix_CheckedChanged / CheckMatrixLid_CheckedChanged / ButtonMatrix_Click /
        //  VisualiseMatrixRunning / ComboInterval_DropDownClosed /
        //  ComboMatrixRunning_SelectedValueChanged / ComboMatrix_SelectedValueChanged）。
        //
        // 这一族是华硕 ROG 的机盖点阵屏与 Slash 灯条，机械革命没有对应硬件。
        // 它在这里其实早就跑不起来：InitMatrix 首句 `if (!matrixControl.IsValid)` 就返回
        // （AniMatrixControl 是空存根，IsValid 恒 false），所以那几个 CheckedChanged
        // 从来没被挂上；唯一能 new 出窗体的 ButtonMatrix_Click 挂在一个
        // `buttonMatrix.Visible = false` 的按钮上。
        //
        // 注意别把 KeyboardRgb.ModeMatrix / MatrixSpeed 跟这一族搞混：那是机械革命键盘
        // RGB 的固件灯效档位，是在用的功能，只是名字里也有 Matrix。

        private void LabelCPUFan_Click(object? sender, EventArgs e)
        {
            RefreshSensors(true);
        }

        private void ButtonKeyboardColor2_Click(object? sender, EventArgs e)
        {
            SetColorPicker("aura_color2", Aura.Color2);
        }

        private void SetColorPicker(string colorField, Color initial)
        {
            using RColorPicker colorDlg = new RColorPicker(initial, colorField == "aura_color" && Aura.HasRandomColor());
            colorDlg.ColorChanged += c =>
            {
                AppConfig.Set(colorField, c.ToArgb());
                SetAura();
            };
            colorDlg.ShowDialog(this);
        }

        private void ButtonKeyboardColor_Click(object? sender, EventArgs e)
        {
            SetColorPicker("aura_color", Aura.Color1);
        }

        // 机背灯（ROG Flow Z13 专属的 rear glow）整族已删除：
        //   ButtonRearColor_Click / ComboRearLight_SelectedValueChanged / InitRearLight
        // 加上设计器里的 panelRearLight 子树。
        // InitRearLight 零调用方，而它首句的门禁 AppConfig.HasRearLight() 就是 IsZ13()。

        public void InitAura()
        {
            // Aura 为华硕残留空实现（Stubs，点击无效果）：隐藏相关控件，键盘灯效入口是 RgbForm
            comboKeyboard.Visible = false;
            buttonKeyboardColor.Visible = false;
            labelBacklight.Visible = false;
        }

        public void SetAura()
        {
            Task.Run(() =>
            {
                Aura.ApplyAura();
                VisualiseAura();
            });
        }

        private void _VisualiseAura()
        {
            buttonKeyboardColor.SwatchColor = Aura.Color1;
            buttonKeyboardColor.SwatchColor2 = Aura.HasSecondColor() ? Aura.Color2 : (Color?)null;

            bool dynamic = AppConfig.IsDynamicLighting() && DynamicLightingHelper.IsEnabled() && !AppConfig.IsDynamicLightingOnly();

            if (dynamic)
            {
                labelBacklight.Cursor = Cursors.Hand;
                labelBacklight.Text = Strings.DisableDynamicLighting;
            } else if (Aura.Mode == AuraMode.AMBIENT)
            {
                labelBacklight.Cursor = Cursors.Default;
                labelBacklight.Text = Strings.AmbientModeResources;
            } else
            {
                labelBacklight.Cursor = Cursors.Default;
                labelBacklight.Text = "";
            }
        }

        public void VisualiseAura()
        {
            if (InvokeRequired)
                Invoke(_VisualiseAura);
            else
                _VisualiseAura();
        }

        // InitMatrix() 与 CycleMatrix() 已删除，见上面那段说明。
        // CycleMatrix 的唯一触发来源是 ASUS 热键派发，而 InputDispatcher.RegisterKeys()
        // 本身就是空存根——这条链从头到尾没有起点。


        // CycleAuraMode(int) 已删除：同样只能由 ASUS 热键派发触发，零调用方。

        private void Button120Hz_Click(object? sender, EventArgs e)
        {
            ScreenControl.SetAutoRefresh(0);
            ScreenControl.SetScreen(ScreenControl.MAX_REFRESH);
        }

        private void Button60Hz_Click(object? sender, EventArgs e)
        {
            ScreenControl.SetAutoRefresh(0);
            ScreenControl.SetScreen(ScreenControl.MIN_RATE);
        }


        private void ButtonMiniled_Click(object? sender, EventArgs e)
        {
            ScreenControl.ToogleMiniled();
        }



        public void VisualiseScreen(bool screenEnabled, bool screenAuto, int frequency, int maxFrequency, int overdrive, bool overdriveSetting, int miniled1, int miniled2, bool hdr, bool acm, int fhd, int hdrControl)
        {
            bool advancedColor = hdr || acm;

            ButtonEnabled(button60Hz, screenEnabled);
            ButtonEnabled(button120Hz, screenEnabled);
            ButtonEnabled(buttonScreenAuto, screenEnabled);
            ButtonEnabled(buttonMiniled, screenEnabled);

            labelSreen.Text = screenEnabled
                ? Properties.Strings.LaptopScreen + ": " + frequency + "Hz" + ((overdrive == 1) ? " + " + Properties.Strings.Overdrive : "")
                : Properties.Strings.LaptopScreen + ": " + Properties.Strings.TurnedOff;

            panelScreen.AccessibleName = labelSreen.Text;

            button60Hz.Activated = false;
            button120Hz.Activated = false;
            buttonScreenAuto.Activated = false;

            if (screenAuto)
            {
                buttonScreenAuto.Activated = true;
            }
            else if (frequency == ScreenControl.MIN_RATE)
            {
                button60Hz.Activated = true;
            }
            else if (frequency > ScreenControl.MIN_RATE)
            {
                button120Hz.Activated = true;
            }

            button60Hz.Text = ScreenControl.MIN_RATE + "Hz";

            if (maxFrequency > ScreenControl.MIN_RATE)
            {
                button120Hz.Text = maxFrequency.ToString() + "Hz" + (overdriveSetting ? " + OD" : "");
                panelScreen.Visible = true;
                tableScreen.Visible = true;
            }
            else if (maxFrequency > 0)
            {
                tableScreen.Visible = false;
                panelScreen.Visible = AppConfig.NoGpu();
            }

            if (fhd >= 0)
            {
                buttonFHD.Visible = true;
                buttonFHD.Text = fhd > 0 ? "FHD" : "UHD";
            }

            bool hdrControlVisible = (hdr && hdrControl >= 0);

            if (miniled1 >= 0)
            {
                buttonMiniled.Visible = !hdrControlVisible;
                buttonMiniled.Enabled = !hdr;
                buttonMiniled.Activated = miniled1 == 1 || hdr;
            }
            else if (miniled2 >= 0)
            {
                buttonMiniled.Visible = !hdrControlVisible;
                buttonMiniled.Enabled = !hdr;
                if (hdr) miniled2 = 1; // Show HDR as Multizone Strong

                switch (miniled2)
                {
                    // Multizone On
                    case 0:
                        buttonMiniled.Text = Properties.Strings.Multizone;
                        buttonMiniled.BorderColor = colorStandard;
                        buttonMiniled.Activated = true;
                        break;
                    // Multizone Strong
                    case 1:
                        buttonMiniled.Text = Properties.Strings.MultizoneStrong;
                        buttonMiniled.BorderColor = colorTurbo;
                        buttonMiniled.Activated = true;
                        break;
                    // Multizone Off
                    case 2:
                        buttonMiniled.Text = Properties.Strings.OneZone;
                        buttonMiniled.BorderColor = colorStandard;
                        buttonMiniled.Activated = false;
                        break;
                }
            }
            else
            {
                buttonMiniled.Visible = false;
            }

            if (hdrControlVisible)
            {
                buttonHDRControl.Visible = true;
                buttonHDRControl.Activated = hdrControl > 0;
                buttonHDRControl.BorderColor = colorTurbo;
            } else
            {
                buttonHDRControl.Visible = false;
            }

            // 这里原来还会把 labelVisual 盖在 tableVisual 上，用来提示
            // 「HDR 开启时视觉模式不可用」。tableVisual 属于已删除的 Splendid 界面，
            // labelVisual 也随之删掉——没有可盖的控件，这段提示无处附着。
        }

        private void ButtonQuit_Click(object? sender, EventArgs e)
        {
            AsusLampArray.Release();
            Close();
            Program.trayIcon.Visible = false;
            Application.Exit();
        }

        /// <summary>
        /// Closes all forms except the settings. Hides the settings
        /// </summary>
        public void HideAll()
        {
            this.Hide();
            if (updatesForm != null && updatesForm.Text != "") updatesForm.Close();
            MemoryHelper.TrimAfter();
        }

        /// <summary>
        /// Brings all visible windows to the top, with settings being the focus
        /// </summary>
        public void ShowAll(Rectangle? workingArea = null)
        {
            bool revealing = !Visible;
            if (revealing)
            {
                bool firstPresentation = !_hasPresentedWindow;
                if (firstPresentation) Opacity = 0.01;
                WindowState = FormWindowState.Normal;
                if (workingArea is not null) ApplyResponsiveBounds(workingArea);
                PerformLayout();
                Show();
                if (firstPresentation)
                {
                    // Only the first native presentation needs the transparent dark-frame guard.
                    // A hidden window already has a complete backing surface; repainting and
                    // toggling opacity on every tray restore creates two visible DWM frames.
                    Update();
                    Opacity = 1;
                    _hasPresentedWindow = true;
                }
            }
            Activate();
        }

        internal void StopRuntimeTimers()
        {
            if (Volatile.Read(ref _runtimeResourcesDisposed) != 0) return;
            // 退出前兜底：息屏期间关应用必须把亮度还回去，否则用户被留在黑屏里。
            ScreenBlankController.RestoreImmediate();
            StopRuntimeTimersCore();
            _brightnessCommitQueue.Dispose();
        }

        private void StopRuntimeTimersCore()
        {
            _liquidCoolingStatusTimer.Stop();
            _displayStatusTimer.Stop();
            _quickSwitchStatusTimer.Stop();
            _officialConsoleStatusTimer.Stop();
            batteryTimer.Stop();
            _sensorTimer.Stop();
        }

        private void DisposeRuntimeResources()
        {
            if (Interlocked.Exchange(ref _runtimeResourcesDisposed, 1) != 0) return;
            ScreenBlankController.RestoreImmediate();
            StopRuntimeTimersCore();
            _brightnessCommitQueue.Dispose();
            _liquidCoolingStatusTimer.Dispose();
            _displayStatusTimer.Dispose();
            _quickSwitchStatusTimer.Dispose();
            _officialConsoleStatusTimer.Dispose();
            batteryTimer.Dispose();
            _sensorTimer.Dispose();
            _liquidCoolingLightMenu?.Dispose();
            contextMenuStrip.Dispose();
        }

        /// <summary>
        /// Check if any of fans, keyboard, update, or itself has focus
        /// </summary>
        /// <returns>Focus state</returns>
        public bool HasAnyFocus(bool lostFocusCheck = false)
        {
            return (updatesForm != null && updatesForm.ContainsFocus) ||
                   this.ContainsFocus ||
                   (lostFocusCheck && Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastLostFocus) < 300);
        }

        private void SettingsForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideAll();
            }
        }

        private async void ButtonUltimate_Click(object? sender, EventArgs e)
        {
            await SwitchGpuModeFromUi(MechrevoLite.Hardware.MechrevoService.GpuDgpu, "独显直连");
        }

        private async void ButtonStandard_Click(object? sender, EventArgs e)
        {
            await SwitchGpuModeFromUi(MechrevoLite.Hardware.MechrevoService.GpuStandard, "标准");
        }

        private async void ButtonEco_Click(object? sender, EventArgs e)
        {
            await SwitchGpuModeFromUi(MechrevoLite.Hardware.MechrevoService.GpuIGpu, "核显");
        }


        private async void ButtonOptimized_Click(object? sender, EventArgs e)
        {
            await SwitchGpuModeFromUi(MechrevoLite.Hardware.MechrevoService.GpuAuto, "自动");
        }

        private async Task SwitchGpuModeFromUi(int targetMode, string modeName)
        {
            if (Program.service is null || Program.hw is not { IsConnected: true })
            {
                MessageBox.Show("GCU 硬件服务尚未连接，请稍后再试。", "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Program.hw is not null && !Program.hw.CanSwitchGpuMode(targetMode))
            {
                Logger.WriteLine($"GPU mode UI request rejected as unsupported: {targetMode}");
                RefreshDeviceCapabilities();
                MessageBox.Show("当前机型或当前 GCU 版本未报告支持此显卡模式。", "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            GpuModeStatusReadback statusReadback = await RefreshGpuModeUiStateAsync();
            if (statusReadback == GpuModeStatusReadback.Unavailable)
            {
                MessageBox.Show("未能读取当前显卡模式。为避免依据旧状态切换，请稍后重试。",
                    "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            GpuSwitchPlan plan = GpuSwitchPolicy.Resolve(
                Program.service.CurrentGpuMode,
                Program.hw.GpuSwitchResult,
                targetMode,
                Program.hw.SupportsGpuHotSwap,
                currentStateFresh: statusReadback == GpuModeStatusReadback.Fresh);
            if (plan.Route == GpuSwitchRoute.NoChange)
            {
                if (HasPendingGpuRestart()) await ShowGpuRestartPromptAsync(targetMode, modeName);
                return;
            }
            // GpuSwitchPolicy 对一切模式变更只返回 Restart：手动切换一律走官方
            // RestartDialog 流程（确认前不发任何命令），热切换路径已整体移除。
            await ShowGpuRestartPromptAsync(targetMode, modeName);
        }

        private async Task<DgpuPreflightResult> PrepareDgpuApplicationsForHotSwitchAsync()
        {
            DgpuApplicationSnapshot snapshot = DgpuApplicationCoordinator.Snapshot();
            Logger.WriteLine($"DGPU preflight: available={snapshot.IsAvailable}, applications=" +
                (snapshot.IsAvailable ? string.Join(",", snapshot.Applications.Select(a => a.ProcessName + "#" + a.ProcessId)) : "n/a"));
            if (!snapshot.IsAvailable)
            {
                return MessageBox.Show(this,
                    "无法读取正在使用独显的程序，不能安全保证热切换。\n\n是否改用重启切换？",
                    "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes
                    ? DgpuPreflightResult.RestartRequested
                    : DgpuPreflightResult.Cancelled;
            }
            if (snapshot.Applications.Count == 0) return DgpuPreflightResult.Continue;

            DialogResult choice = MessageBox.Show(this,
                "检测到以下程序正在使用独显：\n\n" + DescribeDgpuApplications(snapshot.Applications) +
                "\n\n“是”：先请求关闭程序后热切换\n“否”：改用重启切换\n“取消”：保持当前状态",
                "L-Mechrevo", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.Cancel) return DgpuPreflightResult.Cancelled;
            if (choice == DialogResult.No) return DgpuPreflightResult.RestartRequested;

            snapshot = await DgpuApplicationCoordinator.RequestGracefulCloseAsync(snapshot.Applications);
            if (!snapshot.IsAvailable)
            {
                MessageBox.Show("关闭程序后无法重新读取独显占用状态，已取消热切换。", "L-Mechrevo",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return DgpuPreflightResult.Cancelled;
            }
            if (snapshot.Applications.Count == 0) return DgpuPreflightResult.Continue;

            DialogResult forceClose = MessageBox.Show(this,
                "以下程序仍在使用独显：\n\n" + DescribeDgpuApplications(snapshot.Applications) +
                "\n\n是否强制结束这些程序并继续？未保存的工作可能丢失。",
                "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (forceClose != DialogResult.Yes) return DgpuPreflightResult.Cancelled;

            snapshot = await DgpuApplicationCoordinator.ForceCloseAsync(snapshot.Applications);
            Logger.WriteLine("DGPU preflight: after force close, remaining=" +
                (snapshot.IsAvailable ? snapshot.Applications.Count.ToString() : "unknown (enumeration failed)"));
            if (snapshot.IsAvailable && snapshot.Applications.Count == 0) return DgpuPreflightResult.Continue;

            // 普通权限杀不掉的（典型：以管理员身份运行的占卡进程）给一次提权强杀机会，
            // 而不是直接放弃热切换。提权通道内部会重新核对进程身份再动手。
            if (snapshot.IsAvailable && snapshot.Applications.Count > 0)
            {
                DialogResult elevate = MessageBox.Show(this,
                    "以下程序无法以普通权限结束：\n\n" + DescribeDgpuApplications(snapshot.Applications) +
                    "\n\n是否使用管理员权限强制结束？（会弹出 UAC 确认框）\n选择“否”将转用重启切换。",
                    "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (elevate == DialogResult.Yes)
                {
                    (bool clean, string message) = await ElevatedProcessKiller
                        .KillWithElevationAsync(snapshot.Applications).ConfigureAwait(true);
                    Logger.WriteLine($"DGPU preflight: elevated kill clean={clean}, {message}");
                    snapshot = DgpuApplicationCoordinator.Snapshot();
                    if (snapshot.IsAvailable && snapshot.Applications.Count == 0)
                        return DgpuPreflightResult.Continue;
                }
            }

            DialogResult fallback = MessageBox.Show(this,
                "仍有独显程序无法关闭，已取消热切换。\n\n是否改用重启切换？",
                "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return fallback == DialogResult.Yes
                ? DgpuPreflightResult.RestartRequested
                : DgpuPreflightResult.Cancelled;
        }

        private static string DescribeDgpuApplications(IReadOnlyList<DgpuApplication> applications)
        {
            const int maxLines = 8;
            string lines = string.Join(Environment.NewLine, applications.Take(maxLines)
                .Select(application => $"{application.ProcessName} (PID {application.ProcessId})"));
            return applications.Count > maxLines
                ? lines + Environment.NewLine + $"另有 {applications.Count - maxLines} 个程序"
                : lines;
        }

        private async Task<GpuModeStatusReadback> RefreshGpuModeUiStateAsync()
        {
            if (Program.service is null) return GpuModeStatusReadback.Unavailable;

            GpuModeStatusReadback readback = await Program.service.RefreshGpuModeStatus();
            if (readback == GpuModeStatusReadback.Unavailable || IsDisposed)
                return GpuModeStatusReadback.Unavailable;

            VisualiseGPUMode(Program.service.CurrentGpuMode);
            SetContextMenu();
            return readback;
        }

        private static void MarkGpuRestartPending()
        {
            AppConfig.Set("gpu_restart_pending", 1);
            AppConfig.Set("gpu_restart_tick", Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static bool HasPendingGpuRestart()
        {
            if (!AppConfig.Is("gpu_restart_pending")) return false;
            if (long.TryParse(AppConfig.GetString("gpu_restart_tick"), out long markedTick) &&
                Environment.TickCount64 + 10_000 < markedTick)
            {
                // TickCount64 only moves backwards after a real Windows restart.
                AppConfig.Set("gpu_restart_pending", 0);
                AppConfig.Remove("gpu_restart_tick");
                return false;
            }
            return true;
        }

        private static void ClearGpuRestartPending()
        {
            AppConfig.Set("gpu_restart_pending", 0);
            AppConfig.Remove("gpu_restart_tick");
        }

        private async Task<bool> ShowGpuRestartPromptAsync(int targetMode, string modeName)
        {
            DialogResult result = MessageBox.Show(
                $"切换到{modeName}模式必须重启电脑才能生效。\n\n「是」= 发送切换指令并立即重启；「否」= 取消，不做任何更改。",
                "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                await RefreshGpuModeUiStateAsync();
                return false;
            }

            if (Program.service is null)
            {
                await RefreshGpuModeUiStateAsync();
                return false;
            }

            int previousMode = AppConfig.Get("gpu_mode");
            bool previousAuto = AppConfig.Is("gpu_auto");
            bool hadPendingRestart = HasPendingGpuRestart();
            string previousRestartTick = AppConfig.GetString("gpu_restart_tick") ?? "";
            AppConfig.Set("gpu_mode", targetMode);
            AppConfig.Set("gpu_auto", 0);
            MarkGpuRestartPending();
            AppConfig.Flush();
            GpuRestartRequestOutcome outcome = await Program.service.RequestGpuModeRestartOutcomeAsync(targetMode);
            if (outcome != GpuRestartRequestOutcome.Requested)
            {
                AppConfig.Set("gpu_mode", previousMode);
                AppConfig.Set("gpu_auto", previousAuto ? 1 : 0);
                if (hadPendingRestart)
                {
                    AppConfig.Set("gpu_restart_pending", 1);
                    AppConfig.Set("gpu_restart_tick", previousRestartTick);
                }
                else
                {
                    ClearGpuRestartPending();
                }
                AppConfig.Flush();
                await RefreshGpuModeUiStateAsync();
                // 「该方向没有可用指令」与一般发送失败是两回事：前者是机型/固件不支持，
                // 说清楚用户才不会反复点同一次必然白重启的切换。
                MessageBox.Show(
                    outcome == GpuRestartRequestOutcome.Unsupported
                        ? $"当前机型或当前 GCU 版本不支持「{modeName}」方向的显卡切换：没有可用的切换指令，已取消且未重启。"
                        : "GCU 未能发送重启切换请求，显卡模式未被标记为已切换。",
                    "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            VisualiseGPUMode(targetMode);
            SetContextMenu();
            _ = WatchGcuRestartFallbackAsync();
            return true;
        }

        // GCU 收到 DGPU_DIRECT_CONNECT_RESTART 后应自行重启系统；若 15 秒后本进程
        // 仍在运行，说明这次重启没有发生——寄存器已写入但不会生效，退回 Windows 重启兜底。
        private async Task WatchGcuRestartFallbackAsync()
        {
            await Task.Delay(15000);
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    DialogResult reboot = MessageBox.Show(this,
                        "GCU 未自动重启系统。显卡模式指令已写入，重启后才会生效。\n\n点击「确定」5 秒后重启 Windows 完成切换。",
                        "L-Mechrevo", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (reboot == DialogResult.OK)
                    {
                        // 用户刚点过 [确定]，立刻捕获重启凭证，后续日志/调度耗时不会让它过期。
                        SystemRestart.CaptureUserConfirmation();
                        Logger.WriteLine("GCU restart did not happen; falling back to Windows shutdown /r.");
                        // 不可逆动作走统一入口：真实输入或确认凭证 + 后台线程；程序化路径一律拒绝。
                        SystemRestart.RequestRestart("GCU restart fallback", SystemRestart.RebootAfterFiveSecondsArguments);
                    }
                });
            }
            catch (ObjectDisposedException) { }
        }

        private void ButtonStopGPU_Click(object? sender, EventArgs e)
        {
            gpuControl.KillGPUApps();
        }

        public void RefreshSensors(bool force = false)
        {
            int throttle = 2000;
            if (!force && Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastRefresh) < throttle) return;
            lastRefresh = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            string cpuTemp = "";
            string gpuTemp = "";

            string cpuFan = "";
            string gpuFan = "";
            string midFan = "";

            string battery = "";
            string charge = "";

            HardwareControl.ReadSensors();

            if (HardwareControl.cpuTemp > 0)
                cpuTemp = ": " + TempHelper.FormatTemp((double)HardwareControl.cpuTemp);

            if (HardwareControl.batteryCapacity > 0)
            {
                charge = Properties.Strings.BatteryCharge + ": " + HardwareControl.batteryCharge;
            }

            battery = BatteryRateText(HardwareControl.batteryRate);


            if (HardwareControl.gpuTemp > 0)
            {
                gpuTemp = ": " + TempHelper.FormatTemp((double)HardwareControl.gpuTemp);
            }

            if (HardwareControl.cpuFan is not null) cpuFan = Strings.FanSpeed + ": " + HardwareControl.cpuFan + "% (" + HardwareControl.cpuFanRPM + " rpm)";
            if (HardwareControl.gpuFan is not null) gpuFan = Strings.FanSpeed + ": " + HardwareControl.gpuFan + "% (" + HardwareControl.gpuFanRPM + " rpm)";
            if (HardwareControl.midFan is not null) midFan = Strings.FanSpeed + ": " + HardwareControl.midFan;

            string trayTip = "CPU" + cpuTemp + " " + cpuFan;
            if (gpuTemp.Length > 0) trayTip += "\nGPU" + gpuTemp + " " + gpuFan;
            if (battery.Length > 0) trayTip += "\n" + battery;
            
            if (!IsHandleCreated || IsDisposed) return;
            void ApplySnapshot()
            {
                // 行头状态（v2 预览）：性能行右列 = 当前模式名（预览「自定义」位置），显卡行右列 =
                // 「重启生效」常驻提示（本机 MUX 切换重启后才生效）。旧实现把遥测摘要塞进这两处，
                // 与下方遥测行完全重复（真机验收暴露）。
                UpdatePerfRowStatus();
                if (labelGPUFan.Text != "重启生效") labelGPUFan.Text = "重启生效";
                UpdateTelemetryText();
                UpdateGcuStatus();   // 复用既有传感器节拍（≤2s），不新增轮询机制

                if (HardwareControl.gpuFan is not null && AppConfig.NoGpu())
                {
                    string midGpuText = "GPU" + gpuTemp + " " + gpuFan;
                    if (labelMidFan.Text != midGpuText) labelMidFan.Text = midGpuText;
                }
                if (HardwareControl.midFan is not null)
                {
                    string midText = "Mid " + midFan;
                    if (labelMidFan.Text != midText) labelMidFan.Text = midText;
                }

                // 充电状态为空时，右侧退化显示电池健康摘要（循环/容量）——预览电池行右列形态。
                if (labelBattery.Text != battery)
                    labelBattery.Text = battery.Length > 0 ? battery : BatteryHealthText(Program.hw);
                if (!batteryMouseOver && !batteryFullMouseOver && labelCharge.Text != charge) labelCharge.Text = charge;
                if (Program.trayIcon is not null && Program.trayIcon.Text != trayTip) Program.trayIcon.Text = trayTip;
            }

            if (InvokeRequired) BeginInvoke(ApplySnapshot);
            else ApplySnapshot();
        }


        public void ToggleOverlay(bool fromHotkey = false)
        {
            bool enable = !AppConfig.IsOverlay();
            AppConfig.Set("overlay", enable ? 1 : 0);
            Logger.WriteLine("Overlay " + (enable ? "On" : "Off") + (AppConfig.IsOverlayGameOnly() ? " (game only)" : ""));
            if (enable)
                Program.hardwareOverlay?.StartOverlay();
            else
                Program.hardwareOverlay?.StopOverlay();

            buttonOverlay.Activated = enable;
            UpdateFooterOverlayVisual(enable);

            if (fromHotkey && AppConfig.IsOverlayGameOnly())
                Program.toast.RunToast(Properties.Strings.Overlay + " " + (enable ? Properties.Strings.On : Properties.Strings.Off));

            SetContextMenu();
        }

        public void ToggleOverlayGameOnly()
        {
            AppConfig.Set("overlay_game_only", AppConfig.IsOverlayGameOnly() ? 0 : 1);
            if (AppConfig.IsOverlay())
            {
                Program.hardwareOverlay?.StopOverlay();
                Program.hardwareOverlay?.StartOverlay();
            }
            SetContextMenu();
        }

        public void ShowMode(int mode)
        {
            if (InvokeRequired)
                BeginInvoke(delegate
                {
                    VisualiseMode(mode);
                });
            else
                VisualiseMode(mode);
        }

        protected void VisualiseMode(int mode)
        {
            bool silentTurbo = mode == AsusACPI.PerformanceTurbo && MechrevoLite.Hardware.MechrevoService.IsSilentTurboActive;
            if (!ShouldRefreshVisualMode(_lastVisualMode, _lastVisualSilentTurbo, mode, silentTurbo)) return;
            _lastVisualMode = mode;
            _lastVisualSilentTurbo = silentTurbo;
            Logger.WriteLine($"VisualiseMode({mode})");   // 定位主界面模式显示来源
            buttonSilent.Activated = false;
            buttonBalanced.Activated = false;
            buttonTurbo.Activated = false;
            if (buttonSilentTurbo is not null) buttonSilentTurbo.Activated = false;
            if (buttonCustomMode is not null) buttonCustomMode.Activated = false;

            if (IsCustomVisualMode(mode))
            {
                if (buttonCustomMode is not null) buttonCustomMode.Activated = true;
                UpdatePerfRowStatus();
                return;
            }

            switch (mode)
            {
                case AsusACPI.PerformanceSilent:
                    buttonSilent.Activated = true;
                    break;
                case AsusACPI.PerformanceTurbo:
                    // Turbo 子模式：静音狂暴（注册表 0）与狂暴（1）区分高亮
                    if (buttonSilentTurbo is not null && silentTurbo)
                        buttonSilentTurbo.Activated = true;
                    else
                        buttonTurbo.Activated = true;
                    break;
                case AsusACPI.PerformanceBalanced:
                    buttonBalanced.Activated = true;
                    break;
            }
            UpdatePerfRowStatus();
        }

        /// <summary>性能行头右列 = 当前模式名（预览「自定义」位置）——跟随点亮的分段按钮。</summary>
        void UpdatePerfRowStatus()
        {
            string modeText = CurrentModeDisplayText();
            if (labelCPUFan.Text != modeText) labelCPUFan.Text = modeText;
        }

        string CurrentModeDisplayText()
        {
            if (buttonCustomMode?.Activated == true) return buttonCustomMode.Text;
            if (buttonSilentTurbo?.Activated == true) return buttonSilentTurbo.Text;
            if (buttonTurbo.Activated) return buttonTurbo.Text;
            if (buttonBalanced.Activated) return buttonBalanced.Text;
            if (buttonSilent.Activated) return buttonSilent.Text;
            return "";
        }

        internal static bool IsCustomVisualMode(int mode) => mode == AsusACPI.PerformanceManual;

        internal static bool ShouldRefreshVisualMode(int currentMode, bool currentSilentTurbo, int nextMode, bool nextSilentTurbo) =>
            currentMode != nextMode ||
            (nextMode == AsusACPI.PerformanceTurbo && currentSilentTurbo != nextSilentTurbo);


        public void SetModeLabel(string modeText)
        {
            // v2：行标题固定为「性能模式」，当前档位由分段按钮的选中态表达；
            // modeText 只进无障碍名（旧版把档位拼进标题，420 窄行溢出）。
            if (InvokeRequired)
            {
                BeginInvoke(delegate
                {
                    labelPerf.Text = Properties.Strings.PerformanceMode;
                    panelPerformance.AccessibleName = modeText;
                });
            }
            else
            {
                labelPerf.Text = Properties.Strings.PerformanceMode;
                panelPerformance.AccessibleName = modeText;
            }

        }



        // VisualizeXGM 的两个重载已删除：首句就是 `Program.acpi.IsXGConnected()`，
        // 而那个方法恒 false（机械革命全系没有 XG Mobile 外置显卡坞接口），
        // 于是整个方法体等价于 `buttonXGM.Visible = false; return;`。
        // buttonXGM 本身也已从设计器里删掉。

        public void VisualiseGPUButtons(bool eco = true, bool ultimate = true, bool auto = true)
        {
            if (InvokeRequired) { Invoke(() => VisualiseGPUButtons(eco, ultimate, auto)); return; }
            isMuxGpu = ultimate;
            isGpuSection = eco || ultimate;
            buttonEco.Visible = eco;
            buttonOptimized.Visible = false; // 已移除自动模式，始终隐藏
            buttonStandard.Visible = eco || ultimate;
            buttonUltimate.Visible = ultimate;
            if (menuEco is not null) menuEco.Visible = eco;
            if (menuOptimized is not null) menuOptimized.Visible = false; // 已移除自动模式
            if (menuStandard is not null) menuStandard.Visible = eco || ultimate;
            if (menuUltimate is not null) menuUltimate.Visible = ultimate;
            buttonStopGPU.Visible = false;

            var buttons = new List<Control>();
            if (eco) buttons.Add(buttonEco);
            if (eco || ultimate) buttons.Add(buttonStandard);
            // if (auto) buttons.Add(buttonOptimized); // 已移除自动模式
            if (ultimate) buttons.Add(buttonUltimate);
            // 同 ReflowPerformanceButtons：这个方法也被周期性调用，不加守卫就是每三秒闪一次。
            if (ReflowSingleRow(tableGPU, buttons, ref _lastGpuLayout))
                ApplySegmentRow(buttons);
        }

        public void HideGPUModes(bool gpuExists)
        {
            isGpuSection = false;

            buttonEco.Visible = false;
            buttonStandard.Visible = false;
            buttonUltimate.Visible = false;
            buttonOptimized.Visible = false;
            buttonStopGPU.Visible = true;

            tableGPU.ColumnCount = 0;

            SetContextMenu();

            panelGPU.Visible = gpuExists;

        }


        public void LockGPUModes(string text = null)
        {
            if (InvokeRequired) { Invoke(() => LockGPUModes(text)); return; }
            if (text is null) text = Properties.Strings.GPUMode + ": " + Properties.Strings.GPUChanging + " ...";

            ButtonEnabled(buttonOptimized, false);
            ButtonEnabled(buttonEco, false);
            ButtonEnabled(buttonStandard, false);
            ButtonEnabled(buttonUltimate, false);

            // v2：标题固定；切换中的状态说明放进 tip 行（labelTipGPU 随内容显隐）。
            labelGPU.Text = Properties.Strings.GPUMode;
            labelTipGPU.Text = Properties.Strings.GPUChanging + " ...";
            labelTipGPU.Visible = true;
        }

        public void VisualiseGPUMode(int GPUMode = -1)
        {
            if (InvokeRequired) { Invoke(() => VisualiseGPUMode(GPUMode)); return; }

            if (toolTip.GetToolTip(pictureGPU) != (GPUModeControl.gpuError ?? ""))
            {
                pictureGPU.BackgroundImage = GPUModeControl.gpuError is null ? Properties.Resources.icons8_video_card_32 : SystemIcons.Warning.ToBitmap();
                pictureGPU.Cursor = GPUModeControl.gpuError is null ? Cursors.Default : Cursors.Hand;
                toolTip.SetToolTip(pictureGPU, GPUModeControl.gpuError);
            }

            // 这里曾经有一段 ROG Ally 掌机的特殊分支（隐藏整个 GPU 表格、把 XGM 按钮
            // 挪到 tableAMD 里）。它的门禁是 `!IsMechrevo && AppConfig.IsAlly()`——
            // 前半在目标硬件上恒 false、后半靠 SMBIOS 机型串含 "RC7" 匹配，双重恒假。
            ButtonEnabled(buttonOptimized, true);
            ButtonEnabled(buttonEco, true);
            ButtonEnabled(buttonStandard, true);
            ButtonEnabled(buttonUltimate, true);

            if (GPUMode == -1)
            {
                GPUMode = AppConfig.Get("gpu_mode");
                // 已移除自动模式：如果配置中是自动模式(3)，需要根据实际硬件状态映射
                if (GPUMode == MechrevoLite.Hardware.MechrevoService.GpuAuto)
                {
                    // 从GCU读取实际模式
                    GPUMode = Program.service?.CurrentGpuMode ?? MechrevoLite.Hardware.MechrevoService.GpuStandard;
                }
            }

            buttonEco.Activated = false;
            buttonStandard.Activated = false;
            buttonUltimate.Activated = false;
            // buttonOptimized.Activated = false; // 已移除自动模式

            switch (GPUMode)
            {
                case MechrevoLite.Hardware.MechrevoService.GpuIGpu:
                    // buttonOptimized.BorderColor = colorEco; // 已移除自动模式
                    buttonEco.Activated = true;
                    labelGPU.Text = Properties.Strings.GPUMode;
                    panelGPU.AccessibleName = Properties.Strings.GPUMode + " - " + Properties.Strings.EcoMode;
                    break;
                case MechrevoLite.Hardware.MechrevoService.GpuDgpu:
                    buttonUltimate.Activated = true;
                    labelGPU.Text = Properties.Strings.GPUMode;
                    panelGPU.AccessibleName = Properties.Strings.GPUMode + " - " + Properties.Strings.UltimateMode;
                    break;
                // case MechrevoLite.Hardware.MechrevoService.GpuAuto: // 已移除自动模式
                //     buttonOptimized.BorderColor = colorStandard;
                //     buttonOptimized.Activated = true;
                //     labelGPU.Text = Properties.Strings.GPUMode + ": " + Properties.Strings.Optimized;
                //     panelGPU.AccessibleName = Properties.Strings.GPUMode + " - " + Properties.Strings.Optimized;
                //     break;
                default: // 标准/混合
                    // buttonOptimized.BorderColor = colorStandard; // 已移除自动模式
                    buttonStandard.Activated = true;
                    labelGPU.Text = Properties.Strings.GPUMode;
                    panelGPU.AccessibleName = Properties.Strings.GPUMode + " - " + Properties.Strings.StandardMode;
                    break;
            }

            VisualiseIcon();
            labelTipGPU.Text = HasPendingGpuRestart() ? "显卡模式已更改，重启后生效。" : "";
            labelTipGPU.Visible = labelTipGPU.Text.Length > 0;

            if (isGpuSection)
            {
                menuEco.Checked = buttonEco.Activated;
                menuStandard.Checked = buttonStandard.Activated;
                menuUltimate.Checked = buttonUltimate.Activated;
                // menuOptimized.Checked = buttonOptimized.Activated; // 已移除自动模式
            }

            // UI Fix for small screeens
            if (Top < 0)
            {
                labelTipGPU.Visible = false;
                labelTipScreen.Visible = false;
                Top = 5;
            }

        }


        private bool isDark = CheckSystemDarkModeStatus();

        public void VisualiseIcon(bool themeChange = false)
        {
            if (Program.trayIcon is null) return;
            // Mechrevo：托盘图标固定 L 图标（不再随 GPU 模式/主题变化）
            if (_lIconSet) return;
            _lIconSet = true;
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "favicon.ico");
                if (File.Exists(iconPath))
                {
                    Icon? oldIcon = Program.trayIcon.Icon;
                    Program.trayIcon.Icon = new Icon(iconPath);
                    oldIcon?.Dispose();
                }
            }
            catch (Exception ex) { Logger.WriteLine("VisualiseIcon fail: " + ex.Message); }
        }
        bool _lIconSet;

        private void PictureGPU_Click(object? sender, EventArgs e)
        {
            if (GPUModeControl.gpuError is not null)
                Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }

        private async void ButtonSilent_Click(object? sender, EventArgs e)
        {
            await SwitchPerformanceModeAsync(MechrevoService.ModeOffice);
        }

        private async void ButtonBalanced_Click(object? sender, EventArgs e)
        {
            await SwitchPerformanceModeAsync(MechrevoService.ModeGaming);
        }

        private async void ButtonTurbo_Click(object? sender, EventArgs e)
        {
            await SwitchPerformanceModeAsync(MechrevoService.ModeTurbo, silentTurbo: false);
        }

        private async Task SwitchPerformanceModeAsync(int mode, bool? silentTurbo = null)
        {
            MechrevoService? mechrevoService = Program.service;
            if (mechrevoService is null) return;
            if (mode == MechrevoService.ModeTurbo && Program.hw?.Capabilities.TurboMode != true)
            {
                Logger.WriteLine("Turbo mode UI request rejected as unsupported by this device profile.");
                RefreshDeviceCapabilities();
                return;
            }

            int request = Interlocked.Increment(ref _performanceRequest);
            ShowMode(MechrevoService.ToVisualMode(mode)); // Immediate visual acknowledgement while hardware confirms in the background.
            if (mode == MechrevoService.ModeTurbo && buttonSilentTurbo is not null)
            {
                buttonSilentTurbo.Activated = silentTurbo == true;
                buttonTurbo.Activated = silentTurbo != true;
            }
            SetContextMenu();

            bool confirmed = await mechrevoService.SwitchMode(mode);
            if (request != Volatile.Read(ref _performanceRequest)) return;

            bool turboSubModeSupported = Program.hw?.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported;
            if (confirmed && silentTurbo == true && !turboSubModeSupported)
                confirmed = false;
            else if (confirmed && silentTurbo.HasValue && turboSubModeSupported)
                confirmed = await mechrevoService.SwitchTurboSubMode(silentTurbo.Value);
            if (request != Volatile.Read(ref _performanceRequest)) return;

            ShowMode(MechrevoService.ToVisualMode(confirmed ? mode : mechrevoService.CurrentMode));
            SetContextMenu();
        }


        public void ButtonEnabled(RButton but, bool enabled)
        {
            but.Enabled = enabled;
            but.BackColor = but.Enabled ? Color.FromArgb(255, but.BackColor) : Color.FromArgb(100, but.BackColor);
        }

        void InitQuickSwitchRefresh()
        {
            BuildQuickSwitchPanel();
            VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());   // 百分比读数首次回显（EC 实际阈值优先）
            // 面板布局调整（依赖 QuickSwitch 面板已创建）：
            // 1. 删蓝色 100% 满充按钮（上限由滑条设定，这个按钮已无意义）
            buttonBatteryFull.Visible = false;
            // 2. 显卡模式面板置于快捷开关上方
            // 面板顺序（Dock=Top：SetChildIndex 大值=显示上方）：
            // 性能(9) → 液冷(8) → 显卡(7) → 快捷(6) → 电池(5) → 刷新率(4) → 亮度(3) → 启动/版本/底部(2/1/0)
            var quickPanel = Controls["panelQuickSwitch"];
            if (quickPanel is not null)
            {
                Controls.SetChildIndex(panelPerformance, 9);
                Controls.SetChildIndex(Controls["panelLc"], 8);
                Controls.SetChildIndex(panelGPU, 7);
                Controls.SetChildIndex(quickPanel, 6);
                Controls.SetChildIndex(panelBattery, 5);
                Controls.SetChildIndex(Controls["panelBrightness"], 3);
            }
            // 3. 开机自启勾选框移到版本面板右侧：labelVersion Dock=Left、勾选框 Dock=Right，
            // 两个 AutoSize 从两端向中间排——420 窄宽下不再重叠（旧 560 宽的绝对坐标在 420
            // 下会撞 7px）。P2 版本卡拆进 footer+设置弹窗后这里随迁消失。
            panelStartup.Controls.Remove(checkStartup);
            labelVersion.Dock = DockStyle.Left;
            // 420 窄宽下 labelVersion 与开机自启勾选框两端相加正好撞 7px：版本号显式
            // 定宽 140 逻辑 + Caption 字体 + 省略号；它是页脚辅助信息，不是正文。
            labelVersion.AutoSize = false;
            labelVersion.Size = new Size(140, labelVersion.Height);
            labelVersion.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
            labelVersion.AutoEllipsis = true;
            checkStartup.Dock = DockStyle.Right;
            panelVersion.Controls.Add(checkStartup);
            panelStartup.Visible = false;
            _quickSwitchStatusTimer.Tick += (_, _) =>
            {
                // 任务栏/透明/深色三项来自 Windows，未连 GCU 时也要跟随外部改动，
                // 所以回显放在连接检查之前。
                UpdateQuickSwitches();
                if (Program.hw is not { IsConnected: true }) return;
                // 电池健康后缀要等 System/BatteryInfo 到达之后才有内容，所以跟着这个定时器刷
                // （顺带回显上限百分比）。读 EC 实际阈值而不是配置：配置只是缓存，刷新不得覆盖真值。
                VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
            };
            if (!Program.UiAuditMode) _quickSwitchStatusTimer.Start();
        }

        /// <summary>
        /// 充电上限读数未知（从未成功写入 / 写失败 / 机型不支持）时的占位。绝不能把
        /// AppConfig 缺失键的 -1 哨兵当成一个真实的「-1%」上限显示出去。
        /// </summary>
        internal const string BatteryLimitUnknownText = "—";

        public void VisualiseBatteryTitle(int limit)
        {
            // v2（预览 1:1）：标题就是「电池」；循环次数/容量这类健康摘要在右侧 muted 状态里，
            // 充电状态为空时才顶上（避免每秒覆盖掉健康信息）。
            labelBatteryTitle.Text = "电池";
            if (_batteryLimitValue is null) return;
            if (!EcChargeLimit.IsSupportedLimit(limit))
            {
                _batteryLimitValue.Text = BatteryLimitUnknownText;
                return;
            }
            _batteryLimitValue.Text = limit.ToString() + "%";
        }

        /// <summary>
        /// 电池健康后缀。全部未知时返回空串——不能拿 -1 之类的占位值去显示。
        /// </summary>
        internal static string BatteryHealthSuffix(MechrevoLite.Hardware.MechrevoHw? hw)
        {
            if (hw is null) return "";
            var parts = new List<string>(3);
            if (hw.BatteryCycleCount >= 0) parts.Add($"循环 {hw.BatteryCycleCount} 次");
            if (HasMeaningfulCapacity(hw.BatteryCapacityText)) parts.Add(hw.BatteryCapacityText);
            if (hw.BatteryAbnormal) parts.Add("电池异常");
            return parts.Count == 0 ? "" : "（" + string.Join(" · ", parts) + "）";
        }

        /// <summary>
        /// 设计容量是否值得显示。开发机实测服务端报的是 "0 mWh"——字段非空但数值为零，
        /// 直接展示等于告诉用户「这块电池容量是 0」。只有解析出真正的正数才显示。
        /// </summary>
        internal static bool HasMeaningfulCapacity(string? capacityText)
        {
            if (string.IsNullOrWhiteSpace(capacityText)) return false;
            var digits = new string(capacityText.TakeWhile(c => char.IsDigit(c) || char.IsWhiteSpace(c))
                .Where(char.IsDigit).ToArray());
            return digits.Length > 0 && long.TryParse(digits, out long value) && value > 0;
        }

        /// <summary>行右列摘要（不带括号；循环次数/设计容量）。未知全空时返回空串。</summary>
        internal static string BatteryHealthText(MechrevoLite.Hardware.MechrevoHw? hw)
        {
            if (hw is null) return "";
            var parts = new List<string>(2);
            if (hw.BatteryCycleCount >= 0) parts.Add($"循环 {hw.BatteryCycleCount} 次");
            if (HasMeaningfulCapacity(hw.BatteryCapacityText)) parts.Add(hw.BatteryCapacityText);
            return string.Join(" · ", parts);
        }

        /// <summary>充/放瓦数文本：未知（null 或 0）返回空串，不编数；放电为正数加「放电」前缀。</summary>
        internal static string BatteryRateText(decimal? rate)
        {
            if (rate is < 0)
                return Properties.Strings.Discharging + ": " + Math.Round(-rate.Value, 1).ToString() + "W";
            if (rate is > 0)
                return Properties.Strings.Charging + ": " + Math.Round(rate.Value, 1).ToString() + "W";
            return "";
        }

        public void VisualiseBattery(int limit)
        {
            if (InvokeRequired) { Invoke(() => VisualiseBattery(limit)); return; }
            if (!EcChargeLimit.IsSupportedLimit(limit))
            {
                // 未知值绝不静默钳成滑条下限（-1 → 40）冒充一个上限：如实显示未知。
                VisualiseBatteryUnknown();
                return;
            }
            VisualiseBatteryTitle(limit);
            sliderBattery.Value = Math.Clamp(limit, sliderBattery.Minimum, sliderBattery.Maximum);

            sliderBattery.AccessibleName = Properties.Strings.BatteryChargeLimit + ": " + limit.ToString() + "%";
            //sliderBattery.AccessibilityObject.Select(AccessibleSelection.TakeFocus);

            VisualiseBatteryFull();
        }

        /// <summary>上限未知时的诚实回显：读数与无障碍名显示未知，且不移动滑条伪装成一个值。</summary>
        public void VisualiseBatteryUnknown()
        {
            if (InvokeRequired) { Invoke(VisualiseBatteryUnknown); return; }
            if (_batteryLimitValue is not null) _batteryLimitValue.Text = BatteryLimitUnknownText;
            sliderBattery.AccessibleName = Properties.Strings.BatteryChargeLimit + ": " + BatteryLimitUnknownText;
            VisualiseBatteryFull();
        }

        public void VisualiseBatteryFull()
        {
            if (InvokeRequired) { Invoke(VisualiseBatteryFull); return; }
            if (BatteryControl.chargeFull)
            {
                buttonBatteryFull.BackColor = colorStandard;
                buttonBatteryFull.ForeColor = SystemColors.ControlLightLight;
                buttonBatteryFull.AccessibleName = Properties.Strings.BatteryChargeLimit + "100% on";
            }
            else
            {
                buttonBatteryFull.BackColor = buttonSecond;
                buttonBatteryFull.ForeColor = SystemColors.ControlDark;
                buttonBatteryFull.AccessibleName = Properties.Strings.BatteryChargeLimit + "100% off";
            }

        }


        public void UpdateKeyboardLabel()
        {
            labelKeyboard.Text = Properties.Strings.LaptopKeyboard;
        }

        public void VisualiseFnLock()
        {
            buttonFnLock.BackColor = buttonSecond;
            buttonFnLock.ForeColor = SystemColors.ControlDark;
            buttonFnLock.AccessibleName = "Fn-Lock off";
        }

    }


}
