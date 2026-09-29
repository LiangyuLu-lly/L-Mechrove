using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>
/// 通用灯效窗口（灯条 / Logo 灯）——原版协议：MyKeyBoard 载荷（SetEffectALL）+ 各自 Ctrl topic。
/// 效果由固件执行；参数：亮度档/速度/单色颜色（效果选择由仪表盘灯条/Logo 行承担）。
/// </summary>
public class LightForm : RForm
{
    // 原版各灯型固件实际支持的效果列表（反编译 InitalizeKeyboardTypeEffect/GetLogoList）
    public static readonly (string Effect, string Name)[] LightbarEffects =
    {
        ("Single", "单色"), ("Breathing", "呼吸"), ("Wave", "波浪"), ("Impact", "冲击"), ("Raindrop", "流星"),
    };
    public static readonly (string Effect, string Name)[] LogoEffects =
    {
        ("Single", "单色"), ("Breathing", "呼吸"), ("Mix", "混合"),
    };

    readonly string _topic;
    readonly (string Effect, string Name)[] _effects;
    string _effect;
    int _light = 4;
    int _speed = 1;
    bool _powerOn = true;   // 灯光电源持久化：用户关灯选择不因重开窗体被推翻
    Color _singleColor = Color.White;
    string _direction = LightingEffectCatalog.DirNone;
    bool _usePalette;
    Label? _statusLabel;
    Action? _updateParameterRows;

    /// <summary>当前效果的官方参数规格（按本机识别出的通道目录查）。</summary>
    LightEffectSpec? CurrentSpec()
    {
        LightEffectSpec[] catalog = Program.hw is { } hw
            ? LightingEffectCatalog.ForTopic(_topic, hw.Lighting, hw.BiosProjectId)
            : LightingEffectCatalog.ForTopic(_topic, LightingChannelSet.Empty, null);
        return LightingEffectCatalog.Find(catalog, _effect);
    }

    LightChannelSettings CurrentSettings() =>
        new(_effect, _light, _speed, _singleColor.ToArgb(), _powerOn, _direction, _usePalette);

    void UpdateParameterRows() => _updateParameterRows?.Invoke();

    /// <summary>下发结果如实显示：已确认（附证据来源）/ 已下发 / 失败。</summary>
    void ShowOutcome(LightApplyOutcome outcome, LightReadbackSource source)
    {
        if (_statusLabel is null || IsDisposed) return;
        void Apply()
        {
            _statusLabel.Text = LightingEffectCatalog.OutcomeText(outcome, source);
            _statusLabel.ForeColor = outcome == LightApplyOutcome.Failed ? UiVisualStyle.Danger : UiVisualStyle.Muted;
            _statusLabel.Visible = true;
            if (Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } table && IsHandleCreated)
                ClientSize = new Size(ClientSize.Width, table.Height);
        }
        if (InvokeRequired) BeginInvoke(Apply); else Apply();
    }
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _disposeCts = new();
    readonly System.Windows.Forms.Timer _effectUpdateTimer = new() { Interval = 180 };
    int _sendGeneration;
    int _disposed;

    internal static void NotifyApplyOutcome(bool accepted, bool cancelled)
    {
        if (accepted || cancelled) return;
        ToastForm.ShowFailure("灯效设置失败。");
    }

    public LightForm(string topic, string title, (string Effect, string Name)[]? effects = null)
    {
        _topic = topic;
        _effects = effects ?? LightbarEffects;
        _effect = _effects[0].Effect;
        LoadSettings();   // 恢复上次设置（效果/亮度/速度/颜色）
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(420, 240);
        InitTheme(true);

        var table = new TableLayoutPanel
        {
            // Dock=Top + AutoSize：窗口高度随内容收口（run5 二级界面收尾，替代原
            // Fill + Percent-100 填充行——填充行在 3 行内容下留 ~60px 空白）。
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = UiVisualStyle.Surface,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Clear();
        int row = 0;
        void AddRow(string label, Control ctrl)
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
            if (ctrl is ComboBox or TrackBar or RSlider)
            {
                ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                ctrl.Margin = new Padding(0, 3, 0, 3);
            }
            else if (ctrl is Button)   // 颜色块等小控件：左对齐不拉伸
            {
                ctrl.Anchor = AnchorStyles.Left;
                ctrl.Margin = new Padding(0, 3, 0, 3);
            }
            else ctrl.Dock = DockStyle.Fill;
            table.Controls.Add(ctrl, 1, row);
            row++;
        }

        void SyncEffectFromDashboard()
        {
            _effect = LightingSettingsStore.Load(_topic, _effects[0].Effect).Effect;
        }

        async Task SendAsync()
        {
            SyncEffectFromDashboard();
            SaveSettings();
            UpdateParameterRows();
            if (Program.service is null || Program.hw is not { IsConnected: true })
            {
                // 设置已保存（重连后按保存值恢复），但这一次灯没有变化：必须让用户看见，不能静默。
                ShowOutcome(LightApplyOutcome.Failed, LightReadbackSource.None);
                ToastForm.ShowFailure(Properties.Strings.GcuNotConnectedAction);
                return;
            }
            int generation = Interlocked.Increment(ref _sendGeneration);
            bool lockTaken = false;
            try
            {
                await _sendLock.WaitAsync(_disposeCts.Token);
                lockTaken = true;
                if (Volatile.Read(ref _disposed) != 0) return;
                if (generation != _sendGeneration) return;
                LightEffectSpec? spec = CurrentSpec();
                LightChannelSettings current = CurrentSettings();
                var (outcome, source) = await Program.service.ApplyLightEffectConfirmedAsync(_topic, _effect, _light, _speed,
                    LightingSettingsStore.DirectionForSpec(spec, current),
                    LightingSettingsStore.ColorForSpec(spec, current), save: true,
                    brightnessApplies: spec?.Brightness ?? true);
                if (Volatile.Read(ref _disposed) != 0) return;
                if (outcome == LightApplyOutcome.Failed)
                    Logger.WriteLine($"Light effect was not accepted: topic={_topic}, effect={_effect}");
                if (generation == _sendGeneration) ShowOutcome(outcome, source);
                NotifyApplyOutcome(outcome != LightApplyOutcome.Failed, cancelled: false);
            }
            catch (OperationCanceledException)
            {
                NotifyApplyOutcome(accepted: false, cancelled: true);
            }
            finally
            {
                if (lockTaken) _sendLock.Release();
            }
        }

        void ScheduleEffectUpdate()
        {
            SyncEffectFromDashboard();
            SaveSettings();
            if (!_powerOn || Volatile.Read(ref _disposed) != 0) return;

            _effectUpdateTimer.Stop();
            _effectUpdateTimer.Start();
        }

        _effectUpdateTimer.Tick += async (_, _) =>
        {
            _effectUpdateTimer.Stop();
            await SendAsync();
        };

        void LoadSettings()
        {
            LightChannelSettings settings = LightingSettingsStore.Load(_topic, _effects[0].Effect);
            _effect = settings.Effect;
            _light = settings.Light;
            _speed = settings.Speed;
            _singleColor = Color.FromArgb(settings.ColorArgb);
            _powerOn = settings.PowerOn;
            _direction = settings.Direction;
            _usePalette = settings.UsePalette;
        }

        void SaveSettings()
        {
            LightingSettingsStore.Save(_topic, CurrentSettings());
        }

        // 深潜座舱：自绘滑条（原生 TrackBar 在深色主题上会漏灰轨道与系统蓝滑块）。
        var sliderLight = new RSlider
        {
            Minimum = 0, Maximum = 100, Value = _light * 25,
            MinimumSize = new Size(104, 20), Height = 28,
        };
        sliderLight.ValueChanged += (_, _) =>
        {
            _light = Math.Clamp(sliderLight.Value * 4 / 100, 0, 4);
            ScheduleEffectUpdate();
        };
        AddRow("亮度", sliderLight);

        var comboSpeed = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        comboSpeed.Items.AddRange(new[] { "慢", "中", "快" });
        comboSpeed.SelectedIndex = Math.Clamp(_speed - 1, 0, 2);
        comboSpeed.SelectedIndexChanged += (_, _) =>
        {
            _speed = comboSpeed.SelectedIndex + 1;
            ScheduleEffectUpdate();
        };
        AddRow("速度", comboSpeed);

        // 方向：只有官方给该效果列了多个方向时才出现（灯条/Logo 目录目前都没有方向）。
        var comboDirection = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        string[] directionIds = Array.Empty<string>();
        comboDirection.SelectedIndexChanged += (_, _) =>
        {
            int i = comboDirection.SelectedIndex;
            if (i < 0 || i >= directionIds.Length || directionIds[i] == _direction) return;
            _direction = directionIds[i];
            ScheduleEffectUpdate();
        };
        AddRow(Properties.Strings.LightParamDirection, comboDirection);

        var colorBtn = new RColorButton
        {
            Size = new Size(36, 24),
            BackColor = UiVisualStyle.Input,
            BorderColor = UiVisualStyle.Border,
            SwatchColor = _singleColor,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 3),
        };
        // 多色效果：七彩调色板 或 把所选颜色铺满全部色块（官方「同步颜色」同一语义），两种都真实生效。
        var paletteCheck = new RCheckBox
        {
            Text = Properties.Strings.LightColorPalette,
            AutoSize = true,
            ForeColor = UiVisualStyle.Text,
            Checked = _usePalette,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 3),
        };
        var colorHost = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        colorHost.Controls.Add(colorBtn);
        colorHost.Controls.Add(paletteCheck);
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new RColorPicker(_singleColor, false);
            dlg.ColorChanged += c =>
            {
                LightEffectSpec? spec = CurrentSpec();
                // A 面 Logo 呼吸只接受六种预设色：色块直接显示吸附后的颜色，不显示一个固件不会用的颜色。
                _singleColor = spec?.PresetColors is { Length: > 0 } presets
                    ? LightingEffectCatalog.NearestPreset(c, presets) : c;
                colorBtn.SwatchColor = _singleColor;
                if (spec?.UsesColor ?? LightingSettingsStore.EffectUsesSingleColor(_effect)) ScheduleEffectUpdate();
            };
            dlg.ShowDialog(this);
        };
        paletteCheck.CheckedChanged += (_, _) =>
        {
            if (paletteCheck.Checked == _usePalette) return;
            _usePalette = paletteCheck.Checked;
            colorBtn.Enabled = !_usePalette && _powerOn;
            ScheduleEffectUpdate();
        };
        AddRow("单色颜色", colorHost);
        Label colorLabel = (Label)table.GetControlFromPosition(0, row - 1)!;

        // 结果行：只在有结果时占位（已确认 / 已下发 / 失败）。
        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = UiVisualStyle.Muted,
            Margin = new Padding(0, 6, 0, 0),
            Visible = false,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_statusLabel, 0, row);
        table.SetColumnSpan(_statusLabel, 2);
        row++;

        void SetRowVisible(Control control, bool visible)
        {
            control.Visible = visible;
            if (table.GetControlFromPosition(0, table.GetRow(control)) is Label label) label.Visible = visible;
        }

        // 参数行按当前效果的官方规格显隐：速度/方向/颜色只在固件真的采用时出现（杜绝「点了没用」的控件）。
        _updateParameterRows = () =>
        {
            LightEffectSpec? spec = CurrentSpec();
            SetRowVisible(sliderLight, spec?.Brightness ?? true);
            SetRowVisible(comboSpeed, spec?.Speed ?? true);
            directionIds = spec?.Directions is { Length: > 1 } dirs ? dirs : Array.Empty<string>();
            if (directionIds.Length > 1)
            {
                comboDirection.Items.Clear();
                foreach (string d in directionIds) comboDirection.Items.Add(LightingEffectCatalog.DirectionLabel(d));
                int selected = Array.IndexOf(directionIds, _direction);
                comboDirection.SelectedIndex = selected >= 0 ? selected : 0;
            }
            SetRowVisible(comboDirection, directionIds.Length > 1);
            bool usesColor = spec?.UsesColor ?? true;
            SetRowVisible(colorHost, usesColor);
            paletteCheck.Visible = spec?.MultiColor == true;
            colorLabel.Text = spec?.MultiColor == true || spec?.PresetColors is not null
                ? Properties.Strings.LightParamColor : "单色颜色";
            if (spec?.PresetColors is { Length: > 0 } presets)
            {
                _singleColor = LightingEffectCatalog.NearestPreset(_singleColor, presets);
                colorBtn.SwatchColor = _singleColor;
            }
            if (IsHandleCreated && !IsDisposed) ClientSize = new Size(ClientSize.Width, table.Height);
        };

        // 关灯时效果不显示（SetLightPower off）：参数行整体禁用，开灯恢复。
        // 电源开关由仪表盘灯条/Logo 行承担（对话框内重复开关已于 2026-09-13 移除）。
        void UpdateChannelEnabled()
        {
            bool enabled = LightingSettingsStore.Load(_topic, _effects[0].Effect).PowerOn;
            sliderLight.Enabled = enabled;
            comboSpeed.Enabled = enabled;
            comboDirection.Enabled = enabled;
            paletteCheck.Enabled = enabled;
            colorBtn.Enabled = enabled && !(_usePalette && paletteCheck.Visible);
        }
        _updateParameterRows();
        UpdateChannelEnabled();
        Activated += (_, _) => { SyncEffectFromDashboard(); UpdateParameterRows(); };

        Controls.Add(table);

        Shown += async (_, _) =>
        {
            // 请求状态 + 按持久化电源开关恢复（原版页面进入时 GETSTATUS；未开灯时效果不显示）
            if (Program.hw is { IsConnected: true } && Program.service is not null)
            {
                await Program.service.RequestLightStatus(_topic);
                bool confirmed = await Program.service.SetLightPower(_topic, _powerOn);
                if (!confirmed)
                {
                    NotifyApplyOutcome(accepted: false, cancelled: false);
                    return;
                }
                if (!_powerOn) return;
                await Task.Delay(600);
                await SendAsync();
            }
        };
        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplySection(table);
        // 内容实测窗口高度（run5 二级界面收尾）：表格 Dock=Top + AutoSize 自身收口，
        // 窗口高度取表格实测高（钳制工作区由 RForm.ApplyResponsiveBounds 承担）。
        ResponsiveLayout.PerformLayoutTree(this);
        ResponsiveLayout.ScaleFrom96(this, this);
        ResponsiveLayout.PerformLayoutTree(this);
        ClientSize = new Size(Math.Max(420, ClientSize.Width), table.Height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } table)
            ClientSize = new Size(ClientSize.Width, table.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Increment(ref _sendGeneration);
            _effectUpdateTimer.Stop();
            _effectUpdateTimer.Dispose();
            _disposeCts.Cancel();
            if (_sendLock.Wait(1000))
            {
                _sendLock.Release();
                _sendLock.Dispose();
            }
            else
            {
                Logger.WriteLine("LightForm dispose timed out waiting for an in-flight command; lock left for safe completion.");
            }
            _disposeCts.Dispose();
        }
        base.Dispose(disposing);
    }
}
