using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Diagnostics;

namespace MechrevoLite;

/// <summary>
/// 键盘灯效窗口：HID 自定义组（BetterRGB 10 效果），软件渲染发帧，各效果专属参数
/// （方向/速度/颜色等）；睡眠时间由应用内实现。
/// 官方固件效果（16 种，走 GCU 下发并写键盘 NVRAM）已整体移除。
/// </summary>
public class RgbForm : RForm
{
    internal static readonly (int Mode, string Name)[] HidEffects =
    {
        (KeyboardRgb.ModeWave, "流畅彩虹"), (KeyboardRgb.ModeStatic, "静态"), (KeyboardRgb.ModeBreath, "呼吸"),
        (KeyboardRgb.ModeSparkle, "闪烁"), (KeyboardRgb.ModeReactive, "按键反应"), (KeyboardRgb.ModeWheel, "彩虹轮"),
        (KeyboardRgb.ModeLightning, "闪电"), (KeyboardRgb.ModeFlame, "火焰"), (KeyboardRgb.ModeRain, "雨滴"),
        (KeyboardRgb.ModeMatrix, "矩阵"),
    };

    readonly KeyboardRgb _rgb;
    Panel _hidPanel = null!;
    Label _lblStatus = null!;
    RowStyle _statusRowStyle = null!;
    readonly List<string> _paramLabels = new();
    int _paramLabelCol;
    int _minContentWidth;
    int _applyGen;               // 模式切换代际：旧切换任务的延迟动作不得覆盖新选择
    int _closeTimerCommandGen;   // 睡眠时间快速连选时仅确认最后一次设置（ApplyModeSelection 收尾推送用）
    System.Windows.Forms.Timer? _modeSyncTimer;   // 仪表盘改效果时，已打开的对话框跟随重放
    int _activeHidMode = KeyboardRgb.ModeWave;
    Action? _updateClientHeight;   // 内容定高；OnLoad 里首帧之前跑一次（Shown 时窗口已可见，改高会跳）

    int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);

    /// <summary>
    /// 状态行只在有事可说时占行：空文本行高收 0（不保留常驻说明的死行）。
    /// 可见时行高 D(25)：UI 审计缩放视口里字体不随 form.Scale 缩小，行高必须 ≥25 逻辑 px
    /// 才能在 100pct 视口装下缩放后的状态文字（实测 19px + 内距）。
    /// </summary>
    void SetStatus(string text)
    {
        _lblStatus.Text = text;
        bool show = text.Length > 0;
        _lblStatus.Visible = show;
        _statusRowStyle.Height = show ? D(25) : 0;
    }

    public RgbForm(KeyboardRgb rgb)
    {
        _rgb = rgb;
        _rgb.LoadConfig();
        _activeHidMode = _rgb.KbHidMode;

        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = "键盘灯效";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // 宽度构建完成后按内容实测收紧（标签列 + 控件列）；高度由 UpdateClientHeight 跟随内容表。
        ClientSize = new Size(D(520), D(120));
        InitTheme(true);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            ColumnCount = 2,
            Padding = new Padding(D(12)),
            BackColor = UiVisualStyle.Window,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        void SpanRow(Control ctrl)
        {
            ctrl.Dock = DockStyle.Fill;
            table.Controls.Add(ctrl, 0, row);
            table.SetColumnSpan(ctrl, 2);
            row++;
        }

        // ---- 参数面板 ----
        _hidPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiVisualStyle.Surface,
            Padding = new Padding(D(8)),
        };
        _hidPanel.Resize += (_, _) => FitParameterTable(_hidPanel);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_hidPanel, 0, row);
        table.SetColumnSpan(_hidPanel, 2);
        row++;
        BuildHidParams();

        // ---- 状态（只在有事可说时占行：SetStatus 把行高在 0 与 D(25) 间切换）----
        _statusRowStyle = new RowStyle(SizeType.Absolute, 0);
        table.RowStyles.Add(_statusRowStyle);
        _lblStatus = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            ForeColor = UiVisualStyle.Muted,
            AutoEllipsis = true,
            Visible = false,
        };
        SpanRow(_lblStatus);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiVisualStyle.Window,
        };
        scroll.Controls.Add(table);
        Controls.Add(scroll);

        // 内容实测宽度：标签列（最长标签一行）+ 控件列（滑条最短轨道 / 下拉 / 单行 5 色块）
        // + 面板内外边距。Static 模式 10 色块走自动换行，不参与定宽。
        int labelMeasure = 0;
        using (Graphics measureGraphics = CreateGraphics())
            foreach (string text in _paramLabels)
                labelMeasure = Math.Max(labelMeasure, TextRenderer.MeasureText(measureGraphics, text, Font, Size.Empty,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        _paramLabelCol = labelMeasure + D(8);
        int swatchRow = 5 * D(46) + 4 * D(4) + D(4);   // Rain/Matrix 单行 5 色块
        int controlCol = Math.Max(Math.Max(D(RSlider.MinTrackLogicalWidth), D(100)), swatchRow);
        _minContentWidth = _paramLabelCol + controlCol + 2 * D(8) + 2 * D(12);
        MinimumSize = new Size(_minContentWidth + D(24), D(280));
        ClientSize = new Size(_minContentWidth, ClientSize.Height);

        // 窗口高度跟随内容表的首选高（BuildHidParams 每次重建行后都会变）：
        // 只调高度不动宽度 → 表宽不变 → 不会回环。MinimumSize 仍是下限，窗口保持可调。
        void UpdateClientHeight()
        {
            if (IsDisposed || table.Height <= 0) return;
            int desired = table.Height;
            // MinimumSize 是窗口下限，但内容变矮时必须随之下调：否则下限把窗口顶得比内容高，
            // 底部留出死空白（实测 minH=442 钉住 442 窗口而内容只要 406）。
            int nonClient = Math.Max(0, Height - ClientSize.Height);
            MinimumSize = new Size(MinimumSize.Width, Math.Min(MinimumSize.Height, desired + nonClient));
            if (ClientSize.Height != desired) ClientSize = new Size(ClientSize.Width, desired);
        }
        table.SizeChanged += (_, _) => UpdateClientHeight();
        UpdateClientHeight();   // 构建期先校一次，避免首帧停在初始 D(120) 高
        _updateClientHeight = UpdateClientHeight;

        // 模式跟随：仪表盘键盘行改 KbHidMode 时（对话框已打开/可见），2s 内重放到本窗。
        // 睡眠空闲检测已由 Program.StartLightingIdleMonitor 统一轮询，这里不再重复。
        _modeSyncTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _modeSyncTimer.Tick += (_, _) =>
        {
            if (!Visible || IsDisposed) return;
            int dashboardIdx = Array.FindIndex(HidEffects, e => e.Mode == _rgb.KbHidMode);
            int appliedIdx = Array.FindIndex(HidEffects, e => e.Mode == _activeHidMode);
            if (dashboardIdx >= 0 && dashboardIdx != appliedIdx) ApplyModeSelection();
        };
        if (!Program.UiAuditMode) _modeSyncTimer.Start();

        _rgb.DeviceLost += OnRgbDeviceLost;

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                _rgb.SaveConfig();
                e.Cancel = true;
                Hide();
            }
        };
        FormClosed += (_, _) =>
        {
            _modeSyncTimer?.Stop();
            _modeSyncTimer?.Dispose();
            _rgb.DeviceLost -= OnRgbDeviceLost;
        };

        Shown += async (_, _) =>
        {
            // 注意：内容定高已在 OnLoad（首帧之前）完成，这里不再改高——Shown 时窗口已可见，
            // 改高会让首帧之后跳一下。
            // 重新显示时恢复设备相关控件（DeviceLost 曾禁用过它们），状态色回中性。
            SetDeviceUiEnabled(true);
            _lblStatus.ForeColor = UiVisualStyle.Muted;
            if (!_rgb.KbPowerOn)
            {
                SetStatus("灯效已关闭");
                return;
            }
            SetStatus("正在连接…");
            await Task.Delay(1500);
            if (Program.hw is { IsConnected: true })
            {
                SetStatus(string.Empty);   // 正常态不占状态行（常驻说明已删）
                ApplyModeSelection(force: true);
            }
            else SetStatus("服务未连接");
        };
        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplySection(_hidPanel);
    }

    /// <summary>
    /// Load 在窗体可见之前触发：这里按内容表实测高定窗口高，保证首帧尺寸就是最终尺寸。
    /// （原先放在 Shown——那时窗口已经画过一次，高度收紧会让首帧之后跳一下。）
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _updateClientHeight?.Invoke();
    }

    /// <summary>设备可用性驱动的 UI 态：HID 断开时参数面板整体禁用，
    /// 状态文字用 Danger（对比度已校准）；窗口重新显示时恢复并按连接状态重判。</summary>
    void SetDeviceUiEnabled(bool enabled)
    {
        _hidPanel.Enabled = enabled;
    }

    void OnRgbDeviceLost()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(OnRgbDeviceLost); return; }
            SetDeviceUiEnabled(false);
            SetStatus(_rgb.LastError ?? "键盘 RGB 设备未连接");
            _lblStatus.ForeColor = UiVisualStyle.Danger;
        }
        catch (Exception ex) { Logger.WriteLine("RGB device-lost UI update failed: " + ex.Message); }
    }

    async Task SyncDeviceCloseTimerAsync()
    {
        int generation = Interlocked.Increment(ref _closeTimerCommandGen);
        await Task.Delay(150);
        if (generation != Volatile.Read(ref _closeTimerCommandGen)) return;
        if (Program.service is null || Program.hw is not { IsConnected: true }) return;

        // 设备侧计时必须关闭（0）：睡眠由应用内空闲检测实现，固件先熄灭会让恢复时序打架。
        bool applied = await Program.service.SwitchCloseTimer(0);
        if (generation != Volatile.Read(ref _closeTimerCommandGen) || applied) return;
        try
        {
            if (!IsDisposed) SetStatus("睡眠时间已保存，将在设备重连后重试");
        }
        catch (Exception ex) { Logger.WriteLine("RGB close-timer status update failed: " + ex.Message); }
    }

    // ================= 模式分发 =================

    void ApplyModeSelection(bool force = false)
    {
        // 单一真相源是 _rgb.KbHidMode（仪表盘键盘行写它，Settings.V2.cs 回显同步）：
        // 本窗不再有自己的模式下拉，直接按 KbHidMode 找目录项。
        int idx = Array.FindIndex(HidEffects, e => e.Mode == _rgb.KbHidMode);
        if (idx < 0) idx = 0;
        _rgb.KbPowerOn = true;
        Program.NotifyLightingUserIntent();
        int gen = ++_applyGen;          // 切换代际：快速连点/来回切换时，旧任务的延迟动作不得晚到覆盖新选择
        var hid = HidEffects[idx];
        _activeHidMode = hid.Mode;
        _rgb.KbHidMode = hid.Mode;
        Program.MarkKeyboardCustomStatusBaseline();
        BuildHidParams();
        if (!_rgb.IsConnected)
        {
            // HID 枚举/初始化耗时（含内部 Sleep）——后台线程执行，避免 UI 冻结
            SetStatus("正在连接键盘 RGB…");
            _ = Task.Run(() =>
            {
                bool ok = _rgb.Connect();
                try
                {
                    Invoke(() =>
                    {
                        SetStatus(ok ? string.Empty : "连接失败: " + _rgb.LastError);
                        if (ok && gen == _applyGen)
                            _ = Task.Run(() =>
                            {
                                _rgb.StartMode(hid.Mode);   // HID 帧写后台执行
                                Program.MarkKeyboardCustomStatusBaseline();
                            });
                    });
                }
                catch { }
            });
        }
        else
        {
            // StartMode 含 Stop（Join+blank 帧 HID 写）——后台执行避免 UI 卡顿
            _ = Task.Run(async () =>
            {
                // 仅睡眠断电时补开灯；正常切换不动电源，避免与固件上电时的默认效果竞争。
                if (Program.service is not null && Program.hw is { IsConnected: true } && !Program.hw.KeyboardPower)
                {
                    await Program.service.SetLightPower(MqttTopics.KeyboardCtrl, true);
                    if (gen != _applyGen) return;
                }
                _rgb.ReInitCustomMode();   // 固件效果模式下会忽略 HID 帧——重新进入自定义帧模式
                if (gen != _applyGen) return;
                _rgb.StartMode(hid.Mode);
                Program.MarkKeyboardCustomStatusBaseline();
            });
            SetStatus(string.Empty);   // 正常态不占状态行（常驻说明已删）
        }
        _ = SyncDeviceCloseTimerAsync();
        _rgb.QueueSaveConfig();
    }

    // ================= HID 参数面板（BetterRGB 完整参数） =================

    void BuildHidParams()
    {
        // TableLayoutPanel 自动布局（AutoSize 行 + Dock=Fill）：DPI 缩放/不同字号下不重叠
        _hidPanel.Controls.Clear();
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            ColumnCount = 2,
            Padding = new Padding(D(8)),
            BackColor = UiVisualStyle.Surface,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _paramLabelCol > 0 ? _paramLabelCol : D(96)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Clear();
        int row = 0;
        void AddRow(string label, Control ctrl)
        {
            _paramLabels.Add(label);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // Label Dock=Fill：高度=行高，文字垂直居中（与控件同行对齐）
            table.Controls.Add(new Label
            {
                Text = label,
                ForeColor = UiVisualStyle.Text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
            }, 0, row);
            if (ctrl is ComboBox or TrackBar)   // 下拉/滑条 Dock=Fill 会撑满行高错位——Anchor 水平拉伸 + 固定高度
            {
                ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                ctrl.Margin = new Padding(0, D(2), 0, D(2));
            }
            else if (ctrl is Button)   // 颜色块等小控件：左对齐固定尺寸，不拉伸
            {
                ctrl.Anchor = AnchorStyles.Left;
                ctrl.Margin = new Padding(0, D(2), 0, D(2));
            }
            else ctrl.Dock = DockStyle.Fill;
            table.Controls.Add(ctrl, 1, row);
            row++;
        }
        void AddColorRow(string label, Button[] swatches)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = label,
                ForeColor = UiVisualStyle.Text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
            }, 0, row);
            // FlowLayoutPanel 不用 Dock=Fill（与 AutoSize 冲突导致行高错乱）——Anchor 顶部 + AutoSize
            var flow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = UiVisualStyle.Surface,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
            };
            foreach (var b in swatches) flow.Controls.Add(b);
            table.Controls.Add(flow, 1, row);
            row++;
        }
        // 离散档位下拉（档位类参数不用滑条，选择更直观）
        RComboBox TierCombo(int min, int max, int value, Action<int> set)
        {
            var c = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            for (int i = min; i <= max; i++) c.Items.Add(i.ToString());
            c.SelectedIndex = Math.Clamp(value - min, 0, max - min);
            c.SelectedIndexChanged += (_, _) =>
            {
                set(c.SelectedIndex + min);
                _rgb.QueueSaveConfig();
            };
            return c;
        }
        RSlider Slider(int min, int max, int value, Action<int> set)
        {
            // 深潜座舱：自绘滑条（原生 TrackBar 在深色主题上会漏出灰色轨道与系统蓝滑块）。
            var s = new RSlider
            {
                Minimum = min, Maximum = max, Value = value,
                MinimumSize = new Size(D(RSlider.MinTrackLogicalWidth), D(20)), Height = D(24),
            };
            s.ValueChanged += (_, _) =>
            {
                set(s.Value);
                _rgb.QueueSaveConfig();
            };
            return s;
        }
        RColorButton Swatch(Color color, Action<Color> apply)
        {
            // run5 收尾：色块从原生 Button 换成 RColorButton（与应用色块语义一致）；
            // Click 仍开 RColorPicker，驱动路径不变。
            var b = new RColorButton
            {
                Size = new Size(D(46), D(24)),
                BackColor = UiVisualStyle.Input,
                BorderColor = UiVisualStyle.Border,
                SwatchColor = color,
                Cursor = Cursors.Hand,
                Tag = "color-swatch",
                Margin = new Padding(D(2)),
            };
            b.Click += (_, _) =>
            {
                var dlg = new RColorPicker(color, false);
                dlg.ColorChanged += c =>
                {
                    apply(c);
                    b.SwatchColor = c;
                    _rgb.QueueSaveConfig();
                };
                dlg.ShowDialog(this);
            };
            return b;
        }
        CheckBox Chk(string text, bool state, Action<bool> set)
        {
            var c = new RCheckBox { Text = text, Checked = state, ForeColor = UiVisualStyle.Text, AutoSize = true };
            c.CheckedChanged += (_, _) =>
            {
                set(c.Checked);
                _rgb.QueueSaveConfig();
            };
            return c;
        }

        // 通用：亮度 + 帧率
        var bright = Slider(0, 100, _rgb.Brightness, v => _rgb.Brightness = v);
        AddRow("亮度", bright);
        var fps = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var f in new[] { "15 FPS", "30 FPS", "45 FPS", "60 FPS" }) fps.Items.Add(f);
        int fpsIdx = Array.IndexOf(new[] { 15, 30, 45, 60 }, _rgb.TargetFps);
        fps.SelectedIndex = fpsIdx >= 0 ? fpsIdx : 1;
        fps.SelectedIndexChanged += (_, _) =>
        {
            _rgb.TargetFps = new[] { 15, 30, 45, 60 }[fps.SelectedIndex];
            _rgb.QueueSaveConfig();
        };
        AddRow("帧率", fps);

        switch (_activeHidMode)
        {
            case KeyboardRgb.ModeWave:
                AddRow("角度", Slider(0, 360, _rgb.WaveAngle, v => _rgb.WaveAngle = v));
                AddRow("速度", Slider(1, 20, _rgb.WaveSpeed, v => _rgb.WaveSpeed = v));
                break;
            case KeyboardRgb.ModeStatic:
                {
                    // 分区颜色：分区数 N → N 个独立颜色块（与分区一一对应）
                    int zones = Math.Clamp(_rgb.StaticZones, 1, 10);
                    AddColorRow("分区颜色", Enumerable.Range(0, zones).Select(i =>
                    {
                        int idx = i;
                        return Swatch(_rgb.StaticColors[i], c => _rgb.StaticColors[idx] = c);
                    }).ToArray());
                    AddRow("布局", MakeStaticLayoutCombo());
                    var zonesCombo = TierCombo(1, 10, _rgb.StaticZones, v =>
                    {
                        _rgb.StaticZones = v;
                        BuildHidParams();   // 分区数变化 → 重建面板刷新颜色块数量
                    });
                    AddRow("分区数", zonesCombo);
                    break;
                }
            case KeyboardRgb.ModeBreath:
                AddRow("颜色", Swatch(_rgb.BreathColor, c => _rgb.BreathColor = c));
                AddRow("速度", Slider(1, 30, _rgb.BreathSpeed, v => _rgb.BreathSpeed = v));
                AddRow("平滑", Slider(2, 100, _rgb.BreathSmooth, v => _rgb.BreathSmooth = v));
                break;
            case KeyboardRgb.ModeSparkle:
                AddRow("速度", Slider(1, 20, _rgb.SparkleSpeed, v => _rgb.SparkleSpeed = v));
                AddRow("密度", Slider(1, 20, _rgb.SparkleDensity, v => _rgb.SparkleDensity = v));
                break;
            case KeyboardRgb.ModeReactive:
                AddRow("颜色", Swatch(_rgb.ReactiveColor, c => _rgb.ReactiveColor = c));
                AddRow("", Chk("随机颜色", _rgb.ReactiveRandom, v => _rgb.ReactiveRandom = v));
                AddRow("时长", Slider(5, 60, _rgb.ReactiveDuration, v => _rgb.ReactiveDuration = v));
                break;
            case KeyboardRgb.ModeWheel:
                AddRow("", Chk("反向旋转", _rgb.WheelReverse, v => _rgb.WheelReverse = v));
                AddRow("速度", Slider(1, 20, _rgb.WheelSpeed, v => _rgb.WheelSpeed = v));
                break;
            case KeyboardRgb.ModeLightning:
                AddRow("速度", Slider(1, 20, _rgb.LightningSpeed, v => _rgb.LightningSpeed = v));
                AddRow("平滑", Slider(1, 40, _rgb.LightningSmooth, v => _rgb.LightningSmooth = v));
                AddRow("宽度", Slider(1, 4, _rgb.LightningWidth, v => _rgb.LightningWidth = v));
                AddRow("并发", Slider(1, 4, _rgb.LightningConcurrent, v => _rgb.LightningConcurrent = v));
                break;
            case KeyboardRgb.ModeFlame:
                AddRow("速度", Slider(1, 20, _rgb.FlameSpeed, v => _rgb.FlameSpeed = v));
                AddRow("平滑", Slider(1, 40, _rgb.FlameSmooth, v => _rgb.FlameSmooth = v));
                break;
            case KeyboardRgb.ModeRain:
                AddRow("速度", Slider(1, 20, _rgb.RainSpeed, v => _rgb.RainSpeed = v));
                AddRow("密度", TierCombo(1, 5, _rgb.RainDensity, v => _rgb.RainDensity = v));
                AddRow("颜色数", TierCombo(1, 5, _rgb.RainColorCount, v => _rgb.RainColorCount = v));
                AddColorRow("颜色", Enumerable.Range(0, 5).Select(i =>
                {
                    int idx = i;
                    return Swatch(_rgb.RainColors[i], c => _rgb.RainColors[idx] = c);
                }).ToArray());
                break;
            case KeyboardRgb.ModeMatrix:
                AddRow("速度", Slider(1, 20, _rgb.MatrixSpeed, v => _rgb.MatrixSpeed = v));
                var style = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                style.Items.AddRange(new[] { "流光", "落雨", "激光", "涟漪" });
                style.SelectedIndex = _rgb.MatrixStyle;
                style.SelectedIndexChanged += (_, _) =>
                {
                    _rgb.MatrixStyle = style.SelectedIndex;
                    _rgb.QueueSaveConfig();
                };
                AddRow("样式", style);
                AddRow("密度", TierCombo(1, 5, _rgb.MatrixDensity, v => _rgb.MatrixDensity = v));
                AddRow("颜色数", TierCombo(1, 3, _rgb.MatrixColorCount, v => _rgb.MatrixColorCount = v));
                AddColorRow("颜色", Enumerable.Range(0, 3).Select(i =>
                {
                    int idx = i;
                    return Swatch(_rgb.MatrixColors[i], c => _rgb.MatrixColors[idx] = c);
                }).ToArray());
                break;
        }

        // 占位行吸收多余垂直空间：否则 TLP 会把最后一行拉高（文字视觉偏下）
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _hidPanel.Controls.Add(table);
        FitParameterTable(_hidPanel);
        UiVisualStyle.ApplySection(_hidPanel);
    }

    static void FitParameterTable(Panel panel)
    {
        if (panel.Controls.OfType<TableLayoutPanel>().FirstOrDefault() is not { } table) return;
        int width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal);
        table.Width = width;
        table.MinimumSize = new Size(width, 0);
        table.MaximumSize = new Size(width, 0);
    }

    RComboBox MakeStaticLayoutCombo()
    {
        var c = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = D(100) };
        c.Items.AddRange(new[] { "横向分区", "纵向分区" });
        c.SelectedIndex = _rgb.StaticLayout;
        c.SelectedIndexChanged += (_, _) =>
        {
            _rgb.StaticLayout = c.SelectedIndex;
            _rgb.QueueSaveConfig();
        };
        return c;
    }
}
