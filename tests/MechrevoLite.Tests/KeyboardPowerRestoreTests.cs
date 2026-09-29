using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 回归：空闲睡眠恢复时键盘不亮——GCU 对键盘电源的回读「未确认」不得阻止本地 HID 效果恢复。
///
/// 真机现象（%AppData%\MechrevoLite\log.txt）：空闲恢复时
/// <c>SetLightPower(Keyboard/Ctrl, True) not confirmed</c> 之后键盘一直是
/// <c>keyboard=Off</c>，从不再出现 <c>RGB 自动恢复：HID 效果 1 已运行</c>。
/// 根因是 <c>Program.RestoreKeyboardLightingAsync</c> 把未确认的电源写当成致命错误直接
/// <c>return false</c>，在 <c>rgb.StartMode</c> 之前就退出，键盘效果线程永远不会(重)启。
///
/// 契约（本文件锁定）：
/// 1) 电源回读未确认时，键盘效果仍必须恢复（本地 HID 不依赖 GCU 回读）；
/// 2) 一次空闲睡眠 → 输入周期内，键盘电源只写一次、效果只(重)启一次（与外置通道同一「每周期一次」语义）；
/// 3) 未确认的电源写不得妨碍灯带/Logo 的既有恢复。
/// </summary>
public class KeyboardPowerRestoreTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";
    const string KeyboardTopic = "Keyboard/Ctrl";

    /// <summary>确定性 HID：不触真实设备，帧写入恒成功，用于让效果线程可观测地跑起来。</summary>
    sealed class FakeKeyboardHid : HidDeviceWin
    {
        public override bool SetFeature(byte[] report) => true;
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }
    }

    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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
    /// 假 GCU：记录下发；键盘电源命令可切换为「下发但不回读」以模拟真机的
    /// <c>SetLightPower(Keyboard/Ctrl, True) not confirmed</c>。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly KeyboardRgb Keyboard;
        public readonly FakeKeyboardHid KeyboardHid = new();

        readonly object _gate = new();
        volatile bool _suppressKeyboardPowerOnEcho;
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;

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

                    bool powerOn = copy.TryGetValue("powerstatus", out object? power) &&
                        Convert.ToInt32(power) == 1;
                    bool setPower = copy.TryGetValue("function", out object? function) && Equals(function, "SetPower");

                    // 键盘电源命令已下发，但 GCU 不回读 Keyboard/Status → 与该真机现象一致，确认失败。
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
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, MbaLogo = true, Keyboard = true });
            hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"), KeyboardHid) { KbPowerOn = true };

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

        /// <summary>让键盘后续的上电命令只下发、不回读，复现真机「未确认」。</summary>
        public void SuppressKeyboardPowerOnEcho() => _suppressKeyboardPowerOnEcho = true;

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

    /// <summary>
    /// 空闲睡眠 → 输入：键盘上电回读不确认时，本地 HID 效果仍必须恢复，且一次周期内
    /// 键盘电源恰好写一次、效果恰好(重)启一次；后续同周期触发不得重复。
    /// </summary>
    [Fact]
    public async Task IdleRestore_UnconfirmedKeyboardPower_StillStartsEffectExactlyOnce()
    {
        using var harness = new Harness();

        // 空闲到期：三通道一起熄（键盘电源断电回读正常，临时熄灯标志置位）。
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");
        Assert.Equal(-1, harness.Keyboard.ActiveMode);

        int generationBefore = harness.Keyboard.EffectGeneration;
        int requestId = Program.LightingRestoreRequestId;

        // 用户返回：键盘上电命令不再回读（真机 not confirmed）。
        harness.SuppressKeyboardPowerOnEcho();
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        // 1) 效果恢复：未确认的电源写不得阻止 StartMode。
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);

        var written = harness.Snapshot();
        // 2) 键盘电源只写一次；灯带/Logo 的既有恢复不受影响（各一次）。
        Assert.Equal(1, PowerCount(written, KeyboardTopic, 1));
        Assert.Equal(1, PowerCount(written, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(written, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(written, LightbarTopic));
        Assert.Equal(1, EffectAllCount(written, LogoTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LightbarTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LogoTopic));

        // 3) 同一周期的后续触发（唤醒/连接/电源握手会相继触发）不得重复写电源或重启效果。
        await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);

        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);
        Assert.Equal(1, PowerCount(harness.Snapshot(), KeyboardTopic, 1));
        Assert.Equal(requestId, Program.LightingRestoreRequestId);
    }

    /// <summary>
    /// 反向对照：若把未确认的电源写重新当成致命（旧行为），本用例会在效果断言处失败——
    /// <c>ActiveMode</c> 停在 -1、<c>EffectGeneration</c> 不前进。
    /// </summary>
    [Fact]
    public async Task IdleRestore_UnconfirmedKeyboardPower_EffectIsRunningAndFramesFlow()
    {
        using var harness = new Harness();
        harness.SuppressKeyboardPowerOnEcho();

        await Program.EvaluateLightingIdleAsync(10, 10_000);
        await Program.EvaluateLightingIdleAsync(10, 200);

        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.True(harness.Keyboard.IsConnected, "恢复后键盘 HID 应处于连接态。");
    }
}
