using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 第一次开机的回退决策不能用探测前的快照。探测把控制器连上并记下真实的亮度写入拒绝后，
/// 同一次点击必须改走官方通道，而不是照旧启动 HID。
/// Unknown / 未尝试的写入不是失败，亮度写入成功时仍走 HID。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class KeyboardLightingStaleFallbackTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
            await Task.Delay(10);
    }

    /// <summary>冷连接把 StartMode 放在 Invoke 里。测试线程不泵消息的话，那次启动永远排不进队列，断言会假绿。</summary>
    static void Pump(int milliseconds)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    static void DetachUiContext() => SynchronizationContext.SetSynchronizationContext(null);

    /// <summary>连接用的 step1/step2 成功；亮度 step3（0x12 … 0x08）可单独拒绝。</summary>
    sealed class BrightnessGateHid : HidDeviceWin
    {
        public bool RejectBrightnessWrite { get; set; }
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) =>
            !(RejectBrightnessWrite && IsBrightnessStep(report));
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }

        static bool IsBrightnessStep(byte[] report) =>
            report.Length > 4 && report[1] == 0x12 && report[4] == 0x08;
    }

    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly BrightnessGateHid Device = new();
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public bool RejectEffectPublish;

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly SettingsForm? _previousSettings;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool _previousAudit;

        public Harness(bool rejectBrightnessWrite)
        {
            Device.RejectBrightnessWrite = rejectBrightnessWrite;
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            LightingSettingsStore.Save(KeyboardTopic,
                new LightChannelSettings("Wave", 2, 3, Color.FromArgb(12, 34, 56).ToArgb(), PowerOn: true));

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    if (RejectEffectPublish && copy.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"))
                        throw new InvalidOperationException("effect publish failed");
                    lock (_gate) Written.Add((topic, copy));
                    if (copy.TryGetValue("powerstatus", out object? power) && topic == KeyboardTopic)
                    {
                        string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                        hardware!.HandleMessage("Keyboard/Status",
                            $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                    }
                }
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { Keyboard = true });
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = false };
            Keyboard.ResolveDeviceProbe = () => Device;

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSettings = Program.settingsForm;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;
            _previousAudit = Program.UiAuditMode;

            Program.currentSource = Program.PowerSource.Barrel;
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        public int CountEffectAll()
        {
            lock (_gate) return Written.Count(entry => entry.Topic == KeyboardTopic &&
                entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"));
        }

        public Dictionary<string, object>? SingleEffectAll()
        {
            lock (_gate)
            {
                var matches = Written.Where(entry => entry.Topic == KeyboardTopic &&
                    entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")).ToList();
                return matches.Count == 1 ? matches[0].Payload : null;
            }
        }

        public void Dispose()
        {
            Program.settingsForm = _previousSettings!;
            Program.rgb = _previousRgb!;
            Program.service = _previousService!;
            Program.hw = _previousHardware!;
            Program.lastObservedSource = _previousSource;
            Program.LightingIdleSuspended = _previousSuspended;
            Program.UiAuditMode = _previousAudit;
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousLightDir);
            Keyboard.Dispose();
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    static SettingsForm BuildKeyboardForm(out RCheckBox powerSwitch)
    {
        var form = new SettingsForm();
        form.CreateControl();
        var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
        powerSwitch = row.Controls.OfType<RCheckBox>().Single();
        return form;
    }

    /// <summary>
    /// Given 开机时控制器尚未连接（Unknown），官方服务在线，探测会连上但亮度 step3 被拒绝，
    /// When 用户打开键盘电源，
    /// Then 同一次点击不得启动 HID，并且必须改走官方通道下发已保存的效果。
    /// </summary>
    [Fact]
    public async Task PowerOn_AfterProbeRecordsBrightnessRejection_DoesNotStartHidAndUsesGcu()
    {
        using var harness = new Harness(rejectBrightnessWrite: true);
        Assert.Equal(FeatureAvailability.Unknown, harness.Keyboard.ControllerAvailability);
        Assert.False(harness.Keyboard.IsConnected);
        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out RCheckBox powerSwitch);
        Program.UiAuditMode = false;
        DetachUiContext();
        int generation = harness.Keyboard.EffectGeneration;

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode || harness.CountEffectAll() > 0);
        Pump(200);

        Assert.Equal(FeatureAvailability.Supported, harness.Keyboard.ControllerAvailability);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
        Assert.Equal(generation, harness.Keyboard.EffectGeneration);
        Assert.False(harness.Keyboard.BrightnessWriteTookEffectForRouting());
        Assert.True(harness.CountEffectAll() > 0,
            "探测已经记下亮度写入拒绝后，同一次开机必须改走官方通道，而不是启动 HID。");
    }

    /// <summary>
    /// Given 同上，但入口是灯效窗的冷连接，
    /// When 应用当前模式，
    /// Then 探测记下拒绝后不得 StartMode。
    /// </summary>
    [Fact]
    public async Task ApplyMode_AfterProbeRecordsBrightnessRejection_DoesNotStartHid()
    {
        using var harness = new Harness(rejectBrightnessWrite: true);
        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        form.CreateControl();
        int generation = harness.Keyboard.EffectGeneration;
        MethodInfo? apply = typeof(RgbForm).GetMethod("ApplyModeSelection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);

        apply!.Invoke(form, new object?[] { true });
        // 必须留在创建句柄的线程上泵消息：await 会切走，Invoke 里的 StartMode 永远跑不到，断言会假绿。
        long until = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < until && harness.Keyboard.ActiveMode < 0)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }

        try
        {
            Assert.Equal(-1, harness.Keyboard.ActiveMode);
            Assert.Equal(generation, harness.Keyboard.EffectGeneration);
            Assert.False(harness.Keyboard.BrightnessWriteTookEffectForRouting());
            Dictionary<string, object>? effect = harness.SingleEffectAll();
            Assert.NotNull(effect);
            Assert.Equal("Wave", effect!["effect"]);
            Assert.Equal("NOT_SAVE", effect["nv_save"]);
        }
        finally
        {
            harness.Keyboard.StopCurrentEffect();
            Pump(100);
        }
    }

    /// <summary>
    /// Given 控制器已判定不支持、官方服务在线、键盘电源已开，
    /// When 灯效窗走回退分支，
    /// Then 必须用现有 SetKeyboardEffect 重放已保存效果，而不是只改状态文字。
    /// </summary>
    [Fact]
    public async Task FallbackBranch_RestoresSavedEffectViaSetKeyboardEffect()
    {
        using var harness = new Harness(rejectBrightnessWrite: true);
        harness.Keyboard.ResolveDeviceProbe = () => null;
        DetachUiContext();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");
        Assert.True(harness.Hardware.KeyboardPower);
        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        form.CreateControl();
        DetachUiContext();
        int generation = harness.Keyboard.EffectGeneration;
        MethodInfo? apply = typeof(RgbForm).GetMethod("ApplyModeSelection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);

        apply!.Invoke(form, new object?[] { true });
        await WaitUntil(() => harness.CountEffectAll() > 0);

        Dictionary<string, object>? effect = harness.SingleEffectAll();
        Assert.NotNull(effect);
        Assert.Equal("Wave", effect!["effect"]);
        Assert.Equal("2", effect["light"]);
        Assert.Equal("3", effect["speed"]);
        Assert.Equal("NOT_SAVE", effect["nv_save"]);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
        Assert.Equal(generation, harness.Keyboard.EffectGeneration);
    }

    /// <summary>
    /// Given 已走官方通道，电源下发成功但效果发布失败，
    /// When 用户打开键盘电源，
    /// Then 开关不得停在开。
    /// </summary>
    [Fact]
    public async Task PowerOn_RollsSwitchOffWhenEffectPublishFails()
    {
        using var harness = new Harness(rejectBrightnessWrite: true);
        harness.Keyboard.ResolveDeviceProbe = () => null;
        DetachUiContext();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.RejectEffectPublish = true;
        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out RCheckBox powerSwitch);
        Program.UiAuditMode = false;
        DetachUiContext();

        powerSwitch.Checked = true;
        await WaitUntil(() => powerSwitch.Enabled && !powerSwitch.Checked);

        Assert.False(powerSwitch.Checked);
        Assert.False(harness.Keyboard.KbPowerOn);
    }

    /// <summary>
    /// Given 已连接且先前的亮度写入成功被缓存，
    /// When 效果帧的 step3 被拒绝，
    /// Then 路由能看见这次失败，后续成功帧也不能把失败藏到断线为止。
    /// </summary>
    [Fact]
    public async Task EffectFrameStep3Rejection_SticksInRoutingUntilDisconnect()
    {
        using var harness = new Harness(rejectBrightnessWrite: false);
        Assert.True(await harness.Keyboard.EnsureHidReadyAsync());
        Assert.True(harness.Keyboard.BrightnessWriteTookEffectForRouting());
        harness.Device.RejectBrightnessWrite = true;
        // 真重连会拆流并清掉观察。这里只证明：随后一帧 step3 成功也不能把已记下的拒绝藏起来。
        harness.Keyboard.ReconnectProbe = () => true;

        Assert.False(harness.Keyboard.SendFrame(new byte[KeyboardRgb.BufSize]));
        Assert.False(harness.Keyboard.BrightnessWriteTookEffectForRouting());

        await Task.Delay(50);
        harness.Device.RejectBrightnessWrite = false;
        Assert.True(harness.Keyboard.SendFrame(new byte[KeyboardRgb.BufSize]));
        Assert.False(harness.Keyboard.BrightnessWriteTookEffectForRouting());
    }

    /// <summary>
    /// Given 尚未连接、判定仍是 Unknown，
    /// When 发帧因为没有流而失败，
    /// Then 这不是亮度写入失败，路由保持 true。
    /// </summary>
    [Fact]
    public void MissingStream_DoesNotCountAsBrightnessWriteFailure()
    {
        using var harness = new Harness(rejectBrightnessWrite: false);
        Assert.Equal(FeatureAvailability.Unknown, harness.Keyboard.ControllerAvailability);
        Assert.False(harness.Keyboard.IsConnected);

        Assert.False(harness.Keyboard.SendFrame(new byte[KeyboardRgb.BufSize]));

        Assert.True(harness.Keyboard.BrightnessWriteTookEffectForRouting());
        Assert.Equal(FeatureAvailability.Unknown, harness.Keyboard.ControllerAvailability);
    }
}
