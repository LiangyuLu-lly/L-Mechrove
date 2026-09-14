using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>
/// 自定义性能模式（原版 CustomModeSettingPage 协议移植）：
/// 4 个自定义档（OPERATING_CUSTOM_MODE + ProfileIndex），参数经 SET_OPERATING_MODE_DETAIL 保存到当前档。
/// TCC 语义与官方一致：UI 显示目标温度；Intel 下发 TjMax - 目标温度，AMD 下发 CpuAmdTccTarget。
/// </summary>
public class CustomModeForm : RForm
{
    /// <summary>参数行高（逻辑 px）：容纳字体派生高的下拉（96dpi 23px）+ 上下各 ≥1px 呼吸。</summary>
    internal const int RowLogicalHeight = 26;

    // 档位切换状态文案：pending 是中性进行时，只有宽限期后仍未确认才允许失败措辞。
    internal const string SwitchPendingText = "切换中…";
    internal const string SwitchUnconfirmedText = "切换未确认";
    internal const int SwitchGraceAttempts = 4;
    internal const int SwitchGraceDelayMs = 500;

    /// <summary>
    /// 服务确认超时 ≠ 切换失败：GCU 的档位回读可能晚于确认窗口到达。
    /// 只有「服务未确认且硬件仍报别的档」才允许显示失败措辞。
    /// </summary>
    internal static bool SwitchShouldReportFailure(bool serviceConfirmed, int hardwareProfile, int requestedProfile)
        => !serviceConfirmed && hardwareProfile != requestedProfile;

    readonly Dictionary<string, string> _pending = new();   // 防抖合并的待发送参数
    readonly System.Windows.Forms.Timer _debounce;
    readonly SemaphoreSlim _saveLock = new(1, 1);
    readonly List<string> _rowLabels = new();
    int _minContentWidth;
    bool _syncing;
    bool _refreshingHardwareState;
    Panel _scrollHost = null!;
    int _laidOutContentHeight;

    int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);

    RSlider _pl1 = null!, _pl2 = null!, _pl4 = null!, _tcc = null!, _tgp = null!, _db = null!, _coreOc = null!, _memOc = null!;
    RNumericUpDown _pl1Val = null!, _pl2Val = null!, _pl4Val = null!, _tccVal = null!, _tgpVal = null!, _dbVal = null!, _coreOcVal = null!, _memOcVal = null!;
    RSlider _fanSwitchSpeed = null!;
    RNumericUpDown _fanSwitchSpeedVal = null!;
    RCheckBox _dbChk = null!, _tccChk = null!, _ocChk = null!, _fanSwitchSpeedChk = null!;
    // GPU 超频数值行的可调性快照：来自同一次硬件回读，供 SyncGpuOverclockDependents 推导启用状态。
    bool _gpuOcCoreAdjustable;
    bool _gpuOcMemoryAdjustable;
    // 用户本次打开的超频开关意图。零偏移的开启没法被驱动回读确认
    // （IsGpuOverclockEnableConfirmed 要求至少一个非零偏移），若直接用硬件回读覆盖开关，
    // 下一次状态帧就会把它弹回、数值行随之禁用，用户来不及拨值——超频于是永远不可用。
    // arm 保持到用户手动关闭，或窗口隐藏/切换档位时重置。
    bool _gpuOcArmed;
    RComboBox _planCombo = null!, _boostCombo = null!;   // Windows 电源计划 / 睿频（按档独立）
    bool _syncingWinPower;
    int _currentIdx = -1;   // 当前选中的自定义档（Windows 电源设置按档保存）
    Label _status = null!;
    Label _powerWallStatus = null!;
    // 功耗墙判定需要持续采样；这个窗口开着的时候就是用户在关心功耗墙的时候。
    readonly System.Windows.Forms.Timer _powerWallTimer = new() { Interval = 2000 };
    RButton[] _profileBtns = null!;

    public CustomModeForm()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = "自定义性能模式";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // 宽度在构建完成后按内容实测收紧（ComputeContentWidth）；高度由 ResizeToContent 收紧。
        ClientSize = new Size(D(820), D(200));
        InitTheme(true);
        WinPowerPlan.EnsureProfileSettings();

        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiVisualStyle.Window,
        };
        _scrollHost = scrollHost;
        Controls.Add(scrollHost);

        // 根布局只负责真实内容高度，滚动范围由外层 Panel 管理。
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(D(12), D(8), D(12), D(8)),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scrollHost.Controls.Add(root);
        int rootRow = 0;

        var titleFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = UiVisualStyle.Window, Margin = new Padding(0, 0, 0, D(6)) };
        _status = new Label { Text = "选择一个自定义档", ForeColor = UiVisualStyle.Muted, AutoSize = true, Margin = Padding.Empty };
        titleFlow.Controls.Add(_status);
        // 功耗墙实测结论。单独一个标签而不是复用 _status：后者要显示各种一次性提示，
        // 混在一起会互相覆盖。判不出来时文本为空，不占地方。
        _powerWallStatus = new Label
        {
            Name = "labelPowerWallVerdict",
            Text = "",
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Margin = new Padding(D(8), 0, 0, 0),
            // 没有结论时整个标签隐藏，而不是留一个空文本的可见控件——
            // AutoSize 标签空文本时宽度是 0，那既是布局缺陷也会被 UI 审计判为 zero-size。
            Visible = false,
        };
        titleFlow.Controls.Add(_powerWallStatus);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(titleFlow, 0, rootRow++);

        // ---- 4 个档位按钮（分段组：RButton 分段皮肤，选中态 = Accent 内嵌块）----
        // 按内容取宽（AutoSize），不再强制窗口宽度。
        var btnFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiVisualStyle.Window,
            Margin = new Padding(0, 0, 0, D(6)),
        };
        _profileBtns = new RButton[4];
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var b = new RButton
            {
                Text = "自定义 " + (i + 1),
                AutoSize = true,
                Margin = new Padding(i == 0 ? 0 : D(2), 0, i == 3 ? 0 : D(2), 0),
                Cursor = Cursors.Hand,
                Tag = i,
            };
            b.Click += async (_, _) =>
            {
                await ActivateProfileAsync(idx);
            };
            btnFlow.Controls.Add(b);
            _profileBtns[i] = b;
        }
        UiVisualStyle.ApplySegmentGroup(_profileBtns);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(btnFlow, 0, rootRow++);

        // ---- 参数区（紧凑行节奏：D(RowLogicalHeight) 固定行 + Padding.Empty 边距）----
        var table = new TableLayoutPanel
        {
            Name = "paramTable",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = UiVisualStyle.Surface,
            Padding = new Padding(D(10), D(8), D(10), D(8)),
            Margin = new Padding(0, 0, 0, D(6)),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int tableRow = 0;
        var paramRows = new List<TableLayoutPanel>();

        TableLayoutPanel MakeRow()
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                Height = D(RowLogicalHeight),
                Margin = Padding.Empty,
                BackColor = UiVisualStyle.Surface,
            };
            // 列宽在构建完成后按内容实测回填（标签列 = 最长标签实测，数值列 = numeric+双键）。
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));   // 标签
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 控件
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));   // 数值
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(row, 0, tableRow++);
            paramRows.Add(row);
            return row;
        }

        Label MakeLabel(string text, bool subtitle)
        {
            _rowLabels.Add(text);
            var label = new Label
            {
                Text = text,
                ForeColor = UiVisualStyle.Text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
            };
            if (subtitle)
            {
                label.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle);
                label.Padding = new Padding(0, 0, D(8), 0);
            }
            return label;
        }

        // 自绘 −/+ 微调键（mini-chip 皮肤，同液冷行迷你键）：替代被隐藏的系统箭头子窗口。
        RButton MakeSpinChip(string glyph, Action click, int leftMargin)
        {
            var chip = new RButton
            {
                Tag = "mini-chip",
                Text = glyph,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(leftMargin, 0, 0, 0),
                BackColor = UiVisualStyle.Surface,
                ForeColor = UiVisualStyle.Text,
                BorderColor = UiVisualStyle.Border,
                Cursor = Cursors.Hand,
            };
            // 边长 = max(D(22), 字形实测 + 内距)。下限 D(22) 来自审计口径：100pct 视口把控件缩到
            // 0.57× 后 available = 边−8/−4，字形实测 10x19 + 容差 2 要求缩放后边 ≥ 21（真机 175% 实测）。
            Size need = TextRenderer.MeasureText(glyph, chip.Font, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int side = Math.Max(D(22), Math.Max(need.Width + D(8), need.Height + D(6)));
            chip.Size = new Size(side, side);
            chip.Click += (_, _) => click();
            return chip;
        }

        (RSlider bar, RNumericUpDown valueInput) AddSliderRow(string label, int min, int max, int value, Action<int> onChanged)
        {
            var row = MakeRow();
            row.Controls.Add(MakeLabel(label, subtitle: true), 0, 0);
            // 深潜座舱：自绘滑条（原生 TrackBar 会漏出灰轨道与系统蓝滑块）。
            var bar = new RSlider
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                Dock = DockStyle.Fill, Margin = Padding.Empty,
            };
            bar.Tag = row;
            bar.Enabled = max > min;
            var valueInput = new RNumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = bar.Value,
                Increment = 1,
                DecimalPlaces = 0,
                TextAlign = HorizontalAlignment.Right,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = Padding.Empty,
                MinimumSize = new Size(D(52), 0),
                Enabled = max > min,
            };
            valueInput.HideNativeSpinButtons();
            var decChip = MakeSpinChip("−", valueInput.DownButton, D(6));
            var incChip = MakeSpinChip("+", valueInput.UpButton, D(2));
            valueInput.EnabledChanged += (_, _) => decChip.Enabled = incChip.Enabled = valueInput.Enabled;
            decChip.Enabled = incChip.Enabled = valueInput.Enabled;
            int lastSent = int.MinValue;
            bar.ValueChanged += (_, _) =>
            {
                if (valueInput.Value != bar.Value) valueInput.Value = bar.Value;
                if (_syncing || bar.Value == lastSent) return;
                lastSent = bar.Value;
                onChanged(bar.Value);
            };
            valueInput.ValueChanged += (_, _) =>
            {
                int requested = Decimal.ToInt32(valueInput.Value);
                if (bar.Value != requested) bar.Value = requested;
            };
            var valueCell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                BackColor = UiVisualStyle.Surface,
            };
            valueCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // 列宽 = 键宽（实测字形 + D(22) 下限）+ 左边距 + 余量：列窄于键会把键挤出列（parent-overflow）。
            valueCell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(30)));
            valueCell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(27)));
            valueCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            valueCell.Controls.Add(valueInput, 0, 0);
            valueCell.Controls.Add(decChip, 1, 0);
            valueCell.Controls.Add(incChip, 2, 0);
            row.Controls.Add(bar, 1, 0);
            row.Controls.Add(valueCell, 2, 0);
            return (bar, valueInput);
        }
        RCheckBox AddCheckRow(string label, bool on, Action<bool> onChanged)
        {
            var row = MakeRow();
            row.Controls.Add(MakeLabel(label, subtitle: false), 0, 0);
            var chk = new RCheckBox { Text = "启用", Checked = on, ForeColor = UiVisualStyle.Text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            chk.Tag = row;
            chk.CheckedChanged += (_, _) => { if (!_syncing) onChanged(chk.Checked); };
            row.Controls.Add(chk, 1, 0);
            return chk;
        }
        var hw = Program.hw;

        // ---- Windows 电源计划 / 睿频（按自定义档独立保存，机制取自 G-Helper PowerNative 协议）----
        void AddComboRow(string label, RComboBox combo)
        {
            var row = MakeRow();
            row.Controls.Add(MakeLabel(label, subtitle: true), 0, 0);
            combo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            combo.Margin = Padding.Empty;
            row.Controls.Add(combo, 1, 0);
            row.SetColumnSpan(combo, 2);
            // ComboBox 的高度由字体派生（SetBoundsCore 强制，form.Scale 缩不动它）：审计缩放视口里
            // 行被缩到 32 而下拉仍 37 → 溢出。行高只增不减地跟随下拉实际高（基线 D(32)），
            // 字体/句柄就绪后同步；宿主 DPI 下下拉 37 < 56，行保持 D(32) 节奏不变。
            void SyncRowHeight() => row.Height = Math.Max(row.Height, combo.Height);
            combo.FontChanged += (_, _) => SyncRowHeight();
            combo.HandleCreated += (_, _) => SyncRowHeight();
            SyncRowHeight();
            // 下拉列表宽度 = 最长项文本宽度（避免文字被截断）
            int maxW = 0;
            foreach (var it in combo.Items) maxW = Math.Max(maxW, TextRenderer.MeasureText(it.ToString() ?? "", combo.Font).Width);
            combo.DropDownWidth = maxW + 30;
        }
        _planCombo = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Key" };   // 只显示名称（KVP 默认 ToString 会带出 GUID）
        foreach (var (guid, name) in WinPowerPlan.GetPlans())
            _planCombo.Items.Add(new KeyValuePair<string, string>(name, guid));
        _planCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingWinPower || _currentIdx < 0) return;
            string? guid = GetPowerPlanGuid(_planCombo.SelectedItem);
            if (guid is null) return;
            bool planConfirmed = WinPowerPlan.SetActivePlan(guid);
            bool boostConfirmed = planConfirmed && (_boostCombo.SelectedIndex < 0 || WinPowerPlan.SetBoost(_boostCombo.SelectedIndex));
            if (planConfirmed)
            {
                AppConfig.Set(WinPowerPlan.GetProfilePlanKey(_currentIdx), guid);
                if (boostConfirmed && _boostCombo.SelectedIndex >= 0)
                    AppConfig.Set(WinPowerPlan.GetProfileBoostKey(_currentIdx), _boostCombo.SelectedIndex);
                AppConfig.Flush();
            }

            if (!planConfirmed)
            {
                LoadWindowsPowerSettings(_currentIdx);
                _status.Text = "电源计划未确认，未保存";
                _status.ForeColor = UiVisualStyle.Danger;
            }
            else if (!boostConfirmed)
            {
                LoadWindowsPowerSettings(_currentIdx);
                _status.Text = "电源计划已确认，睿频模式未确认";
                _status.ForeColor = UiVisualStyle.Warn;
            }
            else
            {
                _status.Text = "电源计划与睿频模式已确认";
                _status.ForeColor = UiVisualStyle.Ok;
            }
            Logger.WriteLine($"Custom profile {_currentIdx} power plan requested: {guid} planConfirmed={planConfirmed} boostConfirmed={boostConfirmed}");
        };
        AddComboRow("电源计划", _planCombo);

        _boostCombo = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (name, _) in WinPowerPlan.BoostModes) _boostCombo.Items.Add(name);
        _boostCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingWinPower || _currentIdx < 0) return;
            int v = _boostCombo.SelectedIndex;
            if (v < 0) return;
            if (WinPowerPlan.SetBoost(v))
            {
                AppConfig.Set(WinPowerPlan.GetProfileBoostKey(_currentIdx), v);
                AppConfig.Flush();
                _status.Text = "睿频模式已确认";
                _status.ForeColor = UiVisualStyle.Ok;
            }
            else
            {
                LoadWindowsPowerSettings(_currentIdx);
                _status.Text = "睿频模式未确认，未保存";
                _status.ForeColor = UiVisualStyle.Danger;
            }
        };
        AddComboRow("睿频模式", _boostCombo);

        // CPU/TGP 范围来自 GCU；GPU 频率优先使用驱动实时范围，失败时回退 GCU。
        var pl1Range = DeviceRange(hw?.Pl1Minimum ?? -1, hw?.Pl1Maximum ?? -1, hw?.Pl1 ?? 0);
        var pl2Range = DeviceRange(hw?.Pl2Minimum ?? -1, hw?.Pl2Maximum ?? -1, hw?.Pl2 ?? 0);
        (_pl1, _pl1Val) = AddSliderRow("CPU 功耗墙 PL1 (W)", pl1Range.Min, pl1Range.Max, pl1Range.Value, v => Queue("PL1", v.ToString()));
        (_pl2, _pl2Val) = AddSliderRow("CPU 功耗墙 PL2 (W)", pl2Range.Min, pl2Range.Max, pl2Range.Value, v => Queue("PL2", v.ToString()));
        // PL4 是瞬时功耗墙，不是所有机型都上报；ApplyRange 会在范围无效时自动隐藏整行。
        var pl4Range = DeviceRange(hw?.Pl4Minimum ?? -1, hw?.Pl4Maximum ?? -1, hw?.Pl4 ?? 0);
        (_pl4, _pl4Val) = AddSliderRow("CPU 瞬时功耗墙 PL4 (W)", pl4Range.Min, pl4Range.Max, pl4Range.Value, v => Queue("PL4", v.ToString()));
        // 温度墙开关：档位默认是关的（实测），关了 EC 不应用温度值——必须单独发包切换
        _tccChk = AddCheckRow("CPU 温度墙", hw?.TccSwitch ?? false, on =>
        {
            Queue("CpuTccOffsetSwitch", on ? "1" : "0");
            _tcc.Enabled = on;
        });
        var tccRange = DeviceRange(hw?.TccMinimum ?? -1, hw?.TccMaximum ?? -1, hw?.TccTarget ?? 0);
        (_tcc, _tccVal) = AddSliderRow("温度墙值 (°C)", tccRange.Min, tccRange.Max, tccRange.Value, v => Queue("CpuTccOffset", v.ToString()));
        _tccChk.Enabled = tccRange.Adjustable;
        var tgpRange = DeviceRange(hw?.GpuTgpMinimum ?? -1, hw?.GpuTgpMaximum ?? -1, hw?.GpuTgp ?? 0);
        (_tgp, _tgpVal) = AddSliderRow("GPU TGP 目标 (W)", tgpRange.Min, tgpRange.Max, tgpRange.Value, v => Queue("GpuConfigurableTGPTarget", v.ToString()));
        _dbChk = AddCheckRow("GPU 动态加速", hw?.GpuDbSwitch ?? false, on => { Queue("GpuDynamicBoostSwitch", on ? "1" : "0"); _db.Enabled = on; });
        var dbRange = DeviceRange(hw?.GpuDbMinimum ?? -1, hw?.GpuDbMaximum ?? -1, hw?.GpuDb ?? 0);
        (_db, _dbVal) = AddSliderRow("动态加速值", dbRange.Min, dbRange.Max, dbRange.Value, v => Queue("GpuDynamicBoost", v.ToString()));
        _dbChk.Enabled = dbRange.Adjustable;

        // ---- 风扇转换灵敏度（官方"风扇转换灵敏度"，值是换挡延迟毫秒数）----
        // 关着的时候服务端会忽略数值，所以数值行跟着开关一起启停。
        _fanSwitchSpeedChk = AddCheckRow("风扇转换灵敏度", hw?.FanSwitchSpeedEnabled ?? false, on =>
        {
            Queue("FanSwitchSpeedEnabled", on ? "1" : "0");
            _fanSwitchSpeed.Enabled = _fanSwitchSpeedVal.Enabled = on;
        });
        var fanSwitchRange = DeviceRange(
            hw?.FanSwitchSpeedMinimum ?? -1, hw?.FanSwitchSpeedMaximum ?? -1, hw?.FanSwitchSpeed ?? 0);
        (_fanSwitchSpeed, _fanSwitchSpeedVal) = AddSliderRow("换挡延迟 (ms)",
            fanSwitchRange.Min, fanSwitchRange.Max, fanSwitchRange.Value,
            v => Queue("FanSwitchSpeed", v.ToString()));
        // EC 以 100 ms 为一档，滑块按整档走，避免用户选到会被固件截断的值。
        _fanSwitchSpeed.SmallChange = _fanSwitchSpeed.LargeChange = MechrevoHw.FanSwitchSpeedStepMs;
        _fanSwitchSpeedVal.Increment = MechrevoHw.FanSwitchSpeedStepMs;
        _fanSwitchSpeedChk.Enabled = hw?.SupportsFanSwitchSpeed == true;

        // ---- GPU 超频区（驱动直连或 LCHWOC 任一通道可用时显示）----
        _ocChk = AddCheckRow("GPU 超频", hw?.GpuOverclockEnabled ?? false, on =>
        {
            // 用户意图先落地：零偏移的开启无法被驱动回读确认，见 _gpuOcArmed 注释。
            _gpuOcArmed = on;
            Queue("OverClockingSwitch", on ? "1" : "0");
            // 关：只禁用数值行，绝不重算数值（ApplyRange 在范围不可用/设备报 0 时会把用户值
            // 清零）；开：先按同一来源启用，具体范围与数值等回读完成后由 OnCustomChanged 统一 clamp。
            SyncGpuOverclockDependents();
        });
        var coreRange = DeviceRange(hw?.GpuCoreOffsetUserMinimum ?? -1, hw?.GpuCoreOffsetUserMaximum ?? -1, hw?.EffectiveGpuCoreClockOffset ?? 0, allowNegative: true);
        (_coreOc, _coreOcVal) = AddSliderRow("核心频率偏移 (MHz)", coreRange.Min, coreRange.Max, coreRange.Value, v => Queue("GpuCoreClockOffsetOC", v.ToString()));
        var memoryRange = DeviceRange(hw?.GpuMemoryOffsetUserMinimum ?? -1, hw?.GpuMemoryOffsetUserMaximum ?? -1, hw?.EffectiveGpuMemoryClockOffset ?? 0, allowNegative: true);
        (_memOc, _memOcVal) = AddSliderRow("显存频率偏移 (MHz)", memoryRange.Min, memoryRange.Max, memoryRange.Value, v => Queue("GpuMemoryClockOffsetOC", v.ToString()));
        _ocChk.Enabled = Program.UiAuditMode || hw?.SupportsGpuOverclock == true && (coreRange.Adjustable || memoryRange.Adjustable);
        _gpuOcCoreAdjustable = coreRange.Adjustable;
        _gpuOcMemoryAdjustable = memoryRange.Adjustable;
        SyncGpuOverclockDependents();   // 初值来源与回读一致：开关 OFF ⇒ 数值行不可拖动
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(table, 0, rootRow++);

        // ---- 底部：恢复默认 + 提示（同一行流式布局）----
        var bottomFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiVisualStyle.Window,
        };
        var btnRestore = new RButton
        {
            Text = "恢复当前档默认",
            Width = D(120),
            Height = D(28),
            Margin = new Padding(0, 0, D(12), 0),
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        btnRestore.Click += async (_, _) =>
        {
            if (Program.service is null || Program.hw is not { IsConnected: true }) return;
            if (MessageBox.Show("恢复当前自定义档为默认参数？", "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _status.Text = "恢复中…";
            await Task.Run(async () =>
            {
                await Program.hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "RESTORE_OPERATING_MODE_DETAIL" });
                await Program.hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "RESTORE_FAN_SPEED_CURVE_SETTING", ["Name"] = Program.hw.TableName });
            });
            _status.Text = "已发送恢复命令";
        };
        bottomFlow.Controls.Add(btnRestore);
        var btnFanCurve = new RButton
        {
            Text = "风扇曲线",
            Width = D(96),
            Height = D(28),
            Margin = new Padding(0, 0, D(12), 0),
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        btnFanCurve.Click += (_, _) =>
        {
            // 专用曲线编辑器（原版协议：16 点 duty 上下拖动实时保存）
            var f = new FanCurveForm();
            AddOwnedForm(f);
            f.Location = new Point(Math.Max(0, Left - f.Width - 10), Top);
            f.Show();
        };
        bottomFlow.Controls.Add(btnFanCurve);
        var hintLabel = new Label
        {
            Text = "参数保存到当前选中的自定义档。",
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Margin = new Padding(0, D(7), 0, 0),
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
        };
        bottomFlow.Controls.Add(hintLabel);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(bottomFlow, 0, rootRow++);
        root.RowCount = rootRow;

        // ---- 内容实测宽度：标签列（最长标签一行）+ 滑条最短可用轨道 + 数值列（numeric+双键）----
        // 标签列额外留 D(8) 右内距 + D(12) 审计裁切余量（GetTextClipping 扣 Padding+8 后仍须容下实测宽）。
        int labelMeasure = 0;
        using (Graphics measureGraphics = CreateGraphics())
        using (var subtitleFont = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle))
        {
            foreach (string text in _rowLabels)
                labelMeasure = Math.Max(labelMeasure, TextRenderer.MeasureText(measureGraphics, text, subtitleFont,
                    Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        }
        int labelCol = labelMeasure + D(8) + D(12);
        int sliderCol = D(RSlider.MinTrackLogicalWidth);
        int valueCol = D(52) + D(30) + D(27);
        int paramNeeds = labelCol + sliderCol + valueCol;
        // 底行用控件自身的 PreferredSize（= AutoSize 实际渲染宽）：裸 MeasureText 少算 AutoSize
        // 内边距，100pct 审计视口下底行溢出窗口 8px（真机复测）。
        int bottomNeeds = btnRestore.Width + btnRestore.Margin.Horizontal
            + btnFanCurve.Width + btnFanCurve.Margin.Horizontal
            + hintLabel.GetPreferredSize(Size.Empty).Width + hintLabel.Margin.Horizontal + D(8);
        int tabNeeds = 0;
        foreach (RButton tab in _profileBtns) tabNeeds += tab.PreferredSize.Width + tab.Margin.Horizontal;
        _minContentWidth = Math.Max(paramNeeds, Math.Max(bottomNeeds, tabNeeds))
            + root.Padding.Horizontal + table.Padding.Horizontal + D(8);
        foreach (TableLayoutPanel paramRow in paramRows)
        {
            paramRow.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, labelCol);
            paramRow.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, valueCol);
        }
        MinimumSize = new Size(_minContentWidth + D(24), D(280));   // 外框最小宽留出窗框
        ClientSize = new Size(_minContentWidth, ClientSize.Height);

        // ---- 防抖发送：滑条拖动合并 400ms 后逐字段 SET_OPERATING_MODE_DETAIL + GETSTATUS 回读 ----
        _debounce = new System.Windows.Forms.Timer { Interval = 400 };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await FlushPendingAsync();
        };

        if (Program.hw is not null)
        {
            Program.hw.CustomModeChanged += OnCustomChanged;
            FormClosed += (_, _) =>
            {
                Program.hw.CustomModeChanged -= OnCustomChanged;
                _debounce.Stop();
                _debounce.Dispose();
                _powerWallTimer.Stop();
                _powerWallTimer.Dispose();
            };
        }
        else
            FormClosed += (_, _) =>
            {
                _debounce.Dispose();
                _powerWallTimer.Stop();
                _powerWallTimer.Dispose();
            };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        _powerWallTimer.Tick += (_, _) => UpdatePowerWallVerdict();
        VisibleChanged += async (_, _) =>
        {
            // 窗口隐藏后 arm 不再有意义：重开时以硬件回读为准，否则「开了但还没拨值」
            // 的意图会让下次打开的开关与真实硬件状态对不上。
            if (!Visible) _gpuOcArmed = false;
            // 审计模式下不采样：会引入随时间变化的文本，把截图比对搅乱。
            _powerWallTimer.Enabled = Visible && !Program.UiAuditMode;
            if (!Visible || Program.UiAuditMode) return;
            LoadWindowsPowerSettings(CurrentConfigurationProfile());
            await RefreshHardwareStateAsync();
        };
        LoadWindowsPowerSettings(CurrentConfigurationProfile());
        OnCustomChanged();
        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplySection(table);
        ResizeToContent();
    }

    /// <summary>
    /// 窗口尺寸跟随内容：高度按内容收紧（MinimumSize 为下限），宽度不低于构建期实测的
    /// <see cref="_minContentWidth"/>（标签/滑条/数值列的最小需要）。内容没变时不重设，
    /// 避免和用户的手动缩放打架；用户可以放大，缩不破内容下限。
    /// </summary>
    void ResizeToContent()
    {
        if (IsDisposed) return;
        ResponsiveLayout.PerformLayoutTree(this);
        bool widthChanged = false;
        if (ClientSize.Width < _minContentWidth)
        {
            ClientSize = new Size(_minContentWidth, ClientSize.Height);
            widthChanged = true;
        }
        int content = ResponsiveLayout.VisibleContentBottom(_scrollHost);
        if (content == _laidOutContentHeight && !widthChanged) return;
        _laidOutContentHeight = content;
        int frame = Height - ClientSize.Height;
        int target = Math.Max(content, MinimumSize.Height - frame);
        if (ClientSize.Height != target)
            ClientSize = new Size(ClientSize.Width, target);
    }

    /// <summary>
    /// 采一个功耗样本并刷新结论。
    /// 只有「明确判出没生效」才用警示色——判不出来是常态（轻载时本来就判不出来），
    /// 不该拿它去吓用户，所以那种情况文本留空。
    /// </summary>
    void UpdatePowerWallVerdict()
    {
        if (Program.UiAuditMode) return;
        HardwareControl.SampleLocalPower();
        PowerWallVerdict verdict = HardwareControl.powerWall.Evaluate(DateTime.Now);
        string text = verdict switch
        {
            PowerWallVerdict.Enforced or PowerWallVerdict.NotEnforced =>
                HardwareControl.powerWall.Describe(DateTime.Now),
            _ => "",
        };
        if (_powerWallStatus.Text != text)
        {
            _powerWallStatus.Text = text;
            if (verdict == PowerWallVerdict.NotEnforced) Logger.WriteLine("PowerWall: " + text);
        }
        // 空文本时隐藏整个标签：可见但零宽的控件是布局缺陷。
        _powerWallStatus.Visible = text.Length > 0;
        _powerWallStatus.ForeColor = verdict == PowerWallVerdict.NotEnforced
            ? UiVisualStyle.Warn
            : UiVisualStyle.Muted;
    }

    void Queue(string field, string value)
    {
        _pending[field] = value;
        _debounce.Stop();
        _debounce.Start();
    }

    internal async Task<bool> ActivateProfileAsync(int index)
    {
        if (index is < 0 or > 3 || Program.service is null) return false;

        // A pending slider value belongs to the profile that was active when it
        // was edited. Commit it before changing the GCU profile index.
        _debounce.Stop();
        await FlushPendingAsync();

        // 档位换了，超频开关意图随之失效：新档的开关必须由该档的硬件回读决定。
        _gpuOcArmed = false;

        int previousProfile = CurrentConfigurationProfile();
        _status.Text = SwitchPendingText;
        _status.ForeColor = UiVisualStyle.Warn;
        bool ok = await Program.service.SwitchCustomProfile(index);
        if (!ok)
        {
            // SwitchCustomProfile 的确认窗口只有 250ms+900ms；慢回读时它返回 false，但硬件
            // 随后确实切了过去，CustomModeChanged 会把状态刷成「已激活」——成功切换因此
            // 闪现「切换未确认」。宽限期内等硬件上报目标档，只有等不到才算真失败。
            for (int attempt = 0; attempt < SwitchGraceAttempts && Program.hw?.CustomProfileIndex != index; attempt++)
                await Task.Delay(SwitchGraceDelayMs);
            ok = !SwitchShouldReportFailure(serviceConfirmed: false, Program.hw?.CustomProfileIndex ?? -1, index);
        }
        if (ok)
        {
            // Persist the local profile only after GCU confirms the same hardware slot.
            // Loading it before the MQTT confirmation made a failed switch look successful
            // and caused the next page open to restore the previous slot's values.
            AppConfig.Set("custom_last_profile", index);
            AppConfig.Flush();
            LoadWindowsPowerSettings(index);
            if (!WinPowerPlan.ApplyProfile(index))
            {
                _status.Text = "自定义 " + (index + 1) + " 已激活，Windows 电源设置未确认";
                _status.ForeColor = UiVisualStyle.Warn;
            }
        }
        else
        {
            LoadWindowsPowerSettings(previousProfile);
            _status.Text = SwitchUnconfirmedText;
            _status.ForeColor = UiVisualStyle.Danger;
        }
        return ok;
    }

    async Task FlushPendingAsync()
    {
        if (Program.service is null) return;
        await _saveLock.WaitAsync();
        try
        {
            while (_pending.Count > 0)
            {
                var fields = new Dictionary<string, string>(_pending);
                _pending.Clear();
                bool ok = await Program.service.SetCustomDetail(fields);
                _status.Text = ok ? "参数已确认" : "参数未应用，已恢复回读值";
                _status.ForeColor = ok ? UiVisualStyle.Ok : UiVisualStyle.Danger;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Custom profile update failed: " + ex.Message);
            _status.Text = "参数保存失败";
            _status.ForeColor = UiVisualStyle.Danger;
        }
        finally
        {
            if (_pending.Count > 0) _debounce.Start();
            _saveLock.Release();
        }
    }

    static (int Min, int Max, int Value, bool Adjustable) DeviceRange(
        int minimum, int maximum, int current, bool allowNegative = false)
    {
        if ((allowNegative || minimum >= 0) && maximum > minimum)
            return (minimum, maximum, Math.Clamp(current, minimum, maximum), true);
        if (Program.UiAuditMode)
            return (0, 100, Math.Clamp(current, 0, 100), true);
        int locked = Math.Max(0, current);
        return (locked, locked, locked, false);
    }

    internal static string? GetPowerPlanGuid(object? selectedItem)
    {
        if (selectedItem is not KeyValuePair<string, string> plan || !Guid.TryParse(plan.Value, out Guid guid))
            return null;
        return guid.ToString();
    }

    int CurrentConfigurationProfile()
    {
        int hardwareProfile = Program.hw?.CustomProfileIndex ?? -1;
        return hardwareProfile is >= 0 and <= 3
            ? hardwareProfile
            : Math.Clamp(AppConfig.Get("custom_last_profile", 0), 0, 3);
    }

    void LoadWindowsPowerSettings(int profileIndex)
    {
        if (profileIndex is < 0 or > 3) return;
        _syncingWinPower = true;
        try
        {
            _currentIdx = profileIndex;
            WinPowerPlan.ProfileSettings settings = WinPowerPlan.GetOrCreateProfileSettings(profileIndex);
            string desiredPlan = settings.Plan;
            _planCombo.SelectedIndex = -1;
            for (int i = 0; i < _planCombo.Items.Count; i++)
            {
                if (_planCombo.Items[i] is KeyValuePair<string, string> plan &&
                    plan.Value.Equals(desiredPlan, StringComparison.OrdinalIgnoreCase))
                {
                    _planCombo.SelectedIndex = i;
                    break;
                }
            }
            if (_planCombo.SelectedIndex < 0 && _planCombo.Items.Count > 0)
                _planCombo.SelectedIndex = 0;

            int savedBoost = settings.Boost;
            if (savedBoost < 0 || savedBoost >= _boostCombo.Items.Count)
            {
                // 没保存值时不能留 -1（DropDownList 关闭态由原生控件绘制，空选择会显示成空白）；
                // 优先落到「系统自动」档（Windows 默认），找不到则取第一项。
                savedBoost = Array.FindIndex(WinPowerPlan.BoostModes, m => m.Name.Contains("系统自动"));
                if (savedBoost < 0) savedBoost = 0;
            }
            _boostCombo.SelectedIndex = savedBoost;
        }
        finally { _syncingWinPower = false; }
    }

    async Task RefreshHardwareStateAsync()
    {
        if (_refreshingHardwareState || Program.hw is not { IsConnected: true } hw) return;
        _refreshingHardwareState = true;
        try
        {
            // NVAPI discovery can wake a sleeping dGPU and must not block the UI
            // thread. Retry whenever the form becomes visible so late driver/GPU
            // initialization does not permanently hide the OC controls.
            await Task.Run(hw.EnsureDirectGpuOverclock);
            await hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            await hw.Publish("LCHWOC/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        }
        catch (Exception ex) { Logger.WriteLine("Custom mode status refresh failed: " + ex.Message); }
        finally
        {
            _refreshingHardwareState = false;
            OnCustomChanged();
        }
    }

    static void SetRowVisible(Control control, bool visible, Control? value = null)
    {
        // Tag 现在指向整行面板：隐藏行即隐藏标签+控件+数值（含 −/+ 键）。
        if (control.Tag is Control row) row.Visible = visible;
        if (value is not null) value.Visible = visible;
    }

    static bool ApplyRange(RSlider bar, RNumericUpDown valueInput, int minimum, int maximum, int current,
        bool allowNegative = false)
    {
        var range = DeviceRange(minimum, maximum, current, allowNegative);
        bar.Minimum = Math.Min(bar.Minimum, range.Min);
        bar.Maximum = Math.Max(bar.Maximum, range.Max);
        bar.Value = Math.Clamp(range.Value, bar.Minimum, bar.Maximum);
        bar.Minimum = range.Min;
        bar.Maximum = range.Max;
        bar.Value = range.Value;
        valueInput.Minimum = Math.Min(valueInput.Minimum, range.Min);
        valueInput.Maximum = Math.Max(valueInput.Maximum, range.Max);
        valueInput.Value = range.Value;
        valueInput.Minimum = range.Min;
        valueInput.Maximum = range.Max;
        bar.Enabled = range.Adjustable;
        valueInput.Enabled = range.Adjustable;
        SetRowVisible(bar, range.Adjustable, valueInput);
        return range.Adjustable;
    }

    /// <summary>
    /// GPU 超频区唯一状态来源：数值行（滑条 + 数值框 + ± 键）的可用性只由开关当前勾选状态
    /// 与可调性快照推导。构建后、硬件回读后、用户拨动开关后都经这一个方法，杜绝
    /// 「开关 OFF 而数值行仍可拖动」的首次打开不一致（数值框的 Enabled 会带动 ± 键）。
    /// </summary>
    void SyncGpuOverclockDependents()
    {
        bool on = _ocChk.Checked;
        _coreOc.Enabled = _coreOcVal.Enabled = on && _gpuOcCoreAdjustable;
        _memOc.Enabled = _memOcVal.Enabled = on && _gpuOcMemoryAdjustable;
    }

    void OnCustomChanged()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(OnCustomChanged); return; }
            var hw = Program.hw;
            if (hw is null) return;
            _syncing = true;
            try
            {
                for (int i = 0; i < _profileBtns.Length; i++)
                {
                    bool active = hw.CustomProfileIndex == i;
                    _profileBtns[i].Activated = active;
                }
                ApplyRange(_pl1, _pl1Val, hw.Pl1Minimum, hw.Pl1Maximum, hw.Pl1);
                ApplyRange(_pl2, _pl2Val, hw.Pl2Minimum, hw.Pl2Maximum, hw.Pl2);
                ApplyRange(_pl4, _pl4Val, hw.Pl4Minimum, hw.Pl4Maximum, hw.Pl4);
                bool tccAdjustable = ApplyRange(_tcc, _tccVal, hw.TccMinimum, hw.TccMaximum, hw.TccTarget);
                bool tgpAdjustable = ApplyRange(_tgp, _tgpVal, hw.GpuTgpMinimum, hw.GpuTgpMaximum, hw.GpuTgp);
                bool dbAdjustable = ApplyRange(_db, _dbVal, hw.GpuDbMinimum, hw.GpuDbMaximum, hw.GpuDb);
                bool ocSupported = hw.SupportsGpuOverclock;
                // 硬件确认的开启：设备把「开了但偏移还是 0」报成未开启，所以它只决定是否
                // 套用/夹取数值（见下），不决定 UI 开关的位置。
                bool ocHardwareOn = hw.GpuOverclockEnabled && ocSupported;
                // UI 开关 = 硬件确认开启 或 用户本次已开启（arm）。零偏移的开启无法被驱动确认，
                // 用硬件状态回写会把用户刚打开的开关弹回、数值行随之禁用，用户来不及拨值。
                bool ocSwitchOn = (ocHardwareOn || _gpuOcArmed) && ocSupported;
                // 只在超频已开启时套用/夹取数值：关闭时设备会把偏移报成 0，ApplyRange 会把
                // 用户刚设的值清零（用户报告：关掉开关下面的数值归零）。关闭时保留当前 UI 输入
                // 与可调性快照，等重新开启时再 clamp。
                if (ocHardwareOn)
                {
                    _gpuOcCoreAdjustable = ApplyRange(_coreOc, _coreOcVal, hw.GpuCoreOffsetUserMinimum, hw.GpuCoreOffsetUserMaximum, hw.EffectiveGpuCoreClockOffset, allowNegative: true);
                    _gpuOcMemoryAdjustable = ApplyRange(_memOc, _memOcVal, hw.GpuMemoryOffsetUserMinimum, hw.GpuMemoryOffsetUserMaximum, hw.EffectiveGpuMemoryClockOffset, allowNegative: true);
                }
                else if (_gpuOcArmed && ocSupported)
                {
                    // arm 期间刷新可调性快照（只读布尔，不碰数值）：构建时驱动/GCU 尚未就绪
                    // 会留下 false 快照，若不刷新则数值行永远锁死。
                    _gpuOcCoreAdjustable = hw.GpuCoreOffsetAdjustable;
                    _gpuOcMemoryAdjustable = hw.GpuMemoryOffsetAdjustable;
                }
                bool fanSwitchAdjustable = ApplyRange(_fanSwitchSpeed, _fanSwitchSpeedVal,
                    hw.FanSwitchSpeedMinimum, hw.FanSwitchSpeedMaximum, hw.FanSwitchSpeed);
                SetRowVisible(_tccChk, tccAdjustable);
                SetRowVisible(_dbChk, dbAdjustable);
                SetRowVisible(_ocChk, ocSupported);
                // 灵敏度整行的显隐只看机型是否报过这一项；范围是我们按 EC 字段宽度推的，恒有效。
                SetRowVisible(_fanSwitchSpeedChk, hw.SupportsFanSwitchSpeed);
                SetRowVisible(_fanSwitchSpeed, hw.SupportsFanSwitchSpeed, _fanSwitchSpeedVal);
                _fanSwitchSpeedChk.Checked = hw.FanSwitchSpeedEnabled;
                _fanSwitchSpeedChk.Enabled = hw.SupportsFanSwitchSpeed;
                _fanSwitchSpeed.Enabled = _fanSwitchSpeedVal.Enabled =
                    hw.SupportsFanSwitchSpeed && hw.FanSwitchSpeedEnabled && fanSwitchAdjustable;
                _tgp.Enabled = tgpAdjustable;
                _dbChk.Checked = hw.GpuDbSwitch;
                _dbChk.Enabled = dbAdjustable;
                _db.Enabled = hw.GpuDbSwitch && dbAdjustable;
                // Windows 电源设置：档位变化时加载该档保存值
                int configurationProfile = CurrentConfigurationProfile();
                if (configurationProfile != _currentIdx || _planCombo.SelectedIndex < 0)
                    LoadWindowsPowerSettings(configurationProfile);
                _tccChk.Checked = hw.TccSwitch;
                _tccChk.Enabled = tccAdjustable;
                _tcc.Enabled = hw.TccSwitch && tccAdjustable;
                _ocChk.Checked = ocSwitchOn;
                _ocChk.Enabled = ocSupported;
                SyncGpuOverclockDependents();
                if (hw.CustomProfileIndex >= 0)
                {
                    _status.Text = "自定义 " + (hw.CustomProfileIndex + 1) + " 已激活";
                    _status.ForeColor = UiVisualStyle.Ok;
                }
                ResizeToContent();
            }
            finally { _syncing = false; }
        }
        catch (Exception ex) { Logger.WriteLine("Custom mode UI refresh failed: " + ex.Message); }
    }
}
