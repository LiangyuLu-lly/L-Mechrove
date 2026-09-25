using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯光睡眠三合一回归（键盘 / 灯带 / Logo）。
///
/// 用户反馈三个缺陷：
/// 1) 空闲熄灯后灯带/Logo 开关变 OFF，键盘开关仍 ON —— 三条通道回显不一致；
/// 2) 三条通道各自在不同时刻熄灭 —— 键盘还有第二条按设备计时的睡眠时钟；
/// 3) 睡眠结束恢复时灯带/Logo 反复闪烁 —— 同一熄灯周期的恢复被重复上电重放。
///
/// 单一语义决定：开关表示「这条灯此刻亮着」（设备现实），不是「用户曾启用过」。
/// 临时熄灯期间三条通道实际都是暗的，因此三个开关必须一起回落到关。
/// </summary>
public class LightingSleepConsistencyTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";
    const string KeyboardTopic = "Keyboard/Ctrl";

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

    static bool IsPower(
        (string Topic, Dictionary<string, object> Payload) entry, string topic, int status) =>
        entry.Topic == topic &&
        entry.Payload.TryGetValue("powerstatus", out object? value) &&
        Convert.ToInt32(value) == status;

    static int PowerCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic, int status) =>
        written.Count(entry => IsPower(entry, topic, status));

    static int EffectAllCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"));

    /// <summary>
    /// 共享夹具：假 GCU（回显电源状态、记录下发）+ 假键盘 HID 意图 + 临时灯光配置目录。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly KeyboardRgb Keyboard;

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;        readonly bool _previousSuspended;

        public Harness()
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
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, Keyboard = true });
            hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = true };

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;

            Program.currentSource = Program.PowerSource.Barrel;   // 不因离电规则意外熄灯
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(Written);
        }

        public void Clear()
        {
            lock (_gate) Written.Clear();
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

    /// <summary>
    /// BUG 1：临时熄灯期间三条通道都暗，三个开关必须一起显示关；恢复后一起显示开。
    /// 修复前键盘开关只读 KbPowerOn（用户意图），设备已灭仍显示开。
    /// </summary>
    [Fact]
    public void Switches_UnderTemporarySuspend_AllThreeReflectTheDarkDevice()
    {
        using var harness = new Harness();
        bool previousAudit = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            harness.Hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");
            harness.Hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");

            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            MethodInfo sync = typeof(SettingsForm).GetMethod("SyncLightRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            RCheckBox Switch(string rowName) => form.Controls.Find(rowName, true)
                .OfType<TableLayoutPanel>().Single().Controls.OfType<RCheckBox>().Single();

            sync.Invoke(form, null);
            Assert.True(Switch("rowKeyboard").Checked, "前置：未熄灯时键盘开关应为开（KbPowerOn=true）。");
            Assert.True(Switch("rowLightbar").Checked, "前置：未熄灯时灯带开关应为开。");
            Assert.True(Switch("rowLogo").Checked, "前置：未熄灯时 Logo 开关应为开。");

            Program.LightingIdleSuspended = true;   // 空闲熄灯已触发，三通道实际都暗
            sync.Invoke(form, null);

            Assert.False(Switch("rowKeyboard").Checked, "设备已暗，键盘开关不得仍显示开。");
            Assert.False(Switch("rowLightbar").Checked, "设备已暗，灯带开关不得仍显示开。");
            Assert.False(Switch("rowLogo").Checked, "设备已暗，Logo 开关不得仍显示开。");

            Program.LightingIdleSuspended = false;  // 恢复后三通道一起回到用户意图
            sync.Invoke(form, null);
            Assert.True(Switch("rowKeyboard").Checked && Switch("rowLightbar").Checked && Switch("rowLogo").Checked,
                "恢复后三条通道的开关必须一致回到开。");
        }
        finally
        {
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// BUG 2 + BUG 3：一次空闲到期判定同时熄灭三条通道；恢复在同一熄灯周期内只应用一次，
    /// 后续重复触发（唤醒恢复/连接恢复/电源握手/重试）不得再对灯带/Logo 上电重放。
    /// </summary>
    [Fact]
    public async Task SuspendDimsAllThree_AndRestoreAppliesExternalExactlyOncePerCycle()
    {
        using var harness = new Harness();

        // 空闲到期：单一判定 → 三通道一起熄。
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");
        Assert.Equal(-1, harness.Keyboard.ActiveMode);   // 键盘效果已停（StopCurrentEffect 置 -1）
        Assert.Equal(1, PowerCount(harness.Snapshot(), LightbarTopic, 0));
        Assert.Equal(1, PowerCount(harness.Snapshot(), LogoTopic, 0));

        // 用户输入唤醒：三通道一起恢复。
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        var afterFirstRestore = harness.Snapshot();
        Assert.Equal(1, PowerCount(afterFirstRestore, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(afterFirstRestore, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(afterFirstRestore, LightbarTopic));
        Assert.Equal(1, EffectAllCount(afterFirstRestore, LogoTopic));

        // 同一熄灯周期的第二次恢复（唤醒恢复/连接恢复/电源握手会相继触发）必须被吞掉。
        await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);

        var afterDuplicateRestore = harness.Snapshot();
        Assert.Equal(1, PowerCount(afterDuplicateRestore, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(afterDuplicateRestore, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(afterDuplicateRestore, LightbarTopic));
        Assert.Equal(1, EffectAllCount(afterDuplicateRestore, LogoTopic));
    }

    /// <summary>
    /// BUG 2：键盘不得再持有按设备计时的睡眠循环——唯一的到期判定在应用级空闲监视里。
    /// 与 SubLightbarTests 的移除守卫同款：以反射断言旧符号不再存在。
    /// </summary>
    [Fact]
    public void KeyboardRgb_NoLongerOwnsAPerDeviceSleepTimer()
    {
        Assert.Null(typeof(KeyboardRgb).GetMethod("WaitForInput",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.Null(typeof(KeyboardRgb).GetMethod("LastInputMs",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
    }
}
