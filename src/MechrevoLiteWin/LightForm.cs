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
            if (Program.service is null || Program.hw is not { IsConnected: true })
            {
                // 设置已保存（重连后按保存值恢复），但这一次灯没有变化：必须让用户看见，不能静默。
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
                bool accepted = await Program.service.SetLightEffect(_topic, _effect, _light, _speed, "None",
                    LightingSettingsStore.ColorForEffect(_effect, _singleColor));
                if (!accepted) Logger.WriteLine($"Light effect was not accepted: topic={_topic}, effect={_effect}");
                NotifyApplyOutcome(accepted, cancelled: false);
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
        }

        void SaveSettings()
        {
            LightingSettingsStore.Save(_topic, new LightChannelSettings(
                _effect, _light, _speed, _singleColor.ToArgb(), _powerOn));
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

        var colorBtn = new RColorButton
        {
            Size = new Size(36, 24),
            BackColor = UiVisualStyle.Input,
            BorderColor = UiVisualStyle.Border,
            SwatchColor = _singleColor,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Left,
        };
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new RColorPicker(_singleColor, false);
            dlg.ColorChanged += c =>
            {
                _singleColor = c;
                colorBtn.SwatchColor = c;
                if (LightingSettingsStore.EffectUsesSingleColor(_effect)) ScheduleEffectUpdate();
            };
            dlg.ShowDialog(this);
        };
        AddRow("单色颜色", colorBtn);

        // 关灯时效果不显示（SetLightPower off）：参数行整体禁用，开灯恢复。
        // 电源开关由仪表盘灯条/Logo 行承担（对话框内重复开关已于 2026-09-13 移除）。
        void UpdateChannelEnabled()
        {
            bool enabled = LightingSettingsStore.Load(_topic, _effects[0].Effect).PowerOn;
            sliderLight.Enabled = enabled;
            comboSpeed.Enabled = enabled;
            colorBtn.Enabled = enabled;
        }
        UpdateChannelEnabled();

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
