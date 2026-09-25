using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯效回退缺口：仪表盘/唤醒必须采信已连接设备上真实的亮度写入结果；
/// 唤醒回退必须重放已有的 SetKeyboardEffect；服务没在跑时不得假装已切官方通道。
/// Unknown 与未尝试的写入不是失败，探测不得被跳过。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class KeyboardLightingFallbackGapTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";

    /// <summary>连接用的 step1/step2 成功；亮度 step3（0x12 … 0x08）可单独拒绝。</summary>
    sealed class BrightnessGateHid : HidDeviceWin
    {
        public bool RejectBrightnessWrite { get; set; }
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) =>
            !(RejectBrightnessWrite && IsBrightnessStep(report));
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }

        internal static bool IsBrightnessStep(byte[] report) =>
            report.Length > 4 && report[1] == 0x12 && report[4] == 0x08;
    }

    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly BrightnessGateHid Device = new();
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool? _deviceAvailable;

        public Harness(bool? deviceAvailable, bool serviceConnected)
        {
            _deviceAvailable = deviceAvailable;
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            LightingSettingsStore.Save(KeyboardTopic,
                new LightChannelSettings("Wave", 2, 3, Color.FromArgb(12, 34, 56).ToArgb(), PowerOn: true));
            LightingSettingsStore.Save(LightbarTopic,
                new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));
            LightingSettingsStore.Save(LogoTopic,
                new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));

            MechrevoHw? hardware = null;
            Func<string, object, Task>? publish = serviceConnected
                ? (topic, payload) =>
                {
                    if (payload is IDictionary<string, object> values)
                    {
                        var copy = new Dictionary<string, object>(values);
                        lock (_gate) Written.Add((topic, copy));
                        if (copy.TryGetValue("powerstatus", out object? power) &&
                            (topic == LightbarTopic || topic == LogoTopic || topic == KeyboardTopic))
                        {
                            string statusTopic = topic switch
                            {
                                LogoTopic => "HidLightbar_Logo/Status",
                                LightbarTopic => "HidLightbar/Status",
                                _ => "Keyboard/Status",
                            };
                            string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                            hardware!.HandleMessage(statusTopic,
                                $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                        }
                    }
                    return Task.CompletedTask;
                }
                : null;
            hardware = new MechrevoHw(publish, new MechrevoDeviceCapabilities
            {
                Lightbar = true,
                LogoLight = true,
                Keyboard = true,
            });
            if (serviceConnected)
            {
                hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
                hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            }
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = true };
            if (deviceAvailable is not null)
            {
                Keyboard.ResolveDeviceProbe = () => deviceAvailable.Value ? Device : null;
            }

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;

            Program.currentSource = Program.PowerSource.Barrel;
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(Written);
        }

        public void Dispose()
        {
            Program.rgb = _previousRgb!;
            Program.service = _previousService!;
            Program.hw = _previousHardware!;
            Program.lastObservedSource = _previousSource;
            Program.LightingIdleSuspended = _previousSuspended;
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousLightDir);
            Keyboard.Dispose();
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    static List<Dictionary<string, object>> KeyboardEffects(Harness harness) =>
        harness.Snapshot()
            .Where(entry => entry.Topic == KeyboardTopic &&
                entry.Payload.TryGetValue("function", out object? function) &&
                Equals(function, "SetEffectALL"))
            .Select(entry => entry.Payload)
            .ToList();

    /// <summary>
    /// Given 控制器已判定不支持且官方服务在线，When 唤醒恢复，
    /// Then 已保存的灯效经现有 SetKeyboardEffect 重放，且不重探、不进 HID。
    /// </summary>
    [Fact]
    public async Task UnsupportedResume_RestoresTheSavedEffectThroughSetKeyboardEffect()
    {
        using var harness = new Harness(deviceAvailable: false, serviceConnected: true);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        int probes = harness.Keyboard.ControllerProbeCount;
        int generation = harness.Keyboard.EffectGeneration;

        await Program.RestoreLightingWithRetryAsync(force: true);

        List<Dictionary<string, object>> effects = KeyboardEffects(harness);
        Assert.True(effects.Count == 1,
            "唤醒回退必须恰好重放一次 SetKeyboardEffect。实际 " + effects.Count);
        Assert.Equal("Wave", effects[0]["effect"]);
        Assert.Equal("2", effects[0]["light"]);
        Assert.Equal("3", effects[0]["speed"]);
        Assert.Equal(probes, harness.Keyboard.ControllerProbeCount);
        Assert.Equal(FeatureAvailability.Unsupported, harness.Keyboard.ControllerAvailability);
        Assert.False(harness.Keyboard.IsConnected);
        Assert.Equal(generation, harness.Keyboard.EffectGeneration);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
    }

    /// <summary>
    /// Given HID 已连接且判定为 Supported，但亮度写入被拒绝，When 唤醒恢复，
    /// Then 走官方通道重放灯效，而不是继续假装 HID 亮度生效。
    /// </summary>
    [Fact]
    public async Task SupportedResume_UsesGcuWhenTheConnectedBrightnessWriteFails()
    {
        using var harness = new Harness(deviceAvailable: true, serviceConnected: true);
        Assert.True(await harness.Keyboard.EnsureHidReadyAsync());
        Assert.Equal(FeatureAvailability.Supported, harness.Keyboard.ControllerAvailability);
        Assert.True(harness.Keyboard.IsConnected);
        harness.Device.RejectBrightnessWrite = true;
        int probes = harness.Keyboard.ControllerProbeCount;
        int generation = harness.Keyboard.EffectGeneration;

        await Program.RestoreLightingWithRetryAsync(force: true);

        List<Dictionary<string, object>> effects = KeyboardEffects(harness);
        Assert.True(effects.Count == 1,
            "已连接但亮度写入失败时，唤醒必须改走 SetKeyboardEffect。实际 " + effects.Count);
        Assert.Equal("Wave", effects[0]["effect"]);
        Assert.Equal(probes, harness.Keyboard.ControllerProbeCount);
        Assert.Equal(FeatureAvailability.Supported, harness.Keyboard.ControllerAvailability);
        Assert.Equal(generation, harness.Keyboard.EffectGeneration);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
    }

    /// <summary>
    /// Given 同上的静默拒绝，When 仪表盘刷新键盘效果目录，
    /// Then 目录是官方通道而不是 HID 名——不必先打开灯效窗。
    /// </summary>
    [Fact]
    public async Task Dashboard_UsesGcuCatalogWhenTheConnectedBrightnessWriteFails()
    {
        using var harness = new Harness(deviceAvailable: true, serviceConnected: true);
        Assert.True(await harness.Keyboard.EnsureHidReadyAsync());
        harness.Device.RejectBrightnessWrite = true;
        using var form = new SettingsForm();
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();

        var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
        ComboBox combo = row.Controls.OfType<ComboBox>().Single();
        string[] labels = combo.Items.Cast<object>().Select(item => combo.GetItemText(item) ?? "").ToArray();

        Assert.Contains("单色", labels);
        Assert.DoesNotContain("流畅彩虹", labels);
        Assert.Equal(FeatureAvailability.Supported, harness.Keyboard.ControllerAvailability);
        Assert.Equal(1, harness.Keyboard.ControllerProbeCount);
    }

    /// <summary>
    /// Given 控制器不支持且官方服务未连接，When 仪表盘显示键盘状态，
    /// Then 文案说明服务未运行，而不是宣称已经改用官方通道。
    /// </summary>
    [Fact]
    public async Task Unsupported_StatusNamesTheServiceWhenItIsNotRunning()
    {
        using var harness = new Harness(deviceAvailable: false, serviceConnected: false);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        using var form = new SettingsForm();
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();

        Label status = form.Controls.Find("labelKeyboardControllerStatus", true).OfType<Label>().Single();

        Assert.True(status.Visible);
        Assert.Equal("官方 GCU 服务未运行，软件灯效无法回退", status.Text);
        Assert.DoesNotContain("已改用官方通道", status.Text, StringComparison.Ordinal);
    }
}
