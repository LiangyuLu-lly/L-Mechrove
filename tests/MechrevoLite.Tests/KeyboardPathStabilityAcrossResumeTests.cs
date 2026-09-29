using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T6 唤醒路径稳定性（设计 §6「唤醒/重连时绝不重探」/ §7 第 6 项）：模拟睡眠→唤醒，
/// 把恢复协调器（真实唤醒入口 ScheduleKeyboardLightingRestoreAfterResume 调用的 RestoreLightingWithRetryAsync）
/// 调用两次，钉住三条保证：
///   1) 判定（ControllerAvailability）在唤醒前后不变（会话内单调）；
///   2) 探测计数与 HID 枚举计数都不增长（唤醒路径不得重探/重新枚举）；
///   3) 键盘线上发布序列与第 0 步表征基线（KeyboardFallbackCharacterizationTests）逐条一致。
/// 这是表征（characterization）测试：断言只钉「改动前后行为一致 + 接缝只读缓存」。
/// 一旦变红，说明唤醒路径出现了真实缺陷（重探 / 双发 / 命令漂移），不得改断言迁就实现。
/// </summary>
public class KeyboardPathStabilityAcrossResumeTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";
    const string SettingTopic = "Setting/Control";
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";

    /// <summary>
    /// 第 0 步表征基线：一次唤醒恢复周期的键盘命令 = 电源下发 → 设备侧计时关闭（随后才轮到 HID 效果启动）。
    /// 接缝不得增加、删除或重排任何一项。
    /// </summary>
    static readonly string[] ResumeKeyboardBaseline =
    {
        KeyboardTopic + " SetPower powerstatus=1",
        SettingTopic + " KEYBOARD_LIGHTBAR_TIMER_OFF",
    };

    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>模拟睡眠（应用内空闲到期 → 三通道临时熄灯，同时推进恢复令牌）→ 唤醒；清空事件流，后续只反映唤醒恢复。</summary>
    static async Task SleepThenWakeAsync(Harness harness)
    {
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "前置：空闲到期应进入临时熄灯（模拟睡眠）。");
        Program.LightingIdleSuspended = false;   // 用户输入/唤醒：解除临时熄灯
        harness.Clear();
    }

    static string Describe(string topic, Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("function", out object? function) && Equals(function, "SetPower"))
            return $"{topic} SetPower powerstatus={payload["powerstatus"]}";
        if (payload.TryGetValue("Action", out object? action)) return $"{topic} {action}";
        return topic + " publish";
    }

    static bool IsKeyboardRestoreEntry(string entry) =>
        entry.StartsWith("GCU:" + KeyboardTopic + " SetPower", StringComparison.Ordinal) ||
        entry == "GCU:" + SettingTopic + " KEYBOARD_LIGHTBAR_TIMER_OFF";

    /// <summary>唤醒恢复的键盘命令序列（按线上发生顺序）；遥测 GETSTATUS 不算恢复命令。</summary>
    static List<string> KeyboardRestoreSequence(Harness harness) =>
        harness.SnapshotEvents()
            .Where(IsKeyboardRestoreEntry)
            .Select(entry => entry.Substring("GCU:".Length))
            .ToList();

    static void AssertBaselineSequence(List<string> actual, string phase)
    {
        Assert.True(actual.SequenceEqual(ResumeKeyboardBaseline),
            $"{phase}：唤醒后的发布序列与改动前（第 0 步表征基线）不一致。" +
            $"期望 [ {string.Join(" | ", ResumeKeyboardBaseline)} ]，实际 [ {string.Join(" | ", actual)} ]。");
    }

    /// <summary>确定性 HID：记录每次调用；Supported 探测/恢复要用到它，Unsupported 用例中它永远不可达。</summary>
    sealed class SpyKeyboardHid : HidDeviceWin
    {
        readonly Action<string> _record;
        public SpyKeyboardHid(Action<string> record) => _record = record;
        public override bool Open() { _record("HID:Open"); return true; }
        public override bool SetFeature(byte[] report) { _record("HID:SetFeature"); return true; }
        public override bool Write(byte[] report) { _record("HID:Write"); return true; }
        public override void Dispose() { }
    }

    /// <summary>假 GCU（有序记录下发 + 可抑制键盘上电回读）+ 可切换的 HID 枚举 seam + 临时灯光配置目录。</summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly SpyKeyboardHid Device;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly List<string> Events = new();

        readonly object _gate = new();
        readonly bool _deviceAvailable;
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        volatile bool _suppressKeyboardPowerOnEcho;
        int _resolves;

        public Harness(bool deviceAvailable)
        {
            _deviceAvailable = deviceAvailable;
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
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, MbaLogo = true, Keyboard = true });
            hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            Hardware = hardware;

            Device = new SpyKeyboardHid(op => { lock (_gate) Events.Add(op); });
            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = true };
            // 判定来源与真实枚举同构：不可用机型 = 无候选（确定性 Unsupported）；可用机型 = 有候选设备。
            Keyboard.ResolveDeviceProbe = () => { Interlocked.Increment(ref _resolves); return _deviceAvailable ? Device : null; };

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

        /// <summary>真机键盘主题长期 not confirmed：上电命令只下发、不回读。</summary>
        public void SuppressKeyboardPowerOnEcho() => _suppressKeyboardPowerOnEcho = true;

        /// <summary>让固件回读「键盘电源关」（唤醒前沿的保守建模）。</summary>
        public void SetFirmwareKeyboardPower(bool on) =>
            Hardware.HandleMessage("Keyboard/Status",
                "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"" + (on ? "On" : "Off") + "\"}");

        public int ResolveCount => Volatile.Read(ref _resolves);

        public List<string> SnapshotEvents()
        {
            lock (_gate) return new List<string>(Events);
        }

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
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousLightDir);
            Keyboard.Dispose();
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    /// <summary>
    /// Unsupported：显式用户探测取得判定后，睡眠→唤醒的两次协调器调用——
    /// 判定不变、不重探（不枚举/不打开 HID）、HID 分支一步不进，
    /// 电源 → 计时关闭仍等于第 0 步基线，并且每次恢复重放一次 SetKeyboardEffect。
    /// </summary>
    [Fact]
    public async Task Unsupported_ResumeTwice_KeepsTheVerdictAndTheBaselineGcuSequence()
    {
        using var harness = new Harness(deviceAvailable: false);
        harness.SuppressKeyboardPowerOnEcho();
        harness.SetFirmwareKeyboardPower(false);

        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());   // 判定只由显式用户动作取得（设计 §6）
        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Unsupported, "前置：判定应为 Unsupported。");
        int probesAfterUserProbe = harness.Keyboard.ControllerProbeCount;
        int resolvesAfterUserProbe = harness.ResolveCount;

        await SleepThenWakeAsync(harness);

        await Program.RestoreLightingWithRetryAsync(force: true);   // 唤醒 #1（真实唤醒入口的方法体）
        List<string> firstResume = KeyboardRestoreSequence(harness);
        harness.Clear();

        await Program.RestoreLightingWithRetryAsync(force: true);   // 唤醒 #2
        List<string> secondResume = KeyboardRestoreSequence(harness);

        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Unsupported,
            "判定在唤醒后发生了变化（设计 §6：缓存判定在会话内单调）。");
        Assert.True(harness.Keyboard.ControllerProbeCount == probesAfterUserProbe,
            "probe 在唤醒后被重复调用（设计 §6：唤醒/重连绝不重探）。");
        Assert.True(harness.ResolveCount == resolvesAfterUserProbe,
            "唤醒路径重新枚举了 HID 设备（判定缓存后不得再枚举/打开）。");
        Assert.True(harness.CountHidOps() == 0,
            "判定 Unsupported 的机型在唤醒后进入了 HID 分支（HID 分支必须天然惰性）。");
        Assert.True(harness.CountEffectAll(KeyboardTopic) == 1,
            "唤醒回退必须重放一次 SetKeyboardEffect；只下发电源灯不会亮。");
        AssertBaselineSequence(firstResume, "第一次唤醒");
        AssertBaselineSequence(secondResume, "第二次唤醒");
    }

    /// <summary>
    /// Supported：判定不变、不重探；第一次唤醒的键盘序列 = 第 0 步基线（电源 → 计时关闭 → HID 效果重启），
    /// 同一熄灯周期的第二次协调器调用与基线一致地静默（不重复写电源、不重启效果、不发 SetEffectALL）。
    /// </summary>
    [Fact]
    public async Task Supported_ResumeTwice_KeepsTheVerdictAndTheBaselineHidSequence()
    {
        using var harness = new Harness(deviceAvailable: true);
        harness.SuppressKeyboardPowerOnEcho();

        Assert.True(await harness.Keyboard.EnsureHidReadyAsync());   // 显式探测 → Supported，保持流打开
        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Supported, "前置：判定应为 Supported。");
        int probesAfterUserProbe = harness.Keyboard.ControllerProbeCount;
        int resolvesAfterUserProbe = harness.ResolveCount;
        int generationBefore = harness.Keyboard.EffectGeneration;

        await SleepThenWakeAsync(harness);

        await Program.RestoreLightingWithRetryAsync(force: true);   // 唤醒 #1
        List<string> firstResume = KeyboardRestoreSequence(harness);
        int hidAfterFirstResume = harness.CountHidOps();
        int generationAfterFirstResume = harness.Keyboard.EffectGeneration;

        AssertBaselineSequence(firstResume, "第一次唤醒");
        Assert.True(hidAfterFirstResume > 0, "Supported 机型的唤醒恢复必须走 HID 分支（与改动前基线一致）。");
        Assert.True(generationAfterFirstResume == generationBefore + 1, "唤醒恢复必须恰好重启一次 HID 效果（与改动前基线一致）。");
        Assert.True(harness.Keyboard.IsConnected, "唤醒恢复后 HID 流应保持连接。");
        Assert.True(harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode, "唤醒恢复后效果必须已启动。");
        Assert.True(harness.CountGcu(KeyboardTopic, 1) == 1, "一个唤醒周期键盘电源应恰好下发一次。");
        Assert.True(harness.CountEffectAll(KeyboardTopic) == 0, "Supported 机型不得落到 GCU 亮度载体（SetEffectALL）。");

        // 同一熄灯周期的第二次协调器调用（唤醒/连接/电源握手会相继触发）：第 0 步基线要求静默。
        harness.Clear();
        await Program.RestoreLightingWithRetryAsync(force: true);

        List<string> secondResume = KeyboardRestoreSequence(harness);
        Assert.True(secondResume.Count == 0,
            "同一熄灯周期的第二次唤醒触发重复写电源/计时（第 0 步基线被破坏）：" + string.Join(" | ", secondResume));
        Assert.True(harness.CountHidOps() == 0, "同一熄灯周期的第二次唤醒触发不得重启效果（第 0 步基线）。");
        Assert.True(harness.Keyboard.EffectGeneration == generationAfterFirstResume, "效果代际不得因重复触发增长。");

        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Supported, "判定在唤醒后发生了变化。");
        Assert.True(harness.Keyboard.ControllerProbeCount == probesAfterUserProbe,
            "probe 在唤醒后被重复调用（设计 §6：唤醒/重连绝不重探）。");
        Assert.True(harness.ResolveCount == resolvesAfterUserProbe,
            "唤醒路径重新枚举了 HID 设备（判定缓存后不得再枚举）。");
    }

    /// <summary>
    /// Unknown（未探测）：唤醒绝不触发探测——保持今天的行为（HID 阶梯）而不是先探测再路由。
    /// </summary>
    [Fact]
    public async Task Unknown_ResumeTwice_NeverStartsAProbeAndKeepsTodaysHidPath()
    {
        using var harness = new Harness(deviceAvailable: true);
        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Unknown, "前置：判定应为 Unknown。");

        await Program.RestoreLightingWithRetryAsync(force: true);   // 唤醒 #1
        await Program.RestoreLightingWithRetryAsync(force: true);   // 唤醒 #2

        Assert.True(harness.Keyboard.ControllerProbeCount == 0,
            "唤醒路径启动了探测（设计 §6：唤醒绝不重探；Unknown 保持今天的行为）。");
        Assert.True(harness.Keyboard.ControllerAvailability == FeatureAvailability.Unknown, "判定在唤醒后发生了变化。");
        Assert.True(harness.CountHidOps() > 0, "Unknown 必须保持今天的行为：唤醒恢复走 HID 阶梯。");
        Assert.True(harness.CountEffectAll(KeyboardTopic) == 0, "Unknown 不得路由到 GCU 亮度载体。");
    }
}
