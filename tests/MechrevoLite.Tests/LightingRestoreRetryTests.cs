using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯效空闲恢复的外置通道重试回归（灯带 / Logo）。
///
/// 用户反馈：空闲睡眠触发后，回到机器时键盘灯恢复，但灯带/Logo 仍然是暗的，
/// 要等下一次无关事件才亮。根因是空闲恢复直接调用 ReconcileLightingPowerAsync，
/// 外置通道确认失败时既不重试也不记账，恢复结果被丢弃。
///
/// 这里锁定两点：
/// 1) 外置确认失败后，空闲恢复必须走有界重试直到确认（每条通道恰好上电一次，不重复闪烁）；
/// 2) 幂等令牌按通道记账，只有该通道确认成功才写入。
/// </summary>
public class LightingRestoreRetryTests
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
    /// 共享夹具：假 GCU（回显电源状态、可注入指定通道的确认失败）+ 假键盘 HID 意图 + 临时灯光配置目录。
    /// 与 LightingSleepConsistencyTests 的夹具同构，额外提供 FailNextPowerOn 注入。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly KeyboardRgb Keyboard;

        readonly object _gate = new();
        readonly ConcurrentDictionary<string, int> _failPowerOn = new(StringComparer.OrdinalIgnoreCase);
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
                    bool powerOn = copy.TryGetValue("powerstatus", out object? power) &&
                        Convert.ToInt32(power) == 1;
                    bool setPower = copy.TryGetValue("function", out object? function) && Equals(function, "SetPower");

                    // 注入口：让指定通道的下一次上电确认失败，模拟 GCU 回执超时。
                    // 在记录写入之前抛出——失败的下发不应计入"已上电一次"。
                    if (setPower && powerOn &&
                        _failPowerOn.TryGetValue(topic, out int remaining) && remaining > 0)
                    {
                        if (remaining == 1) _failPowerOn.TryRemove(topic, out _);
                        else _failPowerOn[topic] = remaining - 1;
                        throw new InvalidOperationException("simulated GCU confirm failure: " + topic);
                    }

                    lock (_gate) Written.Add((topic, copy));
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

        /// <summary>让指定外置通道的下 N 次上电下发确认失败。</summary>
        public void FailNextPowerOn(string topic, int times = 1) => _failPowerOn[topic] = times;

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
    /// 标记当前连接代次已成功恢复过一次——复现生产序列：启动恢复先完成，
    /// 空闲恢复与它同代次。旧代码的 force 只在首轮生效，第二轮会被协调器
    /// 「本代次已完成」挡住，于是外置确认失败后不再补刷。
    /// 幂等：静态协调器可能已被同进程里的其他用例标记过。
    /// </summary>
    static void MarkCurrentGenerationCompleted()
    {
        var coordinator = (LightingRestoreCoordinator)typeof(Program)
            .GetField("_lightingRestoreCoordinator", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        coordinator.TryBegin(0, force: false);
        coordinator.Complete(0, success: true);
    }

    /// <summary>
    /// 空闲恢复：外置两通道第一次确认失败、重试成功。灯带与 Logo 必须各上电恰好一次，
    /// 键盘也恢复；幂等令牌在确认成功后写入。
    /// </summary>
    [Fact]
    public async Task IdleRestore_ExternalConfirmFailsOnce_RetriesAndPowersEachChannelExactlyOnce()
    {
        using var harness = new Harness();
        MarkCurrentGenerationCompleted();

        // 空闲到期：三通道一起熄。
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        int requestId = Program.LightingRestoreRequestId;
        harness.FailNextPowerOn(LightbarTopic);
        harness.FailNextPowerOn(LogoTopic);

        // 用户输入唤醒：外置第一次确认失败 → 有界重试 → 成功。
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        var written = harness.Snapshot();
        Assert.Equal(1, PowerCount(written, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(written, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(written, LightbarTopic));
        Assert.Equal(1, EffectAllCount(written, LogoTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LightbarTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LogoTopic));

        // 键盘（本地 HID）同一次恢复里也回来。
        Assert.True(harness.Keyboard.IsConnected, "恢复后键盘 HID 应已连接。");
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
    }

    /// <summary>
    /// 幂等令牌按通道记账：确认失败的通道不记账（允许重试补刷），确认成功的通道立即记账
    /// （后续恢复不得再上电重放，避免闪烁）。
    /// </summary>
    [Fact]
    public async Task ExternalRestore_TokenIsRecordedPerChannelOnlyOnConfirmedSuccess()
    {
        using var harness = new Harness();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        int requestId = Program.LightingRestoreRequestId;
        // 模拟"用户已返回"：恢复分支会先清掉临时熄灯标志，再走一次恢复尝试。
        // 直接置位以便逐次观察失败/重试，而不是让有界重试在本调用内自愈。
        Program.LightingIdleSuspended = false;

        // 第一次恢复：灯带确认失败，Logo 成功。
        harness.FailNextPowerOn(LightbarTopic);
        bool first = await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);

        Assert.False(first, "外置通道未全部确认时，恢复不得报告成功。");
        Assert.NotEqual(requestId, Program.ExternalRestoreAppliedRequestId(LightbarTopic));
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LogoTopic));

        var afterFailed = harness.Snapshot();
        Assert.Equal(0, PowerCount(afterFailed, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(afterFailed, LogoTopic, 1));

        // 第二次恢复（重试）：只补刷未确认的灯带；Logo 已记账不得重复上电。
        bool second = await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);

        Assert.True(second, "补刷成功后恢复应报告成功。");
        Assert.Equal(requestId, Program.ExternalRestoreAppliedRequestId(LightbarTopic));

        var afterRetry = harness.Snapshot();
        Assert.Equal(1, PowerCount(afterRetry, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(afterRetry, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(afterRetry, LightbarTopic));
        Assert.Equal(1, EffectAllCount(afterRetry, LogoTopic));
    }

    /// <summary>
    /// 全部确认成功时不得重复上电：同一次恢复的第二次触发必须被逐通道令牌吞掉。
    /// </summary>
    [Fact]
    public async Task ExternalRestore_SuccessfulCycleIsAppliedExactlyOnce()
    {
        using var harness = new Harness();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);

        var once = harness.Snapshot();
        Assert.Equal(1, PowerCount(once, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(once, LogoTopic, 1));

        // 同一周期的后续触发（唤醒恢复 / 连接恢复 / 电源握手）不得重放。
        await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);
        _ = KeyboardTopic;   // 键盘通道不参与外置计数，仅为可读性保留常量

        var twice = harness.Snapshot();
        Assert.Equal(1, PowerCount(twice, LightbarTopic, 1));
        Assert.Equal(1, PowerCount(twice, LogoTopic, 1));
        Assert.Equal(1, EffectAllCount(twice, LightbarTopic));
        Assert.Equal(1, EffectAllCount(twice, LogoTopic));
    }
}
