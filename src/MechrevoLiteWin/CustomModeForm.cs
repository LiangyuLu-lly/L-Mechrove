using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.Mode;
using MechrevoLite.Properties;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>
/// 统一性能模式编辑器（G-Helper 式）：顶部下拉列出全部模式——静音 / 平衡 / 静音狂暴 / 狂暴 / 自定义若干，
/// 选中即切换到该模式，下面直接调它的参数。自定义模式可以新建、改名、删除。
///
/// <para>编辑的永远是**正在运行的模式**：固件级参数（功耗墙、温度墙、TGP、Dynamic Boost、GPU 超频、
/// 风扇转换灵敏度）显示硬件回读值，改动经 <see cref="PerfModeService"/> 保存并只下发变化项；
/// 应用侧参数（Windows 电源模式、电源计划、睿频、风扇增强、刷新率）按模式保存，「不改变」= 沿用官方行为。</para>
///
/// <para>内置模式第一次改功耗墙 / TGP / Dynamic Boost / 风扇曲线时，编排层用厂商出厂值补齐其余固件项、
/// 转到固件自定义档承载（真机实证：这些项只在自定义档里生效，见 <c>docs/hardware/gcu-modes-and-profiles.md</c>），
/// 界面如实提示这一变化。</para>
/// </summary>
public class CustomModeForm : RForm
{
    /// <summary>参数行高（逻辑 px）：容纳字体派生高的下拉（96dpi 23px）+ 上下各 ≥1px 呼吸。</summary>
    internal const int RowLogicalHeight = 26;

    internal static string SwitchPendingText => Strings.SwitchPending;
    internal static string SwitchUnconfirmedText => Strings.SwitchUnconfirmed;
    internal static string GcuDisconnectedText => Strings.GcuDisconnectedShort;
    internal static string FanTableTimeoutText => Strings.FanTableNotReady;

    readonly Dictionary<string, string> _pending = new();   // 防抖合并的待发送参数
    readonly System.Windows.Forms.Timer _debounce;
    readonly SemaphoreSlim _saveLock = new(1, 1);
    readonly List<string> _rowLabels = new();
    readonly ToolTip _tips = new();
    int _minContentWidth;
    bool _syncing;
    bool _syncingModes;   // 程序化刷新模式下拉 / 应用侧下拉时不触发切换与写入
    bool _refreshingHardwareState;
    int _modeSelectionSequence;
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
    // arm 保持到用户手动关闭，或窗口隐藏/切换模式时重置。
    bool _gpuOcArmed;
    // 「超频需要管理员权限」提示行：直连 NVAPI 写入在非提权进程里恒定被拒，
    // 非提权时该行可见并给出「以管理员身份重启」的显式入口（UAC 由用户在该步同意）。
    FlowLayoutPanel _gpuOcAdminRow = null!;

    // ---- 应用侧（按模式保存；第 0 项恒为「不改变」= null）----
    RComboBox _powerModeCombo = null!, _planCombo = null!, _boostCombo = null!, _fanBoostCombo = null!, _refreshCombo = null!;
    TableLayoutPanel _fanBoostRow = null!, _refreshRow = null!;

    // ---- 模式行 ----
    RComboBox _modeCombo = null!;
    RButton _addButton = null!, _renameButton = null!, _deleteButton = null!;
    FlowLayoutPanel _nameRow = null!;
    TextBox _nameBox = null!;
    string _modeId = PerfModeDefinition.CustomId(1);

    Label _status = null!;
    FlowLayoutPanel _statusRow = null!;

    /// <summary>
    /// 状态行只在有内容时出现：切换/下发结果或功耗墙结论。用各自的文字判断，而不是 Label.Visible——
    /// 父容器隐藏时子控件的 Visible 读出来恒为 false。
    /// </summary>
    void SyncStatusRow()
    {
        if (_statusRow is null || _status is null) return;
        bool statusText = !string.IsNullOrEmpty(_status.Text);
        bool powerWallText = _powerWallStatus is not null && !string.IsNullOrEmpty(_powerWallStatus.Text);
        _status.Visible = statusText;
        bool show = statusText || powerWallText;
        if (_statusRow.Visible != show) _statusRow.Visible = show;
    }
    Label _routeHint = null!;
    RButton _restoreButton = null!;
    RButton _fanCurveButton = null!;
    Label _powerWallStatus = null!;
    // 功耗墙判定需要持续采样；这个窗口开着的时候就是用户在关心功耗墙的时候。
    readonly System.Windows.Forms.Timer _powerWallTimer = new() { Interval = 2000 };

    /// <summary>下拉项：只显示文字，携带模式 Id。</summary>
    sealed record ModeItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    public CustomModeForm()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = Strings.PerfEditorTitle;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // 宽度在构建完成后按内容实测收紧；高度由 ResizeToContent 收紧。
        ClientSize = new Size(D(820), D(200));
        InitTheme(true);

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

        // ---- 模式行：下拉 + 新建 / 重命名 / 删除 ----
        // 按窗口宽度折行：窄窗口（1280×720 @100%）下按钮换到第二行，而不是被挤出窗口右边。
        var modeFlow = new FlowLayoutPanel
        {
            Name = "modeRow",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = UiVisualStyle.Window,
            Margin = new Padding(0, 0, 0, D(6)),
        };
        modeFlow.Controls.Add(new Label
        {
            Text = Strings.PerfModeLabel,
            AutoSize = true,
            ForeColor = UiVisualStyle.Muted,
            Margin = new Padding(0, D(6), D(8), 0),
        });
        _modeCombo = new RComboBox
        {
            Name = "modeCombo",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = D(190),
            Margin = new Padding(0, D(2), D(8), 0),
        };
        _modeCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (_syncingModes || _modeCombo.SelectedItem is not ModeItem item) return;
            await SelectModeAsync(item.Id);
        };
        modeFlow.Controls.Add(_modeCombo);
        RButton MakeModeButton(string name, string text, Func<Task> click)
        {
            var button = new RButton
            {
                Name = name,
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 0, D(4), 0),
                BackColor = UiVisualStyle.SurfaceRaised,
                ForeColor = UiVisualStyle.Text,
                BorderColor = UiVisualStyle.Border,
                Cursor = Cursors.Hand,
            };
            button.Click += async (_, _) => await click();
            modeFlow.Controls.Add(button);
            return button;
        }
        _addButton = MakeModeButton("buttonModeNew", Strings.PerfModeNew, AddCustomModeAsync);
        _renameButton = MakeModeButton("buttonModeRename", Strings.PerfModeRename, () => { BeginRename(); return Task.CompletedTask; });
        _deleteButton = MakeModeButton("buttonModeDelete", Strings.PerfModeDelete, DeleteCustomModeAsync);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(modeFlow, 0, rootRow++);

        // ---- 重命名行（只在点「重命名」后出现）----
        _nameRow = new FlowLayoutPanel
        {
            Name = "modeNameRow",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiVisualStyle.Window,
            Margin = new Padding(0, 0, 0, D(6)),
            Visible = false,
        };
        _nameRow.Controls.Add(new Label
        {
            Text = Strings.ProfileNameLabel,
            AutoSize = true,
            ForeColor = UiVisualStyle.Muted,
            Margin = new Padding(0, D(6), D(8), 0),
        });
        _nameBox = new TextBox
        {
            Name = "textModeName",
            Width = D(150),
            MaxLength = PerfModeCollection.MaxUserNameLength,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiVisualStyle.Input,
            ForeColor = UiVisualStyle.Text,
            Margin = new Padding(0, D(3), D(6), 0),
        };
        _nameBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndRename(); return; }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SaveNameAsync();
        };
        _nameRow.Controls.Add(_nameBox);
        var nameSave = new RButton
        {
            Name = "buttonModeNameSave",
            Text = Strings.PerfModeNameSave,
            AutoSize = true,
            Margin = new Padding(0, 0, D(4), 0),
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        nameSave.Click += async (_, _) => await SaveNameAsync();
        _nameRow.Controls.Add(nameSave);
        var nameCancel = new RButton
        {
            Name = "buttonModeNameCancel",
            Text = Strings.PerfModeNameCancel,
            AutoSize = true,
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        nameCancel.Click += (_, _) => EndRename();
        _nameRow.Controls.Add(nameCancel);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_nameRow, 0, rootRow++);

        // ---- 状态行：切换/下发结果 + 功耗墙实测结论 ----
        // 两段都为空时整行隐藏（可见但零宽的控件是布局缺陷）；有文字时按窗口宽度折行。
        var titleFlow = new FlowLayoutPanel
        {
            Name = "modeStatusRow",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = UiVisualStyle.Window,
            Margin = new Padding(0, 0, 0, D(4)),
            Visible = false,
        };
        _status = new Label { Name = "labelModeEditorStatus", Text = "", ForeColor = UiVisualStyle.Muted, AutoSize = true, Margin = Padding.Empty };
        titleFlow.Controls.Add(_status);
        _statusRow = titleFlow;
        _status.TextChanged += (_, _) => SyncStatusRow();
        // 功耗墙实测结论。单独一个标签而不是复用 _status：后者要显示各种一次性提示，
        // 混在一起会互相覆盖。判不出来时整个标签隐藏，不占地方。
        _powerWallStatus = new Label
        {
            Name = "labelPowerWallVerdict",
            Text = "",
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Margin = new Padding(D(8), 0, 0, 0),
            Visible = false,
        };
        titleFlow.Controls.Add(_powerWallStatus);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(titleFlow, 0, rootRow++);

        // ---- 路线说明：官方默认 / 已转到自定义档 / 自定义模式所在档 ----
        _routeHint = new Label
        {
            Name = "labelModeRouteHint",
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(D(480), 0),
            ForeColor = UiVisualStyle.Muted,
            Margin = new Padding(0, 0, 0, D(6)),
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_routeHint, 0, rootRow++);

        // ---- 参数区（紧凑行节奏：D(RowLogicalHeight) 固定行 + Padding.Empty 边距）----
        var table = new TableLayoutPanel
        {
            Name = "paramTable",
            Dock = DockStyle.Top,
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
                label.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle);
            label.Padding = new Padding(D(4), 0, subtitle ? D(8) : 0, 0);
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
            var chk = new RCheckBox { Text = Strings.EnabledLabel, Checked = on, ForeColor = UiVisualStyle.Text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            chk.Tag = row;
            chk.CheckedChanged += (_, _) => { if (!_syncing) onChanged(chk.Checked); };
            row.Controls.Add(chk, 1, 0);
            return chk;
        }
        TableLayoutPanel AddComboRow(string label, RComboBox combo)
        {
            var row = MakeRow();
            row.Controls.Add(MakeLabel(label, subtitle: true), 0, 0);
            combo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            combo.Margin = Padding.Empty;
            combo.Tag = row;
            row.Controls.Add(combo, 1, 0);
            row.SetColumnSpan(combo, 2);
            // ComboBox 的高度由字体派生（SetBoundsCore 强制，form.Scale 缩不动它）：审计缩放视口里
            // 行被缩到 32 而下拉仍 37 → 溢出。行高只增不减地跟随下拉实际高，字体/句柄就绪后同步。
            void SyncRowHeight() => row.Height = Math.Max(row.Height, combo.Height);
            combo.FontChanged += (_, _) => SyncRowHeight();
            combo.HandleCreated += (_, _) => SyncRowHeight();
            SyncRowHeight();
            FitDropDown(combo);
            return row;
        }
        var hw = Program.hw;

        // ---- 应用侧：Windows 电源模式 / 电源计划 / 睿频（按模式保存，「不改变」沿用官方行为）----
        _powerModeCombo = new RComboBox { Name = "powerModeCombo", DropDownStyle = ComboBoxStyle.DropDownList };
        _powerModeCombo.Items.AddRange(new object[]
        {
            Strings.ModeTuneUnchanged, Strings.ModeTunePowerEfficiency, Strings.ModeTunePowerBalanced, Strings.ModeTunePowerPerformance,
        });
        _powerModeCombo.SelectedIndex = 0;
        _powerModeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingModes || _powerModeCombo.SelectedIndex < 0) return;
            int? power = _powerModeCombo.SelectedIndex == 0 ? null : _powerModeCombo.SelectedIndex - 1;
            // 覆盖层只在「平衡」计划下生效：选了电源模式就把计划交还给它。
            if (power is not null) SelectPlan(null);
            _ = CommitAsync(s => s with
            {
                WindowsPowerMode = power,
                PowerPlanGuid = power is null || PerfModeApplyPlanner.IsBalancedPlan(s.PowerPlanGuid) ? s.PowerPlanGuid : null,
            });
        };
        AddComboRow(Strings.ModeTunePowerMode, _powerModeCombo);

        // 只显示名称（KVP 默认 ToString 会带出 GUID）；第 0 项「不改变」的 Value 为空串。
        _planCombo = new RComboBox { Name = "planCombo", DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Key" };
        _planCombo.Items.Add(new KeyValuePair<string, string>(Strings.ModeTuneUnchanged, ""));
        foreach (var (guid, name) in WinPowerPlan.GetPlans())
            _planCombo.Items.Add(new KeyValuePair<string, string>(name, guid));
        _planCombo.SelectedIndex = 0;
        _planCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingModes || _planCombo.SelectedIndex < 0) return;
            string? guid = GetPowerPlanGuid(_planCombo.SelectedItem);
            bool nonBalanced = guid is not null && !PerfModeApplyPlanner.IsBalancedPlan(guid);
            if (nonBalanced) SelectPowerMode(null);
            _ = CommitAsync(s => s with
            {
                PowerPlanGuid = guid,
                WindowsPowerMode = nonBalanced ? null : s.WindowsPowerMode,
            });
        };
        AddComboRow(Strings.PowerPlan, _planCombo);

        _boostCombo = new RComboBox { Name = "boostCombo", DropDownStyle = ComboBoxStyle.DropDownList };
        _boostCombo.Items.Add(Strings.ModeTuneUnchanged);
        foreach (var (name, _) in WinPowerPlan.BoostModes) _boostCombo.Items.Add(name);
        _boostCombo.SelectedIndex = 0;
        _boostCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingModes || _boostCombo.SelectedIndex < 0) return;
            int? boost = _boostCombo.SelectedIndex == 0 ? null : _boostCombo.SelectedIndex - 1;
            _ = CommitAsync(s => s with { CpuBoost = boost });
        };
        AddComboRow(Strings.BoostMode, _boostCombo);

        // CPU/TGP 范围来自 GCU；GPU 频率优先使用驱动实时范围，失败时回退 GCU。
        var pl1Range = DeviceRange(hw?.Pl1Minimum ?? -1, hw?.Pl1Maximum ?? -1, hw?.Pl1 ?? 0);
        var pl2Range = DeviceRange(hw?.Pl2Minimum ?? -1, hw?.Pl2Maximum ?? -1, hw?.Pl2 ?? 0);
        (_pl1, _pl1Val) = AddSliderRow(Strings.CpuPl1, pl1Range.Min, pl1Range.Max, pl1Range.Value, v => Queue("PL1", v.ToString()));
        (_pl2, _pl2Val) = AddSliderRow(Strings.CpuPl2, pl2Range.Min, pl2Range.Max, pl2Range.Value, v => Queue("PL2", v.ToString()));
        // PL4 是瞬时功耗墙，不是所有机型都上报；ApplyRange 会在范围无效时自动隐藏整行。
        // AMD 机型上 PL4 字段永不生效（官方 AMD 分支只发 CpuAmdFPPT），这一行映射到 fPPT，
        // 标签随之改名，免得上写了 PL4 的错觉；Intel 机型的文案与行为保持不变。
        var pl4Range = DeviceRange(hw?.Pl4Minimum ?? -1, hw?.Pl4Maximum ?? -1, hw?.Pl4 ?? 0);
        string pl4Label = hw?.UsesAmdPowerFields == true ? Strings.CpuFppt : Strings.CpuPl4;
        (_pl4, _pl4Val) = AddSliderRow(pl4Label, pl4Range.Min, pl4Range.Max, pl4Range.Value, v => Queue("PL4", v.ToString()));
        // 温度墙开关：档位默认是关的（实测），关了 EC 不应用温度值——必须单独发包切换
        _tccChk = AddCheckRow(Strings.CpuTempWall, hw?.TccSwitch ?? false, on =>
        {
            Queue("CpuTccOffsetSwitch", on ? "1" : "0");
            _tcc.Enabled = on;
        });
        var tccRange = DeviceRange(hw?.TccMinimum ?? -1, hw?.TccMaximum ?? -1, hw?.TccTarget ?? 0);
        (_tcc, _tccVal) = AddSliderRow(Strings.TempWallValue, tccRange.Min, tccRange.Max, tccRange.Value, v => Queue("CpuTccOffset", v.ToString()));
        _tccChk.Enabled = tccRange.Adjustable;
        var tgpRange = DeviceRange(hw?.GpuTgpMinimum ?? -1, hw?.GpuTgpMaximum ?? -1, hw?.GpuTgp ?? 0);
        (_tgp, _tgpVal) = AddSliderRow(Strings.GpuTgp, tgpRange.Min, tgpRange.Max, tgpRange.Value, v => Queue("GpuConfigurableTGPTarget", v.ToString()));
        _dbChk = AddCheckRow(Strings.GpuDynamicBoost, hw?.GpuDbSwitch ?? false, on => { Queue("GpuDynamicBoostSwitch", on ? "1" : "0"); _db.Enabled = on; });
        var dbRange = DeviceRange(hw?.GpuDbMinimum ?? -1, hw?.GpuDbMaximum ?? -1, hw?.GpuDb ?? 0);
        (_db, _dbVal) = AddSliderRow(Strings.DynamicBoostValue, dbRange.Min, dbRange.Max, dbRange.Value, v => Queue("GpuDynamicBoost", v.ToString()));
        _dbChk.Enabled = dbRange.Adjustable;

        // ---- 风扇转换灵敏度（官方"风扇转换灵敏度"，值是换挡延迟毫秒数）----
        // 关着的时候服务端会忽略数值，所以数值行跟着开关一起启停。
        _fanSwitchSpeedChk = AddCheckRow(Strings.FanSwitchSensitivity, hw?.FanSwitchSpeedEnabled ?? false, on =>
        {
            Queue("FanSwitchSpeedEnabled", on ? "1" : "0");
            _fanSwitchSpeed.Enabled = _fanSwitchSpeedVal.Enabled = on;
        });
        var fanSwitchRange = DeviceRange(
            hw?.FanSwitchSpeedMinimum ?? -1, hw?.FanSwitchSpeedMaximum ?? -1, hw?.FanSwitchSpeed ?? 0);
        (_fanSwitchSpeed, _fanSwitchSpeedVal) = AddSliderRow(Strings.FanSwitchDelay,
            fanSwitchRange.Min, fanSwitchRange.Max, fanSwitchRange.Value,
            v => Queue("FanSwitchSpeed", v.ToString()));
        // EC 以 100 ms 为一档，滑块按整档走，避免用户选到会被固件截断的值。
        _fanSwitchSpeed.SmallChange = _fanSwitchSpeed.LargeChange = MechrevoHw.FanSwitchSpeedStepMs;
        _fanSwitchSpeedVal.Increment = MechrevoHw.FanSwitchSpeedStepMs;
        _fanSwitchSpeedChk.Enabled = hw?.SupportsFanSwitchSpeed == true;

        // ---- GPU 超频区（驱动直连或 LCHWOC 任一通道可用时显示）----
        _ocChk = AddCheckRow(Strings.GpuOverclock, hw?.GpuOverclockEnabled ?? false, on =>
        {
            // 用户意图先落地：零偏移的开启无法被驱动回读确认，见 _gpuOcArmed 注释。
            _gpuOcArmed = on;
            Queue("OverClockingSwitch", on ? "1" : "0");
            // 关：只禁用数值行，绝不重算数值（ApplyRange 在范围不可用/设备报 0 时会把用户值
            // 清零）；开：先按同一来源启用，具体范围与数值等回读完成后由 OnCustomChanged 统一 clamp。
            SyncGpuOverclockDependents();
        });
        var coreRange = DeviceRange(hw?.GpuCoreOffsetUserMinimum ?? -1, hw?.GpuCoreOffsetUserMaximum ?? -1, hw?.EffectiveGpuCoreClockOffset ?? 0, allowNegative: true);
        (_coreOc, _coreOcVal) = AddSliderRow(Strings.CoreOffset, coreRange.Min, coreRange.Max, coreRange.Value, v => Queue("GpuCoreClockOffsetOC", v.ToString()));
        var memoryRange = DeviceRange(hw?.GpuMemoryOffsetUserMinimum ?? -1, hw?.GpuMemoryOffsetUserMaximum ?? -1, hw?.EffectiveGpuMemoryClockOffset ?? 0, allowNegative: true);
        (_memOc, _memOcVal) = AddSliderRow(Strings.MemoryOffset, memoryRange.Min, memoryRange.Max, memoryRange.Value, v => Queue("GpuMemoryClockOffsetOC", v.ToString()));
        _ocChk.Enabled = IsOcCheckboxEnabled(hw, coreRange.Adjustable || memoryRange.Adjustable);
        _gpuOcCoreAdjustable = coreRange.Adjustable;
        _gpuOcMemoryAdjustable = memoryRange.Adjustable;
        SyncGpuOverclockDependents();   // 初值来源与回读一致：开关 OFF ⇒ 数值行不可拖动
        // ---- 非提权时的诚实提示：超频需要管理员权限 + 显式「以管理员身份重启」入口 ----
        _gpuOcAdminRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiVisualStyle.Surface,
            Margin = new Padding(0, D(2), 0, 0),
            Visible = false,
        };
        _gpuOcAdminRow.Controls.Add(new Label
        {
            Text = Strings.OcNeedsAdmin,
            ForeColor = UiVisualStyle.Warn,
            AutoSize = true,
            Margin = new Padding(0, D(6), D(12), D(2)),
        });
        var ocAdminButton = new RButton
        {
            Name = "buttonRestartAsAdmin",
            Text = Strings.RestartAsAdmin,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 0),
        };
        ocAdminButton.Click += (_, _) => RestartAsAdmin();
        _gpuOcAdminRow.Controls.Add(ocAdminButton);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_gpuOcAdminRow, 0, tableRow++);

        // ---- 应用侧：风扇增强 / 屏幕刷新率 ----
        _fanBoostCombo = new RComboBox { Name = "fanBoostCombo", DropDownStyle = ComboBoxStyle.DropDownList };
        _fanBoostCombo.Items.AddRange(new object[] { Strings.ModeTuneUnchanged, Strings.ModeTuneOn, Strings.ModeTuneOff });
        _fanBoostCombo.SelectedIndex = 0;
        _fanBoostCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingModes || _fanBoostCombo.SelectedIndex < 0) return;
            bool? on = _fanBoostCombo.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            _ = CommitAsync(s => s with { FanBoost = on });
        };
        _fanBoostRow = AddComboRow(Strings.ModeTuneFanBoost, _fanBoostCombo);

        _refreshCombo = new RComboBox { Name = "refreshCombo", DropDownStyle = ComboBoxStyle.DropDownList };
        _refreshCombo.Items.Add(Strings.ModeTuneUnchanged);
        _refreshCombo.SelectedIndex = 0;
        _refreshCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingModes || _refreshCombo.SelectedIndex < 0) return;
            int? hz = _refreshCombo.SelectedItem is int value ? value : null;
            _ = CommitAsync(s => s with { RefreshHz = hz });
        };
        _refreshRow = AddComboRow(Strings.ModeTuneRefresh, _refreshCombo);

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(table, 0, rootRow++);

        // ---- 底部：恢复出厂 + 风扇曲线 ----
        var bottomFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiVisualStyle.Window,
        };
        _restoreButton = new RButton
        {
            Name = "buttonRestoreModeDetail",
            Text = Strings.PerfModeRestore,
            Width = D(120),
            Height = D(28),
            Margin = new Padding(0, 0, D(12), 0),
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        _restoreButton.Click += async (_, _) => await RestoreDefaultsAsync();
        bottomFlow.Controls.Add(_restoreButton);
        _fanCurveButton = new RButton
        {
            Name = "buttonFanCurve",
            Text = Strings.FanCurve,
            Width = D(96),
            Height = D(28),
            Margin = new Padding(0, 0, D(12), 0),
            BackColor = UiVisualStyle.SurfaceRaised,
            ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border,
            Cursor = Cursors.Hand,
        };
        _fanCurveButton.Click += async (_, _) => await OpenFanCurveAsync();
        bottomFlow.Controls.Add(_fanCurveButton);
        var cpuTuningButton = new RButton
        {
            Name = "buttonCpuTuning", Text = CpuTuningForm.Localized("CpuTuneTitle"),
            Width = D(110), Height = D(28), Margin = new Padding(0, 0, D(12), 0),
            BackColor = UiVisualStyle.SurfaceRaised, ForeColor = UiVisualStyle.Text,
            BorderColor = UiVisualStyle.Border, Cursor = Cursors.Hand,
        };
        cpuTuningButton.Click += (_, _) => { using var form = new CpuTuningForm(); form.ShowDialog(this); };
        bottomFlow.Controls.Add(cpuTuningButton);
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
                    Size.Empty, TextFormatFlags.GlyphOverhangPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        }
        // 余量 D(18)：CJK 标签按 GlyphOverhangPadding 渲染，比裸 MeasureText 宽 1–2px（审计 PL4 行差 1px）。
        int labelCol = labelMeasure + D(8) + D(18);
        int sliderCol = D(RSlider.MinTrackLogicalWidth);
        int valueCol = D(52) + D(30) + D(27);
        int paramNeeds = labelCol + sliderCol + valueCol;
        // 底行 / 模式行用控件自身的 PreferredSize（= AutoSize 实际渲染宽）：裸 MeasureText 少算 AutoSize 内边距。
        int bottomNeeds = _restoreButton.Width + _restoreButton.Margin.Horizontal
            + _fanCurveButton.Width + _fanCurveButton.Margin.Horizontal
            + cpuTuningButton.Width + cpuTuningButton.Margin.Horizontal + D(8);
        int modeNeeds = modeFlow.GetPreferredSize(Size.Empty).Width + D(8);
        _minContentWidth = Math.Max(paramNeeds, Math.Max(bottomNeeds, modeNeeds))
            + root.Padding.Horizontal + table.Padding.Horizontal + D(8);
        foreach (TableLayoutPanel paramRow in paramRows)
        {
            paramRow.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, labelCol);
            paramRow.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, valueCol);
        }
        _routeHint.MaximumSize = new Size(Math.Max(D(320), _minContentWidth - root.Padding.Horizontal), 0);
        MinimumSize = new Size(_minContentWidth + D(24), D(280));   // 外框最小宽留出窗框
        ClientSize = new Size(_minContentWidth, ClientSize.Height);

        // ---- 防抖发送：滑条拖动合并 400ms 后逐字段下发 + 独立判据 ----
        _debounce = new System.Windows.Forms.Timer { Interval = 400 };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await FlushPendingAsync();
        };

        PerfModeService? perf = PerfModeService.Instance;
        if (perf is not null)
        {
            perf.ModesChanged += OnPerfModesChanged;
            perf.ActiveChanged += OnPerfActiveChanged;
            perf.Applied += OnPerfApplied;
        }
        if (Program.hw is not null) Program.hw.CustomModeChanged += OnCustomChanged;
        MechrevoHw? subscribedHw = Program.hw;
        FormClosed += (_, _) =>
        {
            if (perf is not null)
            {
                perf.ModesChanged -= OnPerfModesChanged;
                perf.ActiveChanged -= OnPerfActiveChanged;
                perf.Applied -= OnPerfApplied;
            }
            if (subscribedHw is not null) subscribedHw.CustomModeChanged -= OnCustomChanged;
            _debounce.Stop();
            _debounce.Dispose();
            _powerWallTimer.Stop();
            _powerWallTimer.Dispose();
            _tips.Dispose();
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
            // 窗口隐藏后 arm 不再有意义：重开时以硬件回读为准。
            if (!Visible) _gpuOcArmed = false;
            // 审计模式下不采样：会引入随时间变化的文本，把截图比对搅乱。
            _powerWallTimer.Enabled = Visible && !Program.UiAuditMode;
            if (!Visible || Program.UiAuditMode) return;
            BindMode(PerfModeService.Instance?.DisplayedModeId ?? _modeId);
            ResizeToContent();
            await RefreshHardwareStateAsync();
        };

        RebuildModeItems();
        BindMode(perf?.DisplayedModeId ?? PerfModeStore.ActiveModeId);
        OnCustomChanged();
        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplySection(table);
        ResizeToContent();
    }

    // =====================================================================
    // 模式列表与绑定
    // =====================================================================

    /// <summary>下拉列表宽度 = 最长项文本宽度（避免文字被截断）。</summary>
    static void FitDropDown(RComboBox combo)
    {
        int maxW = 0;
        foreach (object? it in combo.Items)
            maxW = Math.Max(maxW, TextRenderer.MeasureText(combo.GetItemText(it) ?? "", combo.Font).Width);
        combo.DropDownWidth = Math.Max(combo.Width, maxW + 30);
    }

    static IReadOnlyList<PerfModeDefinition> ModesSnapshot() =>
        PerfModeService.Instance?.Modes ?? PerfModeStore.Load();

    static PerfModeDefinition? FindMode(string? id) =>
        id is null ? null : ModesSnapshot().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>这台机器提供哪些内置模式：静音狂暴/狂暴按能力门控，与主界面、托盘同一口径。</summary>
    internal static bool IsOffered(PerfModeDefinition mode, MechrevoHw? hw)
    {
        if (Program.UiAuditMode || hw is null) return true;
        return mode.Kind switch
        {
            PerfModeKind.SilentTurbo => hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported,
            PerfModeKind.Turbo => hw.Capabilities.TurboMode || (!hw.Capabilities.TurboModeVetoed && hw.FanStatusSeen),
            _ => true,
        };
    }

    /// <summary>当前编辑的模式 Id（主界面据此判断是否要切换编辑目标）。</summary>
    internal string EditingModeId => _modeId;

    void RebuildModeItems()
    {
        bool previous = _syncingModes;
        _syncingModes = true;
        try
        {
            _modeCombo.BeginUpdate();
            _modeCombo.Items.Clear();
            MechrevoHw? hw = Program.hw;
            foreach (PerfModeDefinition mode in ModesSnapshot())
                if (IsOffered(mode, hw) || string.Equals(mode.Id, _modeId, StringComparison.Ordinal))
                    _modeCombo.Items.Add(new ModeItem(mode.Id, PerfModeText.ComboText(mode)));
            SelectModeItem(_modeId);
            _modeCombo.EndUpdate();
            FitDropDown(_modeCombo);
        }
        finally { _syncingModes = previous; }
    }

    void SelectModeItem(string id)
    {
        for (int i = 0; i < _modeCombo.Items.Count; i++)
        {
            if (_modeCombo.Items[i] is ModeItem item && string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                if (_modeCombo.SelectedIndex != i) _modeCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// 把窗口绑定到一个模式：标题、下拉选中、改名/删除可用性、路线说明、应用侧下拉的值。
    /// 只刷新界面，不切换模式、不写任何东西。
    /// </summary>
    internal void BindMode(string id)
    {
        PerfModeDefinition? mode = FindMode(id) ?? ModesSnapshot().FirstOrDefault();
        if (mode is null) return;
        bool changed = !string.Equals(_modeId, mode.Id, StringComparison.Ordinal);
        _modeId = mode.Id;
        if (changed) _gpuOcArmed = false;

        bool previous = _syncingModes;
        _syncingModes = true;
        try
        {
            if (_modeCombo.Items.Cast<object>().All(o => o is not ModeItem item || item.Id != mode.Id))
                RebuildModeItems();
            SelectModeItem(mode.Id);
            Text = Strings.PerfEditorTitle + " · " + PerfModeText.Name(mode);
            IReadOnlyList<PerfModeDefinition> modes = ModesSnapshot();
            _addButton.Enabled = PerfModeCollection.CanAdd(modes);
            _renameButton.Enabled = mode.IsCustom;
            _deleteButton.Enabled = mode.IsCustom && PerfModeCollection.CanRemove(modes, mode.Id);
            if (!mode.IsCustom) EndRename();
            UpdateRouteHint(mode);
            LoadAppSide(mode.Settings);
        }
        finally { _syncingModes = previous; }
        if (IsHandleCreated) ResizeToContent();
    }

    void UpdateRouteHint(PerfModeDefinition mode)
    {
        int? slot = PerfModeService.Instance?.SlotOf(mode.Id);
        string text = mode.IsCustom
            ? slot is { } s ? string.Format(Strings.PerfModeHintCustom, s + 1) : Strings.PerfModeHintCustomUnassigned
            : mode.IsEmulatedBuiltIn
                ? string.Format(Strings.PerfModeHintEmulated, slot is { } e ? (e + 1).ToString() : "?")
                : Strings.PerfModeHintVendor;
        if (_routeHint.Text != text) _routeHint.Text = text;
    }

    /// <summary>应用侧下拉：第 0 项「不改变」= 该项为 null（沿用官方行为）。</summary>
    void LoadAppSide(PerfModeSettings s)
    {
        _powerModeCombo.SelectedIndex = s.WindowsPowerMode is { } p and >= 0 and <= 2 ? p + 1 : 0;
        SelectPlan(s.PowerPlanGuid);
        _boostCombo.SelectedIndex = s.CpuBoost is { } b && b + 1 < _boostCombo.Items.Count ? b + 1 : 0;
        _fanBoostCombo.SelectedIndex = s.FanBoost switch { true => 1, false => 2, _ => 0 };
        RebuildRefreshItems(s.RefreshHz);
    }

    void SelectPlan(string? guid)
    {
        bool previous = _syncingModes;
        _syncingModes = true;
        try
        {
            int index = 0;
            if (Guid.TryParse(guid, out Guid wanted))
            {
                for (int i = 1; i < _planCombo.Items.Count; i++)
                {
                    if (_planCombo.Items[i] is KeyValuePair<string, string> plan
                        && Guid.TryParse(plan.Value, out Guid g) && g == wanted)
                    {
                        index = i;
                        break;
                    }
                }
                if (index == 0)
                    index = _planCombo.Items.Add(new KeyValuePair<string, string>(Strings.PowerPlanUnavailable, wanted.ToString()));
            }
            _planCombo.SelectedIndex = index;
        }
        finally { _syncingModes = previous; }
    }

    void SelectPowerMode(int? power)
    {
        bool previous = _syncingModes;
        _syncingModes = true;
        try { _powerModeCombo.SelectedIndex = power is { } p and >= 0 and <= 2 ? p + 1 : 0; }
        finally { _syncingModes = previous; }
    }

    void RebuildRefreshItems(int? selectedHz)
    {
        bool previous = _syncingModes;
        _syncingModes = true;
        try
        {
            IReadOnlyList<int> rates = Program.hw?.HzList ?? Array.Empty<int>();
            var wanted = new List<object> { Strings.ModeTuneUnchanged };
            foreach (int hz in rates.Where(hz => hz > 0).Distinct().OrderBy(hz => hz)) wanted.Add(hz);
            // 保存过、但此刻屏幕不支持的刷新率照样列出（例如外接屏拔掉了），免得选中项凭空消失。
            if (selectedHz is > 0 && !wanted.OfType<int>().Contains(selectedHz.Value)) wanted.Add(selectedHz.Value);
            bool same = wanted.Count == _refreshCombo.Items.Count
                && wanted.Select((o, i) => Equals(o, _refreshCombo.Items[i])).All(x => x);
            if (!same)
            {
                _refreshCombo.BeginUpdate();
                _refreshCombo.Items.Clear();
                foreach (object o in wanted) _refreshCombo.Items.Add(o);
                _refreshCombo.EndUpdate();
            }
            int index = 0;
            if (selectedHz is > 0)
                for (int i = 1; i < _refreshCombo.Items.Count; i++)
                    if (_refreshCombo.Items[i] is int hz && hz == selectedHz.Value) { index = i; break; }
            _refreshCombo.SelectedIndex = index;
        }
        finally { _syncingModes = previous; }
    }

    // =====================================================================
    // 切换 / 新建 / 改名 / 删除
    // =====================================================================

    /// <summary>
    /// 用户在下拉里选了一个模式（或主界面「自定义」段）：先把还没发出去的改动写给原来的模式，
    /// 再绑定并切换过去（G-Helper 行为：选中即切换，编辑的永远是正在运行的模式）。
    /// </summary>
    internal async Task<bool> SelectModeAsync(string id)
    {
        int sequence = ++_modeSelectionSequence;
        PerfModeService.Instance?.CancelPendingApply();
        _debounce.Stop();
        await FlushPendingAsync(applyHardware: false);
        if (sequence != _modeSelectionSequence || IsDisposed) return false;
        BindMode(id);
        return await ActivateModeAsync(id);
    }

    /// <summary>切换到该模式并在状态行给出逐项结果。</summary>
    internal async Task<bool> ActivateModeAsync(string id)
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (Program.hw is not { IsConnected: true } || perf is null)
        {
            _status.Text = GcuDisconnectedText;
            _status.ForeColor = UiVisualStyle.Danger;
            return false;
        }
        _gpuOcArmed = false;
        _status.Text = SwitchPendingText;
        _status.ForeColor = UiVisualStyle.Warn;
        PerfApplyOutcome? outcome = await perf.ActivateAsync(id, "editor");
        if (IsDisposed) return false;
        // null = 被更新的请求取代：那次请求负责最终结果与文案。
        if (outcome is null) return false;
        ShowOutcome(outcome);
        return outcome.ModeSwitched;
    }

    async Task AddCustomModeAsync()
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is null) return;
        string seedName = FindMode(_modeId) is { } seed ? PerfModeText.Name(seed) : "";
        PerfModeDefinition? created = perf.AddCustom(_modeId);
        if (created is null)
        {
            _status.Text = string.Format(Strings.PerfModeLimitReached, PerfModeCollection.MaxCustomModes);
            _status.ForeColor = UiVisualStyle.Warn;
            return;
        }
        RebuildModeItems();
        _status.Text = string.Format(Strings.PerfModeCreated, PerfModeText.Name(created), seedName);
        _status.ForeColor = UiVisualStyle.Ok;
        await SelectModeAsync(created.Id);
    }

    void BeginRename()
    {
        if (FindMode(_modeId) is not { IsCustom: true } mode) return;
        _nameBox.Text = PerfModeText.Name(mode);
        _nameRow.Visible = true;
        ResizeToContent();
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    void EndRename()
    {
        if (!_nameRow.Visible) return;
        _nameRow.Visible = false;
        if (IsHandleCreated) ResizeToContent();
    }

    async Task SaveNameAsync()
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is null || FindMode(_modeId) is not { IsCustom: true } mode) { EndRename(); return; }
        string name = _nameBox.Text.Trim();
        if (name.Length > PerfModeCollection.MaxUserNameLength)
        {
            _status.Text = string.Format(Strings.PerfModeNameTooLong, PerfModeCollection.MaxUserNameLength);
            _status.ForeColor = UiVisualStyle.Danger;
            return;
        }
        perf.Rename(mode.Id, name.Length == 0 ? null : name);
        EndRename();
        BindMode(mode.Id);
        // 该模式正在运行时把名字同步给厂商屏显（切档 OSD 显示它，最多 12 字）。
        bool osd = await perf.PushSlotNameAsync(mode.Id);
        if (IsDisposed) return;
        _status.Text = osd || perf.ActiveModeId != mode.Id ? Strings.ProfileNameSaved : Strings.ProfileNameNotConfirmed;
        _status.ForeColor = osd || perf.ActiveModeId != mode.Id ? UiVisualStyle.Ok : UiVisualStyle.Warn;
    }

    async Task DeleteCustomModeAsync()
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is null || FindMode(_modeId) is not { IsCustom: true } mode) return;
        if (!PerfModeCollection.CanRemove(ModesSnapshot(), mode.Id))
        {
            _status.Text = Strings.PerfModeKeepOneCustom;
            _status.ForeColor = UiVisualStyle.Warn;
            return;
        }
        string name = PerfModeText.Name(mode);
        if (MessageBox.Show(this, string.Format(Strings.PerfModeDeletePrompt, name), "L-Mechrevo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _debounce.Stop();
        _pending.Clear();
        if (!perf.RemoveCustom(mode.Id)) return;
        RebuildModeItems();
        BindMode(perf.DisplayedModeId);
        _status.Text = string.Format(Strings.PerfModeDeleted, name);
        _status.ForeColor = UiVisualStyle.Ok;
        await Task.CompletedTask;
    }

    async Task RestoreDefaultsAsync()
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is null || Program.hw is not { IsConnected: true } || FindMode(_modeId) is not { } mode) return;
        string name = PerfModeText.Name(mode);
        if (MessageBox.Show(this, string.Format(Strings.PerfModeRestorePrompt, name), "L-Mechrevo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _debounce.Stop();
        _pending.Clear();
        _gpuOcArmed = false;
        _status.Text = Strings.Restoring;
        _status.ForeColor = UiVisualStyle.Muted;
        bool ok = await perf.ResetAsync(mode.Id);
        if (IsDisposed) return;
        BindMode(mode.Id);
        OnCustomChanged();
        _status.Text = string.Format(ok ? Strings.PerfModeRestoreDone : Strings.PerfModeRestoreFailed, name);
        _status.ForeColor = ok ? UiVisualStyle.Ok : UiVisualStyle.Danger;
    }

    /// <summary>
    /// 风扇曲线只在固件自定义档里生效（内置模式下写风扇表，风扇不跟随——真机实证）。
    /// 内置模式先转到自定义档（厂商出厂值补齐），再打开曲线编辑器。
    /// </summary>
    async Task OpenFanCurveAsync()
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is not null && FindMode(_modeId) is { Route: PerfModeRoute.BuiltIn } mode)
        {
            if (Program.hw is not { IsConnected: true })
            {
                _status.Text = GcuDisconnectedText;
                _status.ForeColor = UiVisualStyle.Danger;
                return;
            }
            _status.Text = string.Format(Strings.PerfModeFanCurveNeedsSlot, PerfModeText.Name(mode));
            _status.ForeColor = UiVisualStyle.Warn;
            PerfApplyOutcome? outcome = await perf.EnsureFirmwareSlotAsync(mode.Id);
            if (IsDisposed) return;
            if (perf.Find(mode.Id)?.Route != PerfModeRoute.FirmwareSlot)
            {
                _status.Text = Strings.PerfModeNoSlotSeed;
                _status.ForeColor = UiVisualStyle.Danger;
                return;
            }
            BindMode(mode.Id);
            if (outcome is not { ModeSwitched: true })
            {
                ShowOutcome(outcome);
                return;
            }
        }
        // 专用曲线编辑器（原版协议：16 点 duty 上下拖动实时保存）。只走贴边：左放不下就放到右边。
        var form = new FanCurveForm();
        AddOwnedForm(form);
        ResponsiveLayout.ShowAdjacentTo(form, this);
    }

    // =====================================================================
    // 下发与结果
    // =====================================================================

    /// <summary>应用侧项的一次改动（下拉切换即提交，不走防抖）。</summary>
    async Task CommitAsync(Func<PerfModeSettings, PerfModeSettings> edit)
    {
        PerfModeService? perf = PerfModeService.Instance;
        if (perf is null) return;
        string modeId = _modeId;
        _status.Text = Strings.ModeTuneApplying;
        _status.ForeColor = UiVisualStyle.Muted;
        PerfApplyOutcome? outcome = await perf.UpdateAsync(modeId, edit, "editor");
        if (IsDisposed) return;
        if (outcome is null)
        {
            bool running = string.Equals(perf.ActiveModeId, modeId, StringComparison.Ordinal);
            _status.Text = running ? "" : Strings.PerfModeSavedInactive;
            _status.ForeColor = UiVisualStyle.Muted;
            return;
        }
        ShowOutcome(outcome);
    }

    /// <summary>
    /// 状态行：切换失败 / 有失败项 / 逐项统计（已生效、已下发待验证、失败）。
    /// 「已下发」如实单列——拿不到硬件判据时绝不说成「已生效」。悬停看逐项明细。
    /// </summary>
    void ShowOutcome(PerfApplyOutcome? outcome)
    {
        if (outcome is null || IsDisposed) return;
        bool amd = Program.hw?.UsesAmdPowerFields == true;
        bool hasSwitch = outcome.Steps.Any(s => s.Step.Kind is PerfApplyStepKind.SwitchBuiltIn or PerfApplyStepKind.SwitchFirmwareSlot);
        string name = FindMode(outcome.ModeId) is { } m ? PerfModeText.Name(m) : "";
        if (hasSwitch && !outcome.ModeSwitched)
        {
            _status.Text = Strings.PerfModeSwitchFailed;
            _status.ForeColor = UiVisualStyle.Danger;
        }
        else if (outcome.AnyFailed)
        {
            _status.Text = string.Format(Strings.PerfModeOutcomeFailedItems, PerfModeText.FailedItems(outcome, amd));
            _status.ForeColor = UiVisualStyle.Danger;
        }
        else if (outcome.Steps.Count == 0)
        {
            return;
        }
        else
        {
            string summary = PerfModeText.Summary(outcome);
            _status.Text = hasSwitch ? string.Format(Strings.PerfModeSwitched, name) + " · " + summary : summary;
            _status.ForeColor = UiVisualStyle.Ok;
        }
        _tips.SetToolTip(_status, PerfModeText.Details(outcome, amd));
        ResizeToContent();
    }

    void OnPerfModesChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnPerfModesChanged); return; }
        RebuildModeItems();
        if (FindMode(_modeId) is { } mode)
        {
            bool previous = _syncingModes;
            _syncingModes = true;
            try
            {
                IReadOnlyList<PerfModeDefinition> modes = ModesSnapshot();
                _addButton.Enabled = PerfModeCollection.CanAdd(modes);
                _deleteButton.Enabled = mode.IsCustom && PerfModeCollection.CanRemove(modes, mode.Id);
                UpdateRouteHint(mode);
            }
            finally { _syncingModes = previous; }
        }
    }

    /// <summary>模式在别处变了（主界面、托盘、Fn 键）：编辑器跟着换到正在运行的模式。</summary>
    void OnPerfActiveChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnPerfActiveChanged); return; }
        if (PerfModeService.Instance is not { } perf || !Visible) return;
        string shown = perf.DisplayedModeId;
        if (!string.Equals(shown, _modeId, StringComparison.Ordinal) && _pending.Count == 0)
            BindMode(shown);
        else if (FindMode(_modeId) is { } mode)
            UpdateRouteHint(mode);
    }

    void OnPerfApplied(PerfApplyOutcome outcome)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnPerfApplied(outcome)); return; }
        if (!Visible || !string.Equals(outcome.ModeId, _modeId, StringComparison.Ordinal)) return;
        ShowOutcome(outcome);
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ResizeToContent();
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
        PowerWallVerdict verdict = HardwareControl.powerWall.Evaluate(DateTime.UtcNow);
        string text = verdict switch
        {
            PowerWallVerdict.Enforced or PowerWallVerdict.NotEnforced =>
                HardwareControl.powerWall.Describe(DateTime.UtcNow),
            _ => "",
        };
        if (_powerWallStatus.Text != text)
        {
            _powerWallStatus.Text = text;
            if (verdict == PowerWallVerdict.NotEnforced) Logger.WriteLine("PowerWall: " + text);
        }
        // 空文本时隐藏整个标签：可见但零宽的控件是布局缺陷。
        _powerWallStatus.Visible = text.Length > 0;
        SyncStatusRow();
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

    internal static bool IsOcCheckboxEnabled(MechrevoHw? hw, bool rangesAdjustable) =>
        hw is { GpuOverclockWritable: true, Capabilities.OverclockSettings: true } && rangesAdjustable;

    /// <summary>
    /// 把防抖合并的固件级改动交给编排层：落盘到当前模式，正在运行时只下发变化项并逐项判定。
    /// 没有编排层的宿主（单元测试）直接写当前运行的档——与编排层对「正在运行的模式」的写法一致。
    /// </summary>
    async Task FlushPendingAsync(bool applyHardware = true)
    {
        string modeId = _modeId;
        await _saveLock.WaitAsync();
        try
        {
            while (_pending.Count > 0 && string.Equals(modeId, _modeId, StringComparison.Ordinal))
            {
                var fields = new Dictionary<string, string>(_pending);
                _pending.Clear();
                if (PerfModeService.Instance is { } perf)
                {
                    // 零偏移的超频开启无法被驱动确认：把开关和当前的偏移一起下发；偏移都是 0 时只在界面上武装。
                    if (fields.TryGetValue("OverClockingSwitch", out string? oc) && oc == "1"
                        && !fields.ContainsKey("GpuCoreClockOffsetOC") && !fields.ContainsKey("GpuMemoryClockOffsetOC"))
                    {
                        if (_gpuOcCoreAdjustable) fields["GpuCoreClockOffsetOC"] = _coreOc.Value.ToString();
                        if (_gpuOcMemoryAdjustable) fields["GpuMemoryClockOffsetOC"] = _memOc.Value.ToString();
                        bool allZero = (!_gpuOcCoreAdjustable || _coreOc.Value == 0) && (!_gpuOcMemoryAdjustable || _memOc.Value == 0);
                        if (allZero)
                        {
                            _status.Text = Strings.PerfModeOcArmed;
                            _status.ForeColor = UiVisualStyle.Muted;
                            continue;
                        }
                    }
                    PerfApplyOutcome? outcome = await perf.UpdateAsync(modeId, s => ApplyFields(s, fields), "editor", applyHardware);
                    if (IsDisposed) return;
                    if (outcome is null)
                    {
                        if (!string.Equals(perf.ActiveModeId, _modeId, StringComparison.Ordinal))
                        {
                            _status.Text = Strings.PerfModeSavedInactive;
                            _status.ForeColor = UiVisualStyle.Muted;
                        }
                        continue;
                    }
                    ShowOutcome(outcome);
                }
                else if (applyHardware && Program.service is { } service)
                {
                    bool ok = await service.SetCustomDetail(fields);
                    _status.Text = ok ? Strings.ParamsConfirmed : Strings.ParamsReverted;
                    _status.ForeColor = ok ? UiVisualStyle.Ok : UiVisualStyle.Danger;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Performance mode update failed: " + ex.Message);
            _status.Text = Strings.ParamsSaveFailed;
            _status.ForeColor = UiVisualStyle.Danger;
        }
        finally
        {
            if (_pending.Count > 0) _debounce.Start();
            _saveLock.Release();
        }
    }

    /// <summary>编辑器字段（服务层键）→ 模式参数。未知键与非数字值忽略。</summary>
    internal static PerfModeSettings ApplyFields(PerfModeSettings settings, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fields);
        PerfModeSettings s = settings;
        foreach ((string key, string text) in fields)
        {
            if (!int.TryParse(text, out int v)) continue;
            s = key switch
            {
                "PL1" => s with { Pl1 = v },
                "PL2" => s with { Pl2 = v },
                "PL4" => s with { Pl4 = v },
                "CpuTccOffsetSwitch" => s with { TccOn = v == 1 },
                "CpuTccOffset" => s with { TccTarget = v },
                "GpuConfigurableTGPTarget" => s with { GpuTgp = v },
                "GpuDynamicBoostSwitch" => s with { GpuDynamicBoostOn = v == 1 },
                "GpuDynamicBoost" => s with { GpuDynamicBoost = v },
                "FanSwitchSpeedEnabled" => s with { FanSwitchSpeedOn = v == 1 },
                "FanSwitchSpeed" => s with { FanSwitchSpeedMs = v },
                "OverClockingSwitch" => s with { GpuOverclockOn = v == 1 },
                "GpuCoreClockOffsetOC" => s with { GpuCoreOffset = v },
                "GpuMemoryClockOffsetOC" => s with { GpuMemoryOffset = v },
                _ => s,
            };
        }
        return s;
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

    /// <summary>下拉项里的电源计划 GUID；「不改变」或非法项返回 null。</summary>
    internal static string? GetPowerPlanGuid(object? selectedItem)
    {
        if (selectedItem is not KeyValuePair<string, string> plan || !Guid.TryParse(plan.Value, out Guid guid))
            return null;
        return guid.ToString();
    }

    internal async Task<bool> RefreshHardwareStateAsync()
    {
        if (_refreshingHardwareState) return false;
        if (Program.hw is not { IsConnected: true } hw)
        {
            _status.Text = GcuDisconnectedText;
            _status.ForeColor = UiVisualStyle.Danger;
            return false;
        }
        _refreshingHardwareState = true;
        try
        {
            // NVAPI discovery can wake a sleeping dGPU and must not block the UI
            // thread. Retry whenever the form becomes visible so late driver/GPU
            // initialization does not permanently hide the OC controls.
            await Task.Run(hw.EnsureDirectGpuOverclock);
            bool tableOk = await hw.RequestCustomModePageStateAsync();
            if (!tableOk)
            {
                _status.Text = FanTableTimeoutText;
                _status.ForeColor = UiVisualStyle.Danger;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Custom mode status refresh failed: " + ex.Message);
            _status.Text = FanTableTimeoutText;
            _status.ForeColor = UiVisualStyle.Danger;
            return false;
        }
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

    internal static bool ApplyRange(RSlider bar, RNumericUpDown valueInput, int minimum, int maximum, int current,
        bool allowNegative = false)
    {
        var range = DeviceRange(minimum, maximum, current, allowNegative);
        // 两个控件先同时容纳新旧范围，ValueChanged 联动才不会写入仍受旧范围限制的控件。
        bar.Minimum = Math.Min(bar.Minimum, range.Min);
        bar.Maximum = Math.Max(bar.Maximum, range.Max);
        valueInput.Minimum = Math.Min(valueInput.Minimum, range.Min);
        valueInput.Maximum = Math.Max(valueInput.Maximum, range.Max);
        bar.Value = range.Value;
        valueInput.Value = range.Value;
        bar.Minimum = range.Min;
        bar.Maximum = range.Max;
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
        bool on = _ocChk.Checked && _ocChk.Enabled;
        _coreOc.Enabled = _coreOcVal.Enabled = on && _gpuOcCoreAdjustable;
        _memOc.Enabled = _memOcVal.Enabled = on && _gpuOcMemoryAdjustable;
    }

    /// <summary>测试接缝：非 null 时代替 ProcessHelper.RunAsAdmin（测试只记录，绝不提权重启）。</summary>
    internal static Action? RestartAsAdminOverride { get; set; }

    /// <summary>
    /// 「以管理员身份重启」入口：这是 owner 选定的唯一提权方式——由用户点这个显式动作，
    /// 由 UAC 在这一步征求同意；应用自身启动/写超频时绝不自动弹 UAC。
    /// 守卫与其它不可逆动作同源：必须确有新鲜真实输入（程序化/自动化调用一律拒绝）。
    /// </summary>
    void RestartAsAdmin()
    {
        if (!NativeMethods.HasFreshUserInput())
        {
            Logger.WriteLine("Restart-as-admin ignored: no fresh user input.");
            return;
        }
        Action? overrideAction = RestartAsAdminOverride;
        if (overrideAction is not null)
        {
            overrideAction();
            return;
        }
        Logger.WriteLine("Restart-as-admin requested by user (GPU overclock requires elevation).");
        ProcessHelper.RunAsAdmin();
    }

    int _customChangedQueued;   // 1 = 已有一次回显在 UI 队列里

    void OnCustomChanged()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                // Fan/Status 与 LCHWOC/Status 每帧都发这个事件；排队中的那次回显执行时读的就是
                // 最新值，后来的直接合并，避免一帧一遍重排十几个滑条。
                if (Interlocked.Exchange(ref _customChangedQueued, 1) != 0) return;
                BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _customChangedQueued, 0);
                    OnCustomChanged();
                });
                return;
            }
            var hw = Program.hw;
            if (hw is null) return;
            _syncing = true;
            try
            {
                ApplyRange(_pl1, _pl1Val, hw.Pl1Minimum, hw.Pl1Maximum, hw.Pl1);
                ApplyRange(_pl2, _pl2Val, hw.Pl2Minimum, hw.Pl2Maximum, hw.Pl2);
                ApplyRange(_pl4, _pl4Val, hw.Pl4Minimum, hw.Pl4Maximum, hw.Pl4);
                bool tccAdjustable = ApplyRange(_tcc, _tccVal, hw.TccMinimum, hw.TccMaximum, hw.TccTarget);
                bool tgpAdjustable = ApplyRange(_tgp, _tgpVal, hw.GpuTgpMinimum, hw.GpuTgpMaximum, hw.GpuTgp);
                bool dbAdjustable = ApplyRange(_db, _dbVal, hw.GpuDbMinimum, hw.GpuDbMaximum, hw.GpuDb);
                bool ocSupported = hw.SupportsGpuOverclock;
                // 直连 NVAPI 写入需要提权：非提权会话里可调范围能读到但写不动，
                // 此时不给可编辑控件，只显示「超频需要管理员权限」+ 重启入口。
                bool ocRequiresElevation = hw.GpuOverclockRequiresElevation;
                bool ocWritable = ocSupported && !ocRequiresElevation;
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
                _tccChk.Checked = hw.TccSwitch;
                _tccChk.Enabled = tccAdjustable;
                _tcc.Enabled = hw.TccSwitch && tccAdjustable;
                _ocChk.Checked = ocWritable && ocSwitchOn;
                _ocChk.Enabled = ocWritable && hw.Capabilities.OverclockSettings;
                if (_gpuOcAdminRow is not null) _gpuOcAdminRow.Visible = ocSupported && ocRequiresElevation;
                SyncGpuOverclockDependents();
                // 应用侧行的显隐：机型不支持的项整行隐藏（风扇增强 / 刷新率）。
                _fanBoostRow.Visible = Program.UiAuditMode || hw.SupportsFanBoost;
                _refreshRow.Visible = Program.UiAuditMode || hw.SupportsDisplayRefresh;
                if (_refreshRow.Visible && FindMode(_modeId) is { } mode)
                    RebuildRefreshItems(mode.Settings.RefreshHz);
                ResizeToContent();
            }
            finally { _syncing = false; }
        }
        catch (Exception ex) { Logger.WriteLine("Performance mode editor refresh failed: " + ex.Message); }
    }
}
