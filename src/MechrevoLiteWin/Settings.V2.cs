using MechrevoLite.Diagnostics;
using MechrevoLite.Hardware;
using MechrevoLite.Properties;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>
/// v2 组合行（预览 1:1）——分部实现：遥测行 / 刷新率分段同步 / 灯光组 / 设置弹窗。
/// 主装配在 Settings.BuildDashboardLayout。
/// </summary>
public partial class SettingsForm
{
    /// <summary>遥测行面板（性能行之下）：CPU/GPU 温度·功耗·转速·占空比，display timer 刷新。
    /// 形态对齐预览 .telemetry：单行「CPU 54°C 25W 2015rpm 35%  ·  GPU …」，温度亮色（Text）、
    /// 其余 Muted——一段一句两种颜色，WinForms Label 不支持富文本，拆成多段 Label 拼接。</summary>
    void BuildTelemetryRow()
    {
        int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
        _telemetryPanel = new BufferedPanel
        {
            Name = "panelTelemetry",
            Dock = DockStyle.Top,
            Height = D(18),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Window,
            Padding = new Padding(D(42), 0, 0, 0),
        };
        _telemetryCpuName = MakeTelemetryPiece("CPU");
        _telemetryCpuTemp = MakeTelemetryPiece("—", bright: true);
        _telemetryCpuRest = MakeTelemetryPiece("");
        _telemetrySeparator = MakeTelemetryPiece("·");
        _telemetryGpuName = MakeTelemetryPiece("GPU");
        _telemetryGpuTemp = MakeTelemetryPiece("—", bright: true);
        _telemetryGpuRest = MakeTelemetryPiece("");
        // 风扇数据段 = 固定占位槽（MinimumSize，与 DPI/审计缩放同源）+ 右对齐：
        // 文本右端与 · 的间距恒定，数据长短/缺失都不移动分隔点；槽内留白落在文本左侧
        //（真机：槽内留白若堆在右端，会把 · 推向一侧、整行看着不居中）。
        foreach (Label rest in new[] { _telemetryCpuRest, _telemetryGpuRest })
        {
            rest.TextAlign = ContentAlignment.MiddleRight;
            rest.MinimumSize = new Size(D(84), 0);
        }
        foreach (Label piece in new[]
        {
            _telemetryCpuName, _telemetryCpuTemp, _telemetryCpuRest, _telemetrySeparator,
            _telemetryGpuName, _telemetryGpuTemp, _telemetryGpuRest,
        })
        {
            // 尺寸一变就重排：位置是手算的，字体度量随 DPI/审计缩放变化后不重排会重叠
            //（审计 100pct 视口 sibling-overlap 实证）。ResumeLayout(true) 会再打 SizeChanged，
            // 同宽高必须跳过，否则每段 Label 宽度抖动会把整行打成布局风暴。
            int lastW = int.MinValue, lastH = int.MinValue;
            piece.SizeChanged += (_, _) =>
            {
                if (_telemetryLayingOut) return;
                int w = piece.Width, h = piece.Height;
                if (w == lastW && h == lastH) return;
                lastW = w;
                lastH = h;
                LayoutTelemetryPieces();
            };
            _telemetryPanel.Controls.Add(piece);
        }
        // 立即灌一次文本：空段（无功耗/转速）随即按占位槽布局——审计模式下传感器定时器不跑，
        // 只建不灌会让占位槽停留在默认尺寸。
        UpdateTelemetryText();
    }

    Label MakeTelemetryPiece(string text, bool bright = false)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = bright ? UiVisualStyle.Text : UiVisualStyle.Muted,
            BackColor = UiVisualStyle.Window,
            Font = new Font("Consolas", UiStyleCaption(), FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Location = Point.Empty,
        };
    }

    static float UiStyleCaption() => UiVisualStyle.TypeScale.Caption;

    /// <summary>遥测行文本（只在变化时写，避免每秒整页重绘）。</summary>
    internal void UpdateTelemetryText()
    {
        if (_telemetryCpuName is null || _telemetryGpuName is null || IsDisposed) return;
        var hw = Program.hw;
        (string _, string cpuTemp, string cpuRest) = TelemetryParts(hw, isCpu: true);
        (string _, string gpuTemp, string gpuRest) = TelemetryParts(hw, isCpu: false);
        bool changed = false;
        changed |= SetTelemetryPiece(_telemetryCpuTemp, cpuTemp);
        changed |= SetTelemetryPiece(_telemetryCpuRest, cpuRest);
        changed |= SetTelemetryPiece(_telemetryGpuTemp, gpuTemp);
        changed |= SetTelemetryPiece(_telemetryGpuRest, gpuRest);
        if (changed) LayoutTelemetryPieces();
    }

    static bool SetTelemetryPiece(Label? piece, string text)
    {
        if (piece is null || piece.Text == text) return false;
        piece.Text = text;
        return true;
    }

    /// <summary>各段按序排在一行并整体居中：词间距一个空格宽，· 两侧两个空格（预览 2100rpm&nbsp;&nbsp;·&nbsp;&nbsp;GPU）。
    /// 前进量取「首选宽与占位下限的较大者」——占位槽（MinimumSize）随 DPI/审计缩放同源缩放，
    /// 文本右对齐后 · 与右半区不随数据长短漂移。</summary>
    void LayoutTelemetryPieces()
    {
        if (_telemetryPanel is null || _telemetryCpuName is null || _telemetryCpuTemp is null ||
            _telemetryCpuRest is null || _telemetrySeparator is null ||
            _telemetryGpuName is null || _telemetryGpuTemp is null || _telemetryGpuRest is null) return;
        _telemetryLayingOut = true;
        _telemetryPanel.SuspendLayout();
        try
        {
            int spaceW = TextRenderer.MeasureText(" ", _telemetryCpuName.Font, new Size(1000, 100), TextFormatFlags.NoPadding).Width;
            int viewH = _telemetryPanel.ClientSize.Height;
            static int Advance(Label piece) => Math.Max(piece.PreferredWidth, piece.MinimumSize.Width);

            int total = Advance(_telemetryCpuName) + spaceW + Advance(_telemetryCpuTemp) + spaceW + Advance(_telemetryCpuRest)
                      + spaceW * 2 + Advance(_telemetrySeparator) + spaceW * 2
                      + Advance(_telemetryGpuName) + spaceW + Advance(_telemetryGpuTemp) + spaceW + Advance(_telemetryGpuRest);
            // 整行居中：WinForms 显式 Location 相对父容器客户区原点，**不受父容器 Padding 影响**
            //（实测：面板 Padding.Left=42 时 Location.X=0 的控件 Left 仍为 0），故按客户区宽度直接减半。
            int x = Math.Max(0, (_telemetryPanel.ClientSize.Width - total) / 2);
            void Place(Label piece, int gapBefore)
            {
                x += gapBefore;
                piece.Location = new Point(x, Math.Max(0, (viewH - piece.PreferredHeight) / 2));
                x += Advance(piece);
            }
            Place(_telemetryCpuName, 0);
            Place(_telemetryCpuTemp, spaceW);
            Place(_telemetryCpuRest, spaceW);
            Place(_telemetrySeparator, spaceW * 2);
            Place(_telemetryGpuName, spaceW * 2);
            Place(_telemetryGpuTemp, spaceW);
            Place(_telemetryGpuRest, spaceW);
        }
        finally
        {
            _telemetryPanel.ResumeLayout(true);
            _telemetryLayingOut = false;
        }
    }

    /// <summary>遥测三段：名称 / 温度（亮色）/ 其余（功耗·转速·占空比，muted）。</summary>
    static (string name, string temp, string rest) TelemetryParts(MechrevoHw? hw, bool isCpu)
    {
        if (hw is null) return (isCpu ? "CPU" : "GPU", "—", "");
        int temp = isCpu ? hw.CpuTemp : hw.GpuTemp;
        int rpm = isCpu ? hw.CpuFanRpm : hw.GpuFanRpm;
        int duty = isCpu ? hw.CpuFanDuty : hw.GpuFanDuty;
        float? power = isCpu ? HardwareControl.cpuPower : HardwareControl.gpuPower;
        string name = isCpu ? "CPU" : "GPU";
        var rest = new List<string>(3);
        if (power is > 0) rest.Add($"{(int)Math.Round(power.Value)}W");
        if (rpm > 0) rest.Add($"{rpm}rpm");
        if (duty > 0) rest.Add($"{(int)Math.Round((double)duty)}%");
        return (name, $"{temp}°C", string.Join(" ", rest));
    }

    /// <summary>
    /// 刷新率分段按钮重建与当前值点亮（代替旧 comboRefreshRate 下拉）。
    /// 签名守卫：列表/当前值没变就不重建（3 秒一次的画面刷新内不动控件树）。
    /// </summary>
        void SyncHzButtons()
        {
            if (_hzSegTable is null || _hzSegTable.IsDisposed) return;
            var list = Program.hw?.HzList ?? new List<int>();
        int current = Program.hw?.CurrentHz ?? 0;
        string signature = string.Join('|', list) + "#" + current;
        if (signature != _lastHzSignature)
        {
            _lastHzSignature = signature;
            _hzSegTable.SuspendLayout();
            _hzSegTable.Controls.Clear();
            _hzButtons.Clear();
            _hzSegTable.ColumnStyles.Clear();
            _hzSegTable.ColumnCount = Math.Max(1, list.Count);
            foreach (int hzValue in list)
            {
                int value = hzValue;
                var button = new RButton
                {
                    Text = hzValue.ToString(),
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty,
                    Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body),
                    Cursor = Cursors.Hand,
                    AccessibleName = hzValue + "Hz",
                };
                button.Click += async (_, _) =>
                {
                    if (_syncingDisplay || button.Activated) return;
                    _lastHzUi = DateTime.Now;
                    if (Program.service is not null && Program.hw is { IsConnected: true } && value != Program.hw.CurrentHz)
                    {
                        foreach (RButton b in _hzButtons) b.Enabled = false;
                        bool confirmed = await Program.service.SwitchRefreshRate(value);
                        if (confirmed)
                        {
                            foreach (RButton b in _hzButtons) b.Activated = b == button;
                        }
                        foreach (RButton b in _hzButtons) b.Enabled = true;
                    }
                };
                button.Activated = hzValue == current;
                _hzButtons.Add(button);
                _hzSegTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / Math.Max(1, list.Count)));
                _hzSegTable.Controls.Add(button, _hzButtons.Count - 1, 0);
            }
            // 分段外观：与性能/显卡分段同一套首/中/尾无缝皮肤（此前只有裸按钮，
            // 真机可见两个值之间的空旷区域没有任何分段边界）。
            UiVisualStyle.ApplySegmentGroup(_hzButtons.ToArray());
            _hzSegTable.ResumeLayout(true);
            SetScreenRowControls(1, _hzButtons.Count > 0, _hzSegTable);
        }
        else if (Program.hw is not null)
        {
            foreach (RButton button in _hzButtons)
                button.Activated = button.Text == Program.hw.CurrentHz.ToString();
        }
    }

    /// <summary>
    /// 灯光组内容（预览 1:1）：键盘/灯条/Logo 每灯一行 = [名称][电源开关][效果][编辑]。
    /// 电源开关复用快捷开关管线（SetLightPower + LightingSettingsStore.SavePower）；
    /// 键盘电源走 SetKeyboardPower。行可见性在 RefreshDeviceCapabilities 设置。
    /// </summary>
    void BuildLightRowsPanel()
    {
        int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
        var body = new BufferedTableLayoutPanel
        {
            Name = "lightRows",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(D(10), 0, D(4), D(4)),
            BackColor = UiVisualStyle.Window,
        };
        // 显式单列 Percent 样：无样式列按「子行首选宽」布局（窄视口下 345 > 346 客户宽，
        // r31/r33 审计实证），Percent 100 让每行吃满 padding 内的可用宽。
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        TableLayoutPanel MakeRow(string rowName)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1,
                Height = D(32), Margin = Padding.Empty, BackColor = UiVisualStyle.Window,
                Name = rowName,
            };
            // 列宽全走百分比（同读数区教训）：绝对列在审计的多重缩放里被算小，
            // 真机首验后 100pct 视口下 编辑键只剩 ~24px，'编辑' 两字被裁。
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));   // 灯名（预览 .sname 52px）
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 11));   // 电源开关
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));   // 效果（输入框样式）
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));   // 编辑
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(row, 0, body.RowCount - 1);
            return row;
        }

        void FillRow(TableLayoutPanel row, string name, out RCheckBox powerSwitch, out ComboBox effectCombo, Action click)
        {
            var nameLabel = new Label
            {
                Text = name, AutoSize = false, Dock = DockStyle.Fill,
                ForeColor = UiVisualStyle.Text, BackColor = UiVisualStyle.Window,
                Font = UiStyleBody(),
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            };
            powerSwitch = new RCheckBox
            {
                AutoSize = true, ForeColor = UiVisualStyle.Text,
                BackColor = UiVisualStyle.Window, Margin = Padding.Empty,
                Anchor = AnchorStyles.Left,   // 不锚 Top/Bottom：TableLayoutPanel 垂直居中
            };
            // 效果选择器：预览里的输入框形态（Input 底 + 1px 边框 + 右端 ▾），直接选效果。
            // Left|Right 锚定 = 横向撑满列 + 垂直居中（同开关的居中机制）。
            effectCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = "Key",
                FlatStyle = FlatStyle.Flat,
                ForeColor = UiVisualStyle.Text, BackColor = UiVisualStyle.Window,
                Font = UiStyleBody(),
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(D(8), D(3), D(8), D(3)),
            };
            var editBtn = new Button
            {
                Text = Strings.LightRowEdit, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                ForeColor = UiVisualStyle.Text, BackColor = UiVisualStyle.Window,
                FlatAppearance = { BorderColor = UiVisualStyle.Border, BorderSize = 1 },
                AutoSize = false, Dock = DockStyle.Fill,
                Font = UiStyleBody(), Margin = new Padding(D(2), D(3), 0, D(3)),
            };
            editBtn.Click += (_, _) => click();
            row.Controls.Add(nameLabel, 0, 0);
            row.Controls.Add(powerSwitch, 1, 0);
            row.Controls.Add(effectCombo, 2, 0);
            row.Controls.Add(editBtn, 3, 0);
        }

        // —— 键盘行 ——
        {
            var row = MakeRow("rowKeyboard");
            FillRow(row, "键盘", out var sw, out var effectCombo, () => OpenRgbForm());
            _kbPowerSw = sw; _kbEffectCombo = effectCombo;
            FillKeyboardEffectCombo(effectCombo);
            effectCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (_syncingEffectCombos || Program.UiAuditMode) return;
                int idx = effectCombo.SelectedIndex;
                if (idx < 0) return;
                if (Program.rgb is null) return;
                if (ShouldUseGcuKeyboardFallback(Program.rgb))
                {
                    var gcuCatalog = KeyboardFirmwareEffects.Visible(CurrentKeyboardType());
                    if (idx >= gcuCatalog.Length) return;
                    string id = gcuCatalog[idx].Id;
                    LightChannelSettings settings = LightingSettingsStore.Load(
                        MqttTopics.KeyboardCtrl, gcuCatalog[0].Id);
                    if (!sw.Checked)
                    {
                        LightingSettingsStore.Save(MqttTopics.KeyboardCtrl, settings with { Effect = id });
                        return;
                    }
                    if (Program.service is null || Program.hw is not { IsConnected: true }) return;
                    Color? color = LightingSettingsStore.ColorForEffect(id, settings.ColorArgb);
                    sw.Enabled = false;
                    bool ok = await Program.service.SetKeyboardEffect(
                        id, settings.Light, settings.Speed, "None", color, save: true);
                    sw.Enabled = true;
                    if (ok) LightingSettingsStore.Save(MqttTopics.KeyboardCtrl, settings with { Effect = id });
                    return;
                }
                var hidCatalog = RgbForm.VisibleHidEffects(CurrentKeyboardType());
                if (idx >= hidCatalog.Length) return;
                var hid = hidCatalog[idx];
                // 键盘行关态时只记住选择（KbHidMode 持久化在 KeyboardRgb 配置里），
                // 不点亮、不下发：与灯带/Logo 行的 persist-only 规则一致。开关打开时
                // 开关路径会 StartMode(rgb.KbHidMode)，选择照样生效。
                if (!sw.Checked)
                {
                    Program.rgb.KbHidMode = hid.Mode;
                    Program.rgb.QueueSaveConfig();
                    return;
                }
                Program.rgb.KbPowerOn = true;
                Program.rgb.KbHidMode = hid.Mode;
                Program.rgb.QueueSaveConfig();
                // 唯一接缝（防双发）：确定性「不支持」→ 不进入 HID 分支（效果选择交官方通道）；
                // Supported/Unknown 保持今天的阶梯（Unknown 视为支持）。
                if (Program.rgb.IsConnected && !ShouldUseGcuKeyboardFallback(Program.rgb))
                {
                    _ = Task.Run(() => Program.rgb.StartMode(hid.Mode));   // HID 帧写后台执行
                    return;
                }
                if (Program.service is not null && Program.hw is { IsConnected: true })
                {
                    // HID 未连时退回固件电源补开；效果名绝不用 HID 中文显示名。
                    sw.Enabled = false;
                    bool ok = await Program.service.SetKeyboardPower(true);
                    if (ok)
                        ok = await Program.service.SetKeyboardBrightnessPreservingEffect(
                            KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(Program.rgb.Brightness));
                    sw.Enabled = true;
                    if (!ok)
                    {
                        _syncingSwitches = true;
                        sw.Checked = false;
                        _syncingSwitches = false;
                        Program.rgb.KbPowerOn = false;
                    }
                }
            };
            sw.CheckedChanged += async (_, _) =>
            {
                if (_syncingSwitches) return;
                _lastQuickSwitchUi = DateTime.Now;
                bool requested = sw.Checked;
                int gen = ++_kbCmdGen;   // 代际：迟到的旧续体不得再启动 HID

                // 唯一接缝（防双发）：确定性「不支持」→ 本分支绝不触碰 HID（连接/旁路/StartMode 全部跳过），
                // 键盘可见光交给官方通道；Supported/Unknown 保持今天的阶梯（Unknown 视为支持）。
                bool gcuFallback = ShouldUseGcuKeyboardFallback(Program.rgb);

                // 键盘可见光由应用内 HID 渲染器掌控，固件通道只是电源旁路：两条都要动。
                // 只发固件命令时 HID 帧会继续写亮——用户报告的「开关不起作用」。
                Task? hidStop = null;
                if (Program.rgb is not null)
                {
                    Program.rgb.KbPowerOn = requested;
                    if (!requested)
                    {
                        var rgb = Program.rgb;
                        int stopGen = gen;   // 迟到的 OFF 不得停掉后来 ON 启动的新效果
                        hidStop = Task.Run(() =>
                            StopHidEffectForCurrentGeneration(stopGen, () => _kbCmdGen, rgb.StopCurrentEffect));
                    }
                    Program.rgb.QueueSaveConfig();
                }

                // HID 未连接时先取判定：判定未知则在后台惰性探测（Supported 时流保持打开，随即由下面的
                // HID 分支接续启动效果）；确定性「不支持」跳过连接，效果/电源走官方通道。
                // 审计/测试模式不碰真实 HID 设备（与效果下发处的 UiAuditMode 门控同一纪律）：
                // 测试机确实插着键盘灯 HID，若在这里真连接会走 HID 分支、绕过固件通道断言。
                if (requested && !gcuFallback && !Program.UiAuditMode && Program.rgb is { IsConnected: false } rgbToConnect)
                {
                    if (rgbToConnect.ControllerAvailability == FeatureAvailability.Unknown)
                        await rgbToConnect.EnsureHidReadyAsync();
                    // 判定到达：刷新键盘状态行（RefreshDeviceCapabilities 自带 InvokeRequired 守卫）。
                    if (rgbToConnect.ControllerAvailability != FeatureAvailability.Unknown)
                        RefreshDeviceCapabilities();
                    // 判定已达到 Supported（或探测瞬时 Unknown）而流仍未打开：保持今天的连接语义重连一次。
                    if (!rgbToConnect.IsConnected && rgbToConnect.ControllerAvailability != FeatureAvailability.Unsupported)
                        await Task.Run(() => rgbToConnect.Connect());
                }

                // 探测和 RefreshDeviceCapabilities 可能刚刚记下真实的亮度写入拒绝。
                // 用探测前的快照会把这次点击仍送进 HID。
                if (requested)
                    gcuFallback = ShouldUseGcuKeyboardFallback(Program.rgb);

                if (requested && !gcuFallback && Program.rgb is { IsConnected: true })
                {
                    // HID 直连时由渲染器接管：固件效果已退出帧模式，必须重进自定义帧模式再发帧。
                    // I2：固件电源旁路必须**并发**进行而非挡在出帧前面——本机固件键盘通道永不确认
                    //（SetLightPower 最坏 180+500+700+700 ≈ 2.08s），先 await 它会让开关慢好几倍。
                    // RgbForm 冷连接路径（Connect + StartMode，不发固件电源）证明固件电源不是出光前置条件。
                    var rgb = Program.rgb;
                    var svc = Program.service;
                    var hwd = Program.hw;
                    // 在 UI 线程就创建并登记旁路任务：OFF 路径的有界等待才看得到它
                    //（否则可能在 Task 尚未跑到赋值前读到 null 而漏等，迟到的补开电会覆盖 OFF）。
                    Task? bypass = (svc is not null && hwd is { IsConnected: true } && !hwd.KeyboardPower)
                        ? svc.SetKeyboardPower(true)     // 调用即开始发布，与出帧并发
                        : null;
                    _kbPowerBypassInFlight = bypass;
                    _ = Task.Run(async () =>
                    {
                        await RunHidWhileFirmwarePowerBypassCompletesAsync(bypass, () =>
                        {
                            if (gen != _kbCmdGen) return;    // 期间用户又切过：不启动旧代际的 HID
                            rgb.ReInitCustomMode();
                            rgb.StartMode(rgb.KbHidMode);
                        });
                        if (ReferenceEquals(_kbPowerBypassInFlight, bypass)) _kbPowerBypassInFlight = null;
                    });
                    return;
                }

                if (Program.service is null || Program.hw is not { IsConnected: true }) return;

                var service = Program.service;
                sw.Enabled = false;
                bool ok;
                if (!requested)
                {
                    if (_kbPowerBypassInFlight is { } pendingBypass)
                        await Task.WhenAny(pendingBypass, Task.Delay(250));   // 有界等待，避免迟到的补开电覆盖 OFF
                    ok = await service.SetKeyboardPower(false);
                }
                else
                {
                    // HID 未连（或确定性不支持）时退回固件通道（同 RgbForm 的电源补开路径）。
                    // 回退通道不承载任意 HID 效果，中文显示名也不是协议合法效果名：改用亮度载体——
                    // 重发 GCU 当前回报的效果 + 当前 UI 亮度档（设计 §4），效果保留、线上不再出现无效中文名。
                    ok = await service.SetKeyboardPower(true);
                    if (ok)
                        ok = await service.SetKeyboardBrightnessPreservingEffect(
                            KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(Program.rgb?.Brightness ?? 100));
                }
                sw.Enabled = true;
                if (!ok)
                {
                    _syncingSwitches = true; sw.Checked = !requested; _syncingSwitches = false;
                    // 复选框回滚后 HID 意图必须跟着回滚：失败的 OFF 要把渲染重新点起来，
                    // 否则开关显示「开」而灯是灭的（这里选择回滚时重新应用 ON）。
                    if (Program.rgb is not null)
                    {
                        Program.rgb.KbPowerOn = !requested;
                        if (!requested)
                        {
                            var rgb = Program.rgb;
                            if (rgb.IsConnected && !gcuFallback)
                                _ = Task.Run(async () =>
                                {
                                    hidStop?.Wait(600);   // 等 OFF 的清屏帧写完，避免清屏晚到覆盖重开的帧
                                    if (Program.service is not null && Program.hw is { IsConnected: true } && !Program.hw.KeyboardPower)
                                        await Program.service.SetKeyboardPower(true);
                                    rgb.ReInitCustomMode();
                                    rgb.StartMode(rgb.KbHidMode);
                                });
                        }
                    }
                }
            };
            body.Controls.Add(row, 0, 0);
        }
        // 灯条 / Logo 两行共用同一构造助手：唯一差异 = 通道（topic / 名称 / 效果目录）与行号。
        // 事件语义必须与原逐行块一致：控件存档字段在事件接线前赋值（构造期不触发），
        // 关通道改选只持久化；电源确认后按存档效果补发一次。
        void BuildLightChannelRow(string rowName, string label, string topic, string title,
            (string Effect, string Name)[] effects, int gridRow, Action<RCheckBox, ComboBox> storeControls)
        {
            var row = MakeRow(rowName);
            FillRow(row, label, out var sw, out var effectCombo, () => OpenLightForm(topic, title, effects));
            storeControls(sw, effectCombo);
            foreach (var (_, fxName) in effects)
                effectCombo.Items.Add(new KeyValuePair<string, string>(fxName, fxName));
            string saved = LightingSettingsStore.Load(topic, effects[0].Effect).Effect;
            int savedIdx = Array.FindIndex(effects, e => e.Effect == saved);
            effectCombo.SelectedIndex = savedIdx >= 0 ? savedIdx : 0;
            effectCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (_syncingEffectCombos || Program.UiAuditMode) return;
                int idx = effectCombo.SelectedIndex;
                if (idx < 0 || idx >= effects.Length) return;
                string effectId = effects[idx].Effect;
                var settings = LightingSettingsStore.Load(topic, effects[0].Effect);
                // 通道关闭时只记住选择：此时下发效果会先闪一次，等电源打开时固件又按上电
                // 默认效果初始化，用户看到的最终态就不是刚选的这个。
                if (!sw.Checked)
                {
                    LightingSettingsStore.Save(topic, settings with { Effect = effectId });
                    return;
                }
                if (Program.service is null || Program.hw is not { IsConnected: true }) return;
                sw.Enabled = false;
                bool ok = await Program.service.SetLightEffect(topic, effectId,
                    settings.Light, settings.Speed, "None",
                    LightingSettingsStore.ColorForEffect(effectId, settings.ColorArgb), save: true);
                sw.Enabled = true;
                if (ok) LightingSettingsStore.Save(topic, settings with { Effect = effectId });
            };
            sw.CheckedChanged += async (_, _) =>
            {
                if (_syncingSwitches) return;
                _lastQuickSwitchUi = DateTime.Now;
                if (Program.service is not null && Program.hw is { IsConnected: true })
                {
                    bool requested = sw.Checked;
                    sw.Enabled = false;
                    bool ok = await Program.service.SetLightPower(topic, requested);
                    sw.Enabled = true;
                    if (ok)
                    {
                        LightingSettingsStore.SavePower(topic, requested);
                        // 固件上电只显示默认的常亮；电源确认后按存档效果补发一次，
                        // 否则用户选的效果会丢。下发前再确认电源仍开着。
                        if (requested)
                        {
                            var applied = LightingSettingsStore.Load(topic, effects[0].Effect);
                            _ = Program.ApplyLightChannelEffectAsync(topic, applied,
                                shouldApply: () => LightingSettingsStore.Load(
                                    topic, effects[0].Effect).PowerOn);
                        }
                    }
                    else { _syncingSwitches = true; sw.Checked = !requested; _syncingSwitches = false; }
                }
            };
            body.Controls.Add(row, 0, gridRow);
        }

        // —— 灯条行 ——
        BuildLightChannelRow("rowLightbar", "灯条", MqttTopics.LightbarCtrl, "灯条灯效",
            LightForm.LightbarEffects, 1, (sw, effectCombo) => { _lbPowerSw = sw; _lbEffectCombo = effectCombo; });
        // —— Logo 行 ——
        BuildLightChannelRow("rowLogo", "Logo", MqttTopics.LogoLightCtrl, "Logo灯效",
            LightForm.LogoEffects, 2, (sw, effectCombo) => { _logoPowerSw = sw; _logoEffectCombo = effectCombo; });

        _lightGroup?.SetContent(body);

        // —— 内容首行（用户 2026-09-13 改址：两个全局灯光设置放「灯光」内容区顶部，不放组头——
        // 组头行放不下会截断摘要；放内容区还能随组折叠一起收起）——
        // 1) 离电自动关闭全部灯效：此前 lighting_off_on_battery 只有 Program.cs:1256 的读者、无任何 UI 写者。
        var offOnBatteryChk = new RCheckBox
        {
            Name = "checkLightingOffOnBattery",
            Text = Strings.LightingOffOnBattery,
            ForeColor = UiVisualStyle.Text,
            AutoSize = true,
            Checked = AppConfig.Is("lighting_off_on_battery"),
            Margin = new Padding(D(2), 0, 0, 0),
            Anchor = AnchorStyles.Left,   // 与行内其他控件一致：垂直居中
        };
        offOnBatteryChk.CheckedChanged += async (_, _) =>
        {
            if (_syncingLightHeader) return;
            AppConfig.Set("lighting_off_on_battery", offOnBatteryChk.Checked ? 1 : 0);
            await Program.ReconcileLightingPowerAsync();   // Program.cs:1256 实时读该键；同时把设备侧计时推回 0
        };
        _lightOffOnBatteryChk = offOnBatteryChk;

        // 2) 睡眠时间：选项表自 RgbForm 迁来；写 lighting_idle_seconds + CloseTimerMinutes 存档，
        //    设备侧计时必须保持 0（睡眠由应用内空闲检测实现），ReconcileLightingPowerAsync 会经
        //    Program.cs:1386-1387 推 SwitchCloseTimer(0)。
        var sleepCombo = new RComboBox
        {
            Name = "comboLightingSleepTimer",
            DropDownStyle = ComboBoxStyle.DropDownList,
            DropDownWidth = D(150),
            Margin = new Padding(D(6), D(3), 0, D(3)),   // 与左侧文字留间距；右边距 0 = 右缘与「编辑」列对齐
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        foreach (var (label, _, _) in LightingCloseTimerOptions) sleepCombo.Items.Add(label);
        int configuredIdleSeconds = AppConfig.Get("lighting_idle_seconds", Math.Max(0, Program.rgb?.CloseTimerMinutes ?? 0) * 60);
        int sleepIdx = Array.FindIndex(LightingCloseTimerOptions, o => o.IdleSeconds == configuredIdleSeconds);
        _syncingLightHeader = true;
        sleepCombo.SelectedIndex = sleepIdx >= 0 ? sleepIdx : 0;
        _syncingLightHeader = false;
        sleepCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (_syncingLightHeader) return;
            int idx = sleepCombo.SelectedIndex;
            if (idx < 0 || idx >= LightingCloseTimerOptions.Length) return;
            var option = LightingCloseTimerOptions[idx];
            AppConfig.Set("lighting_idle_seconds", option.IdleSeconds);
            if (Program.rgb is not null)
            {
                Program.rgb.CloseTimerMinutes = option.Mins;
                Program.rgb.SaveConfig();
            }
            await Program.ReconcileLightingPowerAsync();
        };
        _lightSleepCombo = sleepCombo;

        // 行几何沿用 MakeRow 的节奏（同高 D(32)、同 body 左 padding）；右列绝对宽 = 下拉右缘
        // 与 键盘/灯条/Logo 行「编辑」按钮的右缘同一条线。
        var prefsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1,
            Height = D(32), Margin = Padding.Empty, BackColor = UiVisualStyle.Window,
            Name = "rowLightPrefs",
        };
        prefsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 离电开关（左）
        prefsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(120)));   // 睡眠时间（右）
        prefsRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        prefsRow.Controls.Add(offOnBatteryChk, 0, 0);
        prefsRow.Controls.Add(sleepCombo, 1, 0);

        // 插到内容体最前：既有三行整体下移一行（行样式全为 AutoSize，顺序平移即可）。
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowCount = body.RowStyles.Count;
        foreach (var existing in body.Controls.OfType<Control>().ToArray())
            body.SetCellPosition(existing, new TableLayoutPanelCellPosition(
                0, body.GetCellPosition(existing).Row + 1));
        body.Controls.Add(prefsRow, 0, 0);

        // —— 键盘控制器状态行（设计 §5）：紧跟键盘行之后，判定为「不支持」时才显示；
        // Supported/Unknown 隐藏（AutoSize 行无可见子控件 → 整行收 0，现有用户布局不变）。
        // 直接落 body（不套行容器）：名称不得带 rowKeyboard 前缀——Controls.Find 是前缀匹配。
        // 高度走 Text Reflow（同 FirstRunGuideForm 描述行）：AutoSize 标签在 Percent 列里
        // 「布局高 ≠ 首选高」，body 实际高度会超出父容器首选高（beta17 审计 parent-overflow
        // +21 的根因）；关 AutoSize、宽度随列、高度按当前宽度换行测量回填，Resize/FontChanged
        // （含审计缩放）时重算，行高确定且随视口自适应。
        _lblKeyboardControllerStatus = new Label
        {
            Name = "labelKeyboardControllerStatus",
            Text = KeyboardControllerUnsupportedText,
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Visible = false,
            ForeColor = UiVisualStyle.Muted,
            BackColor = UiVisualStyle.Window,
            Font = UiStyleCaptionFont(),
            Margin = new Padding(D(2), 0, D(4), D(2)),
        };
        void ReflowKeyboardStatusHeight()
        {
            Label lbl = _lblKeyboardControllerStatus;
            // 测量口径与渲染一致（NoPadding + 8px 宽余量），高度再加 4px 渲染余量：
            // 少这 4px 文字底部会被裁（beta17 审计实测 needs 34 / available 30）。
            int width = Math.Max(1, lbl.ClientSize.Width - 8);
            int needed = TextRenderer.MeasureText(lbl.Text, lbl.Font,
                new Size(width, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
            lbl.Height = needed + 4;
        }
        _lblKeyboardControllerStatus.Resize += (_, _) => ReflowKeyboardStatusHeight();
        _lblKeyboardControllerStatus.FontChanged += (_, _) => ReflowKeyboardStatusHeight();
        Control keyboardRow = body.Controls.OfType<Control>().First(c => c.Name == "rowKeyboard");
        int keyboardRowIndex = body.GetCellPosition(keyboardRow).Row;
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowCount = body.RowStyles.Count;
        foreach (var existing in body.Controls.OfType<Control>().ToArray())
        {
            int rowIndex = body.GetCellPosition(existing).Row;
            if (rowIndex > keyboardRowIndex)
                body.SetCellPosition(existing, new TableLayoutPanelCellPosition(0, rowIndex + 1));
        }
        body.Controls.Add(_lblKeyboardControllerStatus, 0, keyboardRowIndex + 1);
    }

    /// <summary>判定为「不支持」且官方服务在线时的状态文案（设计 §5，UI 语言 = 中文）。</summary>
        internal static string KeyboardControllerUnsupportedText => Strings.KeyboardControllerUnsupported;

    /// <summary>
    /// 官方 GCU 服务未运行时的状态文案。本进程不请求管理员权限，也不能替安装器启动 GCUBridge，
    /// 所以这里只说明服务没在跑，不假装已经切到官方通道。
    /// </summary>
        internal static string KeyboardGcuServiceNotRunningText => Strings.KeyboardGcuNotRunning;

    internal static string KeyboardControllerStatusText(bool serviceConnected) =>
        serviceConnected ? KeyboardControllerUnsupportedText : KeyboardGcuServiceNotRunningText;

    /// <summary>睡眠时间选项（label / 设备侧分钟 / 应用侧空闲秒）：自 RgbForm.cs 迁来（2026-09-13）。
    /// 设备侧计时恒为 0——睡眠由应用内空闲检测实现（RgbForm.SyncDeviceCloseTimerAsync 同一纪律）。</summary>
    internal static readonly (string Label, int Mins, int IdleSeconds)[] LightingCloseTimerOptions =
    {
        ("关闭", 0, 0), ("10 秒（软件）", 0, 10), ("10 分钟", 10, 600), ("15 分钟", 15, 900),
        ("20 分钟", 20, 1200), ("30 分钟", 30, 1800), ("45 分钟", 45, 2700),
        ("1 小时", 60, 3600), ("2 小时", 120, 7200),
    };

    static Font UiStyleCaptionFont() => UiVisualStyle.Font(UiStyleCaption());
    static Font UiStyleBody() => UiVisualStyle.Font(UiVisualStyle.TypeScale.Body);

    /// <summary>灯光组同步：开关回显（键盘 = 应用内 HID 意图 KbPowerOn，无渲染器时退回 KeyboardPower；
    /// 灯条/Logo = QuickSwitches）与键盘效果下拉回显。</summary>
    /// <summary>
    /// I2/I3：先启动 HID 出帧（快），再等固件电源旁路收敛，落地后**重申**一次自定义帧模式。
    /// 固件键盘通道在本机永不确认，把旁路 await 放在出帧之前会让开关慢到 ~2 秒，故先出帧；
    /// 但 SetKeyboardPower(true) 落地会把控制器踢出 ITE 自定义帧模式，先出的帧随之被静默忽略
    /// （灯亮约 1~2 秒后常暗），所以固件落地后必须重进自定义帧模式 + 重启效果。
    /// 旁路任务失败也不得吞掉这次重申；异常记日志后照常重申。抽成静态内部方法以便单测锁定契约。
    /// </summary>
    internal static async Task RunHidWhileFirmwarePowerBypassCompletesAsync(Task? firmwarePowerBypass, Action startHid)
    {
        startHid();
        if (firmwarePowerBypass is null) return;
        try
        {
            await firmwarePowerBypass;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"RGB 固件电源旁路失败，仍重申自定义帧模式: {ex.Message}");
        }
        Logger.WriteLine("RGB 固件电源落地，重申自定义帧模式");
        startHid();
    }

    /// <summary>
    /// OFF 的 HID 停止同样按代际守卫：_kbCmdGen 已前进说明用户在 OFF 之后又切了一次（例如马上又开），
    /// 迟到的 StopCurrentEffect 会停止并 blank 清屏，清掉新代际刚启动的帧——此时必须放弃这次停止。
    /// 抽成静态内部方法以便单测锁定语义。
    /// </summary>
    internal static void StopHidEffectForCurrentGeneration(int stopGeneration, Func<int> currentGeneration, Action stopHid)
    {
        if (stopGeneration == currentGeneration()) stopHid();
    }

    /// <summary>
    /// 唯一接缝（防双发）：当前运行态下键盘可见光是否只能走官方（GCU）通道。
    /// 纯策略吃 (判定, HID 连接态, GCU 可用, HID 亮度写入是否生效)；调用方一律独占提前返回，绝不穿透。
    /// 亮度标志只采信已连接且判定为 Supported 的真实写入；未连接或 Unknown 保持 true，不跳过探测。
    /// </summary>
    static bool ShouldUseGcuKeyboardFallback(KeyboardRgb? rgb) =>
        rgb is not null && KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            rgb.ControllerAvailability, rgb.IsConnected,
            Program.service is not null && Program.hw is { IsConnected: true },
            rgb.BrightnessWriteTookEffectForRouting());

    static int CurrentKeyboardType() => Program.hw?.Capabilities.KeyboardType ?? 0;

    void FillKeyboardEffectCombo(ComboBox combo)
    {
        bool gcu = ShouldUseGcuKeyboardFallback(Program.rgb);
        int keyboardType = CurrentKeyboardType();
        var gcuCatalog = KeyboardFirmwareEffects.Visible(keyboardType);
        var hidCatalog = RgbForm.VisibleHidEffects(keyboardType);
        string[] labels = gcu
            ? gcuCatalog.Select(e => e.Label).ToArray()
            : hidCatalog.Select(e => e.Name).ToArray();
        int idx;
        if (gcu)
        {
            LightChannelSettings settings = LightingSettingsStore.Load(
                MqttTopics.KeyboardCtrl, gcuCatalog[0].Id);
            idx = Array.FindIndex(gcuCatalog, e => e.Id == settings.Effect);
        }
        else
        {
            int mode = Program.rgb?.KbHidMode ?? -1;
            idx = mode >= 0 ? Array.FindIndex(hidCatalog, e => e.Mode == mode) : -1;
        }
        if (idx < 0) idx = 0;

        bool sameCatalog = combo.Items.Count == labels.Length;
        if (sameCatalog)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                if (combo.GetItemText(combo.Items[i]) != labels[i]) { sameCatalog = false; break; }
            }
        }
        if (sameCatalog && combo.SelectedIndex == idx) return;

        _syncingEffectCombos = true;
        if (!sameCatalog)
        {
            combo.Items.Clear();
            foreach (string label in labels)
                combo.Items.Add(new KeyValuePair<string, int>(label, 0));
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = Math.Clamp(idx, 0, combo.Items.Count - 1);
        _syncingEffectCombos = false;
    }

    void SyncLightRows()
    {
        var hw = Program.hw;
        // 开关语义 = 设备此刻是否亮着（不是「用户曾启用过」）。临时熄灯（空闲休眠/离电关灯）
        // 期间三条通道都是暗的，三个开关必须一起回落到关；恢复后一起回到开。
        bool temporarilySuspended = Program.IsLightingTemporarilySuspended;
        if (_kbPowerSw is not null && !IsDisposed)
        {
            // 键盘开关回显跟随 HID 渲染器的意图：固件 KeyboardPower 在 HID 接管后不是可见光的真相。
            bool state = LightingState.IsLightSwitchOn(
                Program.rgb?.KbPowerOn ?? (hw?.KeyboardPower ?? true), temporarilySuspended);
            if (_kbPowerSw.Checked != state) { _syncingSwitches = true; _kbPowerSw.Checked = state; _syncingSwitches = false; }
            if (_kbEffectCombo is not null)
                FillKeyboardEffectCombo(_kbEffectCombo);
        }
        if (_lbPowerSw is not null && hw is not null && hw.QuickSwitches.TryGetValue("lightbar", out bool lb))
        {
            bool state = LightingState.IsLightSwitchOn(lb, temporarilySuspended);
            if (_lbPowerSw.Checked != state) { _syncingSwitches = true; _lbPowerSw.Checked = state; _syncingSwitches = false; }
        }
        if (_logoPowerSw is not null && hw is not null && hw.QuickSwitches.TryGetValue("logolight", out bool logo))
        {
            bool state = LightingState.IsLightSwitchOn(logo, temporarilySuspended);
            if (_logoPowerSw.Checked != state) { _syncingSwitches = true; _logoPowerSw.Checked = state; _syncingSwitches = false; }
        }
        if (_lblKeyboardControllerStatus is not null && !IsDisposed)
        {
            bool serviceConnected = Program.service is not null && hw is { IsConnected: true };
            string statusText = KeyboardControllerStatusText(serviceConnected);
            if (_lblKeyboardControllerStatus.Text != statusText)
                _lblKeyboardControllerStatus.Text = statusText;
        }
        if (_lightOffOnBatteryChk is not null && !IsDisposed)
        {
            // 组头两控件的回显：跟随持久化配置（离电开关无硬件回读，睡眠时间读 AppConfig）。
            bool offOnBattery = AppConfig.Is("lighting_off_on_battery");
            if (_lightOffOnBatteryChk.Checked != offOnBattery)
            { _syncingLightHeader = true; _lightOffOnBatteryChk.Checked = offOnBattery; _syncingLightHeader = false; }
            int idleSeconds = AppConfig.Get("lighting_idle_seconds", Math.Max(0, Program.rgb?.CloseTimerMinutes ?? 0) * 60);
            int sleepIdx = Array.FindIndex(LightingCloseTimerOptions, o => o.IdleSeconds == idleSeconds);
            if (sleepIdx >= 0 && _lightSleepCombo is not null && _lightSleepCombo.SelectedIndex != sleepIdx)
            { _syncingLightHeader = true; _lightSleepCombo.SelectedIndex = sleepIdx; _syncingLightHeader = false; }
        }
        UpdateLightSummary();
    }

    /// <summary>灯光组摘要（预览「3 通道 · 2 开启」）：通道 = 支持的灯数，开启 = 通电的灯数。</summary>
    void UpdateLightSummary()
    {
        if (_lightGroup is null) return;
        int channels = (_lightChannelKeyboard ? 1 : 0) + (_lightChannelLightbar ? 1 : 0) + (_lightChannelLogo ? 1 : 0);
        int on = 0;
        if (_lightChannelKeyboard && _kbPowerSw?.Checked == true) on++;
        if (_lightChannelLightbar && _lbPowerSw?.Checked == true) on++;
        if (_lightChannelLogo && _logoPowerSw?.Checked == true) on++;
        _lightGroup.Summary = $"{channels} 通道 · {on} 开启";
    }

    /// <summary>液冷折叠组摘要：完整状态的一句话压缩版（去掉设备号/固件号尾巴）。
    /// 连通时带档位后缀（预览「已连接 · 泵自动 · 风扇自动」）；2026-09-14 用户要求
    /// 手动档也显示（「已连接 · 泵60% · 风扇自动」），最高档读「最大」，与下拉同表同措辞。</summary>
    internal static string LcSummaryText(string full)
    {
        if (full.StartsWith("GCU 已连接") || full.StartsWith("已直连"))
        {
            var parts = new List<string>(3) { "已连接" };
            AddLcGearPart(parts, "泵", "lc_pump_profile", LiquidCoolingDisplayPolicy.PumpGearLabels,
                Program.ble?.SavedPumpProfile, WaterCoolerBle.TopPumpProfile);
            AddLcGearPart(parts, "风扇", "lc_fan_profile", LiquidCoolingDisplayPolicy.FanGearLabels,
                Program.ble?.SavedFanProfile, WaterCoolerBle.TopFanProfile);
            return string.Join(" · ", parts);
        }
        if (full.StartsWith("蓝牙已连接")) return "蓝牙待接管";
        if (full.StartsWith("GCU 报告流量异常")) return "流量异常";
        if (full.StartsWith("未连接")) return "未连接";
        return full;
    }

    /// <summary>摘要的单通道档位段：BLE 直连通道存 WaterCoolerBle 记忆档，GCU 通道落 AppConfig；
    /// 两者取同一归一化结果（NormalizeProfile 把越界值钳回 ProfileUnset）。</summary>
    static void AddLcGearPart(List<string> parts, string channelPrefix, string configKey,
        string[] gearLabels, int? bleProfile, int topProfile)
    {
        int stored = bleProfile ?? WaterCoolerBle.NormalizeProfile(
            AppConfig.Get(configKey, WaterCoolerBle.ProfileUnset), topProfile);
        string? label = LiquidCoolingDisplayPolicy.GearSummaryLabel(channelPrefix, gearLabels, stored, topProfile);
        if (label is not null) parts.Add(label);
    }

    /// <summary>当前色域档（屏幕校色下拉回显）：优先同帧上报（CurrentColorCalibration），
    /// 否则读注册表；原版协议 1=默认 2=sRGB（P3/AdobeRGB 已删，见 Settings.cs 行头下拉）。</summary>
    internal static int ResolveColorCalibrationMode() => Program.hw?.ColorCalibrationModeSeen == true
        ? Program.hw.ColorCalibrationMode
        : MechrevoService.GetColorCalibrationMode();

    /// <summary>色域档 → 下拉索引：2=sRGB，其余（含已删除的 3/4 旧档）回落 默认。</summary>
    internal static int CalibComboIndexFor(int mode) => mode == 2 ? 1 : 0;

    /// <summary>自绘滚动：把滚动条值应用到内容偏移；rangeChanged 时先按当前内容/视口重算范围。</summary>
    void UpdateDashboardScroll(bool rangeChanged = true)
    {
        if (_dashboardScroll is null || _dashboardStack is null || _dashboardScrollBar is null) return;
        if (rangeChanged)
        {
            // 滚动量取「AutoSize 高度」与「控件树真实内容底边」的较大者：快捷开关展开后
            // 折叠组的 AutoSize 不会总把内容高度交给 _dashboardStack，只按 stack.Height
            // 设范围会得到 0 滚动量——末尾开关被固定底栏盖住且滚不上来（run6 F2）。
            // 只放大滚动量、不改内容布局，避免引入新的行溢出。
            int content = Math.Max(_dashboardStack.Height,
                DeepestContentBottom(_dashboardStack) + _dashboardStack.Padding.Bottom);
            _dashboardScrollBar.SetRange(content, _dashboardScroll.ClientSize.Height);
        }
        _dashboardStack.Location = new Point(_dashboardStack.Left, -_dashboardScrollBar.Value);
    }

    /// <summary>
    /// 控件树的真实内容底边（相对 <paramref name="root"/> 坐标）。中间容器的 AutoSize 欠量时，
    /// 子控件的实际位置仍然有效，逐层取「child.Bottom」与「child.Top + 子树底边」的最大值，
    /// 就能拿到内容真正延伸到哪一行。
    /// </summary>
    static int DeepestContentBottom(Control root)
    {
        int bottom = 0;
        foreach (Control child in root.Controls)
        {
            if (!child.Visible) continue;
            bottom = Math.Max(bottom, child.Bottom);
            int nested = DeepestContentBottom(child);
            if (nested > 0) bottom = Math.Max(bottom, child.Top + nested);
        }
        return bottom;
    }

    /// <summary>滚轮路由：内容视口改自绘滚动（AutoScroll 关闭）后，滚轮要手动转发到面板。
    /// 指针下若是自带滚轮语义的控件（下拉/滑条），不拦——让它们照旧处理。</summary>
    sealed class DashboardWheelFilter : IMessageFilter
    {
        readonly SettingsForm _form;
        public DashboardWheelFilter(SettingsForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (_form.IsDisposed || !_form.IsHandleCreated) return false;
            BufferedPanel? scroll = _form._dashboardScroll;
            RScrollBar? bar = _form._dashboardScrollBar;
            if (scroll is null || bar is null || !bar.Visible || !scroll.IsHandleCreated) return false;

            int x = (short)((long)m.LParam & 0xFFFF);
            int y = (short)(((long)m.LParam >> 16) & 0xFFFF);
            var screenPoint = new Point(x, y);
            if (!scroll.RectangleToScreen(scroll.ClientRectangle).Contains(screenPoint)) return false;

            // 指针下的控件是否自带滚轮语义（LC 下拉 / 亮度滑条）：有就让给它们。
            Control? hit = scroll.GetChildAtPoint(scroll.PointToClient(screenPoint),
                GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled);
            for (Control? walk = hit; walk is not null && walk != scroll; walk = walk.Parent)
            {
                if (walk is ComboBox || walk is RSlider) return false;
            }

            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            if (delta == 0) return false;
            bar.Value -= Math.Sign(delta) * bar.Step;
            return true;
        }
    }

    /// <summary>footer ⚙：懒建设置弹窗并贴边显示（模式同 CustomModeForm）。</summary>
    void OpenSettingsDialog()
    {
        if ((_settingsDialog is null || _settingsDialog.IsDisposed) && _themeModePanel is not null)
        {
            // 用户 2026-09-13：弹窗里的「悬浮窗」开关与 footer 键重复，整体移除（footer 路径不动）。
            // 隔离官方控制台已移除：首次引导已要求彻底卸载官方控制台，不再提供隔离/恢复。
            _settingsDialog = new SettingsDialog(
                _themeModePanel, _overdriveChk, _overdriveAvailable);
            AddOwnedForm(_settingsDialog);
        }
        if (_settingsDialog is not null)
        {
            if (_settingsDialog.Visible) { _settingsDialog.Close(); return; }
            _settingsDialog.ShowAdjacentTo(this);
        }
    }

    /// <summary>
    /// footer 重排（预览 1:1）：版本号(V2 版本 labelVersion 迁到 footer) + 五枚图标行尾键：
    /// ⊙悬浮窗 ⚙设置 ↻更新 ❤赞助 ✕退出（纯图标 + tooltip；可访问名保留）。Surface 底 + 顶部 1px Border。
    /// </summary>
    void BuildFooterV2(Func<int, int> scale)
    {
        panelFooter.AutoSize = false;
        panelFooter.Padding = new Padding(scale(12), scale(6), scale(10), scale(4));
        panelFooter.Height = scale(51);
        panelFooter.Controls.Clear();

        var topLine = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = UiVisualStyle.Border,
            Margin = Padding.Empty,
        };

        // 版本号进门列（预览：左端 beta13.x muted，右侧五个图标键均分）。
        // 不能再用「labelVersion 绝对定位」——它会浮压在第一个按钮上（真机首验）。
        labelVersion.AutoSize = true;
        labelVersion.Dock = DockStyle.Fill;
        labelVersion.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
        labelVersion.ForeColor = UiVisualStyle.Muted;

        // 按钮布局用等列 TLP；按钮 Dock=Fill，尺寸由容器在任何缩放下推导，杜绝越界。
        var host = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(scale(4), 2, 0, 1),
            BackColor = panelFooter.BackColor,
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        for (int i = 0; i < 6; i++) host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78 / 6f));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(labelVersion, 0, 0);

        // 注意：System.Window.Forms Button 的 ForeColor 只影响文本

        // 退出按钮复用既有 handler（ButtonQuit_Click 关闭到托盘还是退出由其内部处理）；
        // 更新/赞助/悬浮窗各自有既有 Click 绑定的原按钮，为保接线，原按钮改造成图标形态后保留。
        // 图标按预览 footer 逐一对应：✕退出 ❤赞助 ↻更新 ⊙悬浮窗（旧映射 Console/Battery/Gauge 皆非预览图形）。
        ResetLegacyFooterButton(buttonQuit, Strings.Quit, UiGlyph.Kind.Close, scale);
        ResetLegacyFooterButton(buttonDonate, Strings.Donate, UiGlyph.Kind.Heart, scale);
        ResetLegacyFooterButton(buttonUpdates, Strings.Updates, UiGlyph.Kind.Refresh, scale);
        ResetLegacyFooterButton(buttonOverlay, Strings.FooterOverlay, UiGlyph.Kind.Overlay, scale);
        // 更新键的 Click 已在构造函数（Settings.cs）绑定；此处再绑一次会让一次点击先后弹出两个
        // 模态更新窗口（关掉第一个后第二个才出现）——用户报告的「更新窗口要关两次」。不要在此重复接线。

        // 与四个旧键同一造型来源（StyleFooterGhostButton）：此前这里手写第二份字面量，
        // FlatAppearance 底/圆角与旧键各自漂移。
        var settingsButton = new RButton
        {
            Text = Strings.FooterSettings,
            Cursor = Cursors.Hand,
            AutoSize = false,
            Size = new Size(scale(46), scale(36)),
            Anchor = AnchorStyles.None,   // 与其余键一致：单元格内垂直居中（缺省 Top|Left 会整体高 4px）
            BackColor = panelFooter.BackColor,
        };
        UiVisualStyle.StyleFooterGhostButton(settingsButton);
        UiVisualStyle.ApplyFooterGlyph(settingsButton, UiGlyph.Kind.Gear, scale(16));
        settingsButton.Click += (_, _) => OpenSettingsDialog();

        // 诊断包入口（footer 幽灵键，与其余键同形态）：导出到用户选择的 zip。
        // 它是 panelFooter 的后代——UiAuditRunner.CheckFooterOcclusion 明确跳过底栏自身控件，
        // 因此不会被固定底栏遮挡，真实点击必有可点中中心点。
        var diagnosticButton = new RButton
        {
            Text = Strings.FooterDiagnostics,
            Cursor = Cursors.Hand,
            AutoSize = false,
            Size = new Size(scale(46), scale(36)),
            Anchor = AnchorStyles.None,
            BackColor = panelFooter.BackColor,
            AccessibleName = Strings.ExportDiagnostics,
        };
        UiVisualStyle.StyleFooterGhostButton(diagnosticButton);
        UiVisualStyle.ApplyFooterGlyph(diagnosticButton, UiGlyph.Kind.Package, scale(16));
        diagnosticButton.Click += async (_, _) => await DiagnosticPackCommand.RunAsync(this, diagnosticButton);

        host.Controls.Add(buttonOverlay, 1, 0);
        host.Controls.Add(settingsButton, 2, 0);
        host.Controls.Add(buttonUpdates, 3, 0);
        host.Controls.Add(buttonDonate, 4, 0);
        host.Controls.Add(diagnosticButton, 5, 0);
        host.Controls.Add(buttonQuit, 6, 0);
        panelFooter.Controls.Add(host);
        panelFooter.Controls.Add(topLine);
    }

    /// <summary>
    /// 设计器旧 footer 按钮统一改图标形态：Dock 清掉（Top/Fill 会在等列单元里把它拉成
    /// 满格大块，真机首验实证）、文本清空、图标+底部小字居中。铬全部取自
    /// UiVisualStyle.StyleFooterGhostButton（与设置/诊断键同一来源）。接线不动。
    /// </summary>
    void ResetLegacyFooterButton(Button button, string label, UiGlyph.Kind icon, Func<int, int> scale)
    {
        button.Text = label;
        button.Dock = DockStyle.None;
        button.Anchor = AnchorStyles.None;   // 单元内居中，保持 46x36 的紧凑键尺寸
        button.AutoSize = false;
        button.Size = new Size(scale(46), scale(36));
        button.BackColor = panelFooter.BackColor;
        UiVisualStyle.StyleFooterGhostButton(button);
        if (icon == UiGlyph.Kind.Overlay)
        {
            // 悬浮窗键的图标/文字色随激活态走 Accent/Muted：UpdateFooterOverlayVisual
            // 登记带状态的取色器，主题重刷按当前主题+激活态现算（不再残留构建期颜色）。
            UpdateFooterOverlayVisual(AppConfig.IsOverlay());
        }
        else
        {
            UiVisualStyle.ApplyFooterGlyph(button, icon, scale(16));
        }
    }

    /// <summary>悬浮窗键激活态 = 强调色图标/文字；未激活态与其余五键同色（Text 字 + Muted 图标）。
    /// 图标登记带激活态的取色器：主题重刷时按当前主题+激活态现算，不残留构建期颜色。</summary>
    internal void UpdateFooterOverlayVisual(bool active)
    {
        buttonOverlay.ForeColor = active ? UiVisualStyle.Accent : UiStyleXXX();
        UiVisualStyle.ApplyFooterGlyph(buttonOverlay, UiGlyph.Kind.Overlay,
            ResponsiveLayout.LogicalToDevice(this, 16),
            () => AppConfig.IsOverlay() ? UiVisualStyle.Accent : UiVisualStyle.Muted);
    }

    static Color UiStyleXXX() => UiVisualStyle.Text;
}
