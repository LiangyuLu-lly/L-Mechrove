using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// T3 第 0 步表征（先于任何生产改线）：为 SUPPORTED 机型钉住今天的行为——
/// 键盘行 ON 的 HID 路径 + 并发的固件电源旁路（Settings.V2.cs:394-396）、
/// RgbForm 冷连接的 HID 路径（RgbForm.cs:276-316）、空闲恢复序列（Program.cs:1140-1189）。
/// 改线后这些用例必须原样绿：任何发布数量/顺序/命令种类的变化都是对可用机型的回归。
/// </summary>
public class KeyboardFallbackCharacterizationTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";

    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
            await Task.Delay(10);
    }

    /// <summary>确定性 HID：不触真实设备，逐次记录调用（帧数量与自定义模式进出都要可断言）。</summary>
    sealed class RecordingKeyboardHid : HidDeviceWin
    {
        readonly Action<string> _record;
        public RecordingKeyboardHid(Action<string> record) => _record = record;
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) { _record("HID:SetFeature"); return true; }
        public override bool Write(byte[] report) { _record("HID:Write"); return true; }
        public override void Dispose() { }
    }

    static string Describe(string topic, Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("powerstatus", out object? power)) return $"{topic} powerstatus={power}";
        if (payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")) return $"{topic} SetEffectALL";
        if (payload.TryGetValue("Action", out object? action)) return $"{topic} Action={action}";
        return $"{topic} publish";
    }

    /// <summary>假 GCU（记录下发 + 回显状态）+ 记录型假 HID + 临时灯光配置目录，共享一条有序事件流。</summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly List<string> Events = new();

        readonly object _gate = new();
        volatile bool _suppressKeyboardPowerOnEcho;
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool _previousAudit;

        public Harness(bool keyboardConnected = true, bool kbPowerOn = true)
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            LightingSettingsStore.Save(LightbarTopic,
                new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));
            LightingSettingsStore.Save(LogoTopic,
                new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    lock (_gate)
                    {
                        Written.Add((topic, copy));
                        Events.Add("GCU:" + Describe(topic, copy));
                    }

                    bool powerOn = copy.TryGetValue("powerstatus", out object? power) &&
                        Convert.ToInt32(power) == 1;
                    bool setPower = copy.TryGetValue("function", out object? function) && Equals(function, "SetPower");

                    if (setPower && powerOn && topic == KeyboardTopic && _suppressKeyboardPowerOnEcho)
                        return Task.CompletedTask;

                    if (copy.TryGetValue("powerstatus", out power) &&
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
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, Keyboard = true });
            hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            Hardware = hardware;

            Keyboard = keyboardConnected
                ? new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"), new RecordingKeyboardHid(RecordHid)) { KbPowerOn = kbPowerOn }
                : new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = kbPowerOn };

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;
            _previousAudit = Program.UiAuditMode;

            Program.currentSource = Program.PowerSource.Barrel;   // 不因离电规则意外熄灯
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        public void RecordHid(string op)
        {
            lock (_gate) Events.Add(op);
        }

        /// <summary>让固件回读「键盘电源关」，使 ON 路径的并发固件电源旁路条件成立。</summary>
        public void SetFirmwareKeyboardPower(bool on) =>
            Hardware.HandleMessage("Keyboard/Status",
                "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"" + (on ? "On" : "Off") + "\"}");

        /// <summary>让键盘后续的上电命令只下发、不回读（真机 not confirmed）。</summary>
        public void SuppressKeyboardPowerOnEcho() => _suppressKeyboardPowerOnEcho = true;

        public List<string> SnapshotEvents()
        {
            lock (_gate) return new List<string>(Events);
        }

        public void ClearEvents()
        {
            lock (_gate) Events.Clear();
        }

        /// <summary>清空事件流与下发记录：后续计数只反映本阶段。</summary>
        public void Clear()
        {
            lock (_gate) { Events.Clear(); Written.Clear(); }
        }

        public int CountGcu(string topic, int powerstatus)
        {
            lock (_gate) return Written.Count(entry => entry.Topic == topic &&
                entry.Payload.TryGetValue("powerstatus", out object? value) && Convert.ToInt32(value) == powerstatus);
        }

        public int CountEffectAll(string topic)
        {
            lock (_gate) return Written.Count(entry => entry.Topic == topic &&
                entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"));
        }

        public int CountHidOps()
        {
            lock (_gate) return Events.Count(op => op.StartsWith("HID:", StringComparison.Ordinal));
        }

        public void Dispose()
        {
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
        form.PerformLayout();
        var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
        powerSwitch = row.Controls.OfType<RCheckBox>().Single();
        return form;
    }

    /// <summary>
    /// 表征 1：SUPPORTED 机型键盘行 ON——HID 出帧（含固件电源落地后的重申）与
    /// **并发的**固件电源旁路各发生一次；HID 分支绝不发固件效果（SetEffectALL）。
    /// </summary>
    [Fact]
    public async Task Supported_PowerSwitchOn_KeepsHidPathAndConcurrentFirmwarePowerBypass()
    {
        using var harness = new Harness(kbPowerOn: false);   // 开关关 → 置位才会触发处理器
        harness.SetFirmwareKeyboardPower(false);   // 固件电源旁路（Settings.V2.cs:394-396）的条件
        Program.UiAuditMode = true;                // 表单构造不碰真实设备；HID 分支本身不看审计门

        using var form = BuildKeyboardForm(out var powerSwitch);
        int generationBefore = harness.Keyboard.EffectGeneration;
        harness.Clear();

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode);

        Assert.True(harness.Keyboard.KbPowerOn, "ON 必须落地应用内 HID 意图。");
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);   // 重申不重启效果线程
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));                     // 固件电源旁路恰好一次
        Assert.Equal(0, harness.CountEffectAll(KeyboardTopic));                  // HID 分支不发固件效果
        Assert.True(harness.CountHidOps() > 0, "HID 分支必须真的进入自定义帧模式并出帧。");
    }

    /// <summary>
    /// 表征 2：SUPPORTED 机型 RgbForm 已连接态应用模式——重进自定义帧模式 + 启动效果（HID 路径），
    /// 固件电源已开时不发任何 GCU 命令，且不触发探测（仅在 HID 缺席时才需要判定）。
    /// （冷连接分支共用 KeyboardRgb.Connect 的同一实现体，由 KeyboardControllerProbeTests 钉住。）
    /// </summary>
    [Fact]
    public async Task Supported_ConnectedRgbFormApply_ReentersCustomModeAndStartsEffect()
    {
        using var harness = new Harness();
        harness.SetFirmwareKeyboardPower(true);   // 固件电源已开：连接态路径不应补发 GCU 电源
        Program.UiAuditMode = true;               // 不启动模式跟随计时器；不改 HID 路径本身

        using var form = new RgbForm(harness.Keyboard);
        harness.Clear();
        int reinitBefore = harness.Keyboard.CustomModeReinitCount;

        MethodInfo? apply = typeof(RgbForm).GetMethod("ApplyModeSelection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);
        apply!.Invoke(form, new object?[] { true });

        await WaitUntil(() => harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode);

        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(reinitBefore + 1, harness.Keyboard.CustomModeReinitCount);   // 固件效果模式下必须重进帧模式
        Assert.True(harness.CountHidOps() > 0, "应用模式后必须出帧。");
        Assert.Equal(0, harness.CountGcu(KeyboardTopic, 1));
        Assert.Equal(0, harness.CountGcu(KeyboardTopic, 0));
        Assert.Equal(0, harness.Keyboard.ControllerProbeCount);   // 已连接路径不探测
        // 收尾的设备侧计时关闭（SyncDeviceCloseTimerAsync，150ms 延迟）必须在本用例窗口内落地：
        // 它是即发即弃的 GCU 发布，迟到会漂进下一个用例的假 GCU（Program.service 是全局的）。
        await WaitUntil(() => harness.SnapshotEvents()
            .Any(e => e.Contains("Setting/Control") && e.Contains("KEYBOARD_LIGHTBAR_TIMER_OFF")));
        Assert.Contains(harness.SnapshotEvents(),
            e => e.Contains("Setting/Control") && e.Contains("KEYBOARD_LIGHTBAR_TIMER_OFF"));
    }

    /// <summary>
    /// 表征 3：SUPPORTED 机型空闲恢复序列（Program.cs:1140-1189）——
    /// 电源下发 → 设备侧计时关闭 → 进入自定义帧模式并启动效果，顺序与数量都不变；
    /// 同一熄灯周期的后续触发不得重复写电源或重启效果。
    /// </summary>
    [Fact]
    public async Task Supported_IdleRestore_KeepsOrderAndPerCycleCounts()
    {
        using var harness = new Harness();
        harness.SuppressKeyboardPowerOnEcho();   // 真机：键盘上电回读长期 not confirmed

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");
        Assert.Equal(-1, harness.Keyboard.ActiveMode);

        int generationBefore = harness.Keyboard.EffectGeneration;
        harness.Clear();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        List<string> events = harness.SnapshotEvents();
        // 取**首次**下发：本轮恢复自己的 GCU 写必须先于 HID 效果启动。外置通道确认失败时
        // RestoreLightingWithRetryAsync 会补跑一轮（可能再次下发计时关闭），那属于既有重试语义，
        // 不在本表征范围内（由 LightingRestoreRetryTests 单独钉）。
        int powerOn = events.FindIndex(e => e == $"GCU:{KeyboardTopic} powerstatus=1");
        int closeTimerOff = events.FindIndex(e => e.Contains("Setting/Control") && e.Contains("KEYBOARD_LIGHTBAR_TIMER_OFF"));
        int firstHid = events.FindIndex(e => e.StartsWith("HID:", StringComparison.Ordinal));

        Assert.True(powerOn >= 0, "恢复必须下发键盘上电。事件：" + string.Join(" | ", events));
        Assert.True(closeTimerOff >= 0, "恢复必须下发设备侧计时关闭。事件：" + string.Join(" | ", events));
        Assert.True(firstHid >= 0, "恢复必须启动本地 HID 效果。事件：" + string.Join(" | ", events));
        Assert.True(powerOn < closeTimerOff, "键盘上电必须先于设备侧计时关闭。事件：" + string.Join(" | ", events));
        Assert.True(powerOn < firstHid, "键盘上电必须先于 HID 效果启动。事件：" + string.Join(" | ", events));
        Assert.True(closeTimerOff < firstHid, "设备侧计时关闭必须先于 HID 效果启动。事件：" + string.Join(" | ", events));
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));    // 一个周期恰好一次电源写
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);

        // 同一熄灯周期的后续触发（唤醒/连接/电源握手会相继触发）不得重复写电源或重启效果。
        int requestId = Program.LightingRestoreRequestId;
        harness.Clear();
        await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);

        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);
        Assert.Equal(0, harness.CountGcu(KeyboardTopic, 1));
        Assert.Equal(requestId, Program.LightingRestoreRequestId);
    }
}
