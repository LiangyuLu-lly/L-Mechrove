using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯效恢复的「对齐」回归。
///
/// 键盘走应用自己的本地 HID 帧（毫秒级、无厂商服务），灯带/Logo 走 GCU/MQTT
/// （命令慢、回读可能永远 <c>not confirmed</c>）。两条路径必须**在同一个恢复周期内一起落地**，
/// 且任何一条厂商回读确认不到都不得阻塞、跳过或延迟可见应用——这正是此前三次翻烙饼的症结：
/// 键盘确认没来时被卡在 <c>StartMode</c> 之前（键盘黑）；把键盘修好后，外置通道又因为
/// <c>SetLightEffect</c> 挂在电源回读确认上而被跳过、并触发 20 轮重发（灯带/Logo 反复闪烁或保持暗）。
///
/// 本文件锁定两条契约：
/// 1) 外置确认**永远不来**时，三条通道仍各恰好应用一次，键盘不被厂商确认拖住；
/// 2) 三条通道的上电命令在同一恢复周期内下发（时间窗很小），不存在约 2 秒的路径错位。
/// </summary>
public class LightingAlignmentTests
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
        (string Topic, Dictionary<string, object> Payload, long Tick) entry, string topic, int status) =>
        entry.Topic == topic &&
        entry.Payload.TryGetValue("powerstatus", out object? value) &&
        Convert.ToInt32(value) == status;

    static int PowerCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload, long Tick)> written, string topic, int status) =>
        written.Count(entry => IsPower(entry, topic, status));

    static long PowerTick(
        IEnumerable<(string Topic, Dictionary<string, object> Payload, long Tick)> written, string topic, int status)
    {
        foreach (var entry in written)
            if (IsPower(entry, topic, status)) return entry.Tick;
        return -1;
    }

    static int EffectAllCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload, long Tick)> written, string topic) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"));

    /// <summary>
    /// 假 GCU：记录下发（含时间戳）；可为指定主题抑制电源状态回显，复现真机
    /// <c>SetLightPower(...) not confirmed</c>——命令已下发，但厂商永远不回读。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload, long Tick)> Written = new();
        public readonly KeyboardRgb Keyboard;
        public readonly FakeKeyboardHid KeyboardHid = new();

        readonly object _gate = new();
        readonly ConcurrentDictionary<string, bool> _suppressEcho = new(StringComparer.OrdinalIgnoreCase);
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
                    lock (_gate) Written.Add((topic, copy, Environment.TickCount64));

                    bool powerOn = copy.TryGetValue("powerstatus", out object? power) &&
                        Convert.ToInt32(power) == 1;
                    bool setPower = copy.TryGetValue("function", out object? function) && Equals(function, "SetPower");

                    // 命令已下发，但不回读状态：与该真机现象一致，确认永远不来。
                    if (setPower && powerOn && _suppressEcho.ContainsKey(topic))
                        return Task.CompletedTask;

                    if (copy.TryGetValue("powerstatus", out power))
                    {
                        string statusTopic = topic switch
                        {
                            LogoTopic => "HidLightbar_Logo/Status",
                            LightbarTopic => "HidLightbar/Status",
                            _ => "Keyboard/Status",
                        };
                        if (topic == LightbarTopic || topic == LogoTopic || topic == KeyboardTopic)
                        {
                            string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                            hardware!.HandleMessage(statusTopic,
                                $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                        }
                    }
                }
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, Keyboard = true });
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

        /// <summary>让该主题的电源命令只下发不回读（模拟 GCU 确认永远不来）。</summary>
        public void SuppressEcho(string topic) => _suppressEcho[topic] = true;

        public List<(string Topic, Dictionary<string, object> Payload, long Tick)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>, long)>(Written);
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
    /// 核心用例：外置通道的厂商确认**永远不来**时，三条通道仍然各恰好应用一次，
    /// 键盘不被厂商确认拖住（旧行为：约 2 秒 / 通道阻塞 + 效果被跳过 + 20 轮重发）。
    /// </summary>
    [Fact]
    public async Task IdleRestore_ExternalConfirmNeverArrives_AppliesAllThreeExactlyOnce()
    {
        using var harness = new Harness();
        harness.SuppressEcho(KeyboardTopic);
        harness.SuppressEcho(LightbarTopic);
        harness.SuppressEcho(LogoTopic);

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        int requestId = Program.LightingRestoreRequestId;
        int generationBefore = harness.Keyboard.EffectGeneration;

        var stopwatch = Stopwatch.StartNew();
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        stopwatch.Stop();
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        var written = harness.Snapshot();

        // 1) 本地 HID 效果不依赖 GCU 回读：仍在同一周期内启动。
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);

        // 2) 三条通道各恰好一次上电；外置效果各恰好一次（不再被回读确认挡掉）。
        Assert.Equal(1, PowerCount(written, KeyboardTopic, 1));
        Assert.Equal(1, PowerCount(written, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(written, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(written, LightbarTopic));
        Assert.Equal(1, EffectAllCount(written, LogoTopic));

        // 3) 已下发的通道立即记账，重试不会被触发（不会 20 轮重发）。
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LightbarTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LogoTopic));
        Assert.Equal(requestId, Program.LightingRestoreRequestId);

        // 4) 键盘没有被厂商确认按住约 2 秒（旧行为约 7s 串行阻塞 + 50s 重发）。
        Assert.True(stopwatch.ElapsedMilliseconds < 1500,
            $"恢复耗时 {stopwatch.ElapsedMilliseconds}ms，键盘被厂商确认拖住。");
    }

    /// <summary>
    /// 对齐契约：三条通道的上电命令必须在同一个恢复周期内下发，时间差远小于厂商确认超时
    /// （不存在「本地 HID 先亮约 2 秒、外置才跟上」的路径错位）。
    /// </summary>
    [Fact]
    public async Task IdleRestore_AllThreeDispatchInSameCycle_WithinAlignmentWindow()
    {
        using var harness = new Harness();
        harness.SuppressEcho(KeyboardTopic);
        harness.SuppressEcho(LightbarTopic);
        harness.SuppressEcho(LogoTopic);

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);

        var written = harness.Snapshot();
        long keyboardTick = PowerTick(written, KeyboardTopic, 1);
        long lightbarTick = PowerTick(written, LightbarTopic, 1);
        long logoTick = PowerTick(written, LogoTopic, 1);

        Assert.True(keyboardTick >= 0 && lightbarTick >= 0 && logoTick >= 0,
            "三条通道都必须在同一恢复周期内上电。");

        long spread = Math.Max(keyboardTick, Math.Max(lightbarTick, logoTick)) -
            Math.Min(keyboardTick, Math.Min(lightbarTick, logoTick));
        Assert.True(spread <= 1000,
            $"同一恢复周期内三条通道上电时间差 {spread}ms，超过对齐窗口（预期同一轮并发下发）。");

        // 外置效果在同周期重放，且各一次。
        Assert.Equal(1, EffectAllCount(written, LightbarTopic));
        Assert.Equal(1, EffectAllCount(written, LogoTopic));
    }
}
