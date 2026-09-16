using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 空闲休眠恢复后键盘效果被固件「静态」效果顶掉的回归（beta16 真机）。
///
/// 现象：灯效休眠结束、检测到输入后，键盘先出现我们的软件效果，紧接着被厂商的官方（静态）
/// 效果替换——我们的自定义帧模式没有存活。
///
/// 机制：恢复时我们先下发 <c>Keyboard/Ctrl SetPower 1</c> 再（约 120ms 后）进入自定义帧模式；
/// 固件的上电初始化约 1s 后才完成，并在完成时重新套用 NVRAM 官方效果，于是控制器退出
/// ITE 自定义帧模式、我们的帧被静默忽略。该帧在 <c>Keyboard/Status</c> 里 effect 与亮度
/// 相对基线**都没变**（只有 power 由 Off→On），既有 <c>ShouldReassertKeyboardCustomMode</c>
/// 只认 effect/亮度变化，因此完全看不到这次接管，不会重申。
///
/// 本文件锁定空闲恢复后武装的「效果保护窗口」：
/// 1) 恢复在重新上电时武装窗口；窗口内固件的上电回帧（power On、版本前进）触发**恰好一次**
///    重申（ReInit），最终仍是我们的模式，且命中即解除（不形成重试风暴）；
/// 2) 进入自定义帧模式之前，恢复周期相关的 GCU 写（键盘上电、设备侧计时关闭）必须已经下发——
///    效果启动不得排在会触发固件重新套用效果的 GCU 写之前；
/// 3) 保护判据本身是纯函数，直接锁定 armed/过期/版本/电源四个条件；
/// 4) 上电回帧的亮度**不得**镜像进渲染器：该帧带的是 GCU 侧键盘亮度寄存器（自定义帧模式下恒为 0），
///    不是用户按 Fn 设的亮度，写进去会被持久化成 0，键盘亮几秒后永久熄灭（真机 15:45 复现）。
/// </summary>
public class KeyboardEffectProtectTests
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

    static string Describe(string topic, Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("powerstatus", out object? power)) return $"{topic} powerstatus={power}";
        if (payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")) return $"{topic} SetEffectALL";
        if (payload.TryGetValue("Action", out object? action)) return $"{topic} Action={action}";
        return $"{topic} publish";
    }

    /// <summary>确定性 HID：不触真实设备，逐次记录调用顺序，供断言「GCU 写先于 HID 效果启动」。</summary>
    sealed class FakeKeyboardHid : HidDeviceWin
    {
        readonly Action<string> _record;
        public FakeKeyboardHid(Action<string> record) => _record = record;
        public override bool SetFeature(byte[] report) { _record("HID:SetFeature"); return true; }
        public override bool Write(byte[] report) { _record("HID:Write"); return true; }
        public override void Dispose() { }
    }

    /// <summary>
    /// 假 GCU（记录下发 + 回显电源）+ 假键盘 HID（记录调用顺序）+ 临时灯光配置目录。
    /// 与 KeyboardPowerRestoreTests 的夹具同构，额外提供共享事件序列与状态回帧订阅。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly List<string> Events = new();

        readonly object _gate = new();
        volatile bool _suppressKeyboardPowerOnEcho;
        volatile int _gcuPublishDelayMs;
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
            hardware = new MechrevoHw(async (topic, payload) =>
            {
                // 模拟真实 MQTT 发布的异步性：发布完成晚于调用返回。
                if (_gcuPublishDelayMs > 0) await Task.Delay(_gcuPublishDelayMs);
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
                        return;

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
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, Keyboard = true });
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"), new FakeKeyboardHid(RecordHid))
            {
                KbPowerOn = true
            };

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
            Hardware.StateChanged += Program.OnHardwareStateChanged;
        }

        void RecordHid(string op)
        {
            lock (_gate) Events.Add(op);
        }

        /// <summary>让键盘后续的上电命令只下发、不回读（模拟固件上电在恢复之后才回帧）。</summary>
        public void SuppressKeyboardPowerOnEcho() => _suppressKeyboardPowerOnEcho = true;

        /// <summary>让 GCU 发布异步完成（真实 MQTT 语义），用于暴露「未 await 的 GCU 写会排在效果启动之后」。</summary>
        public void DelayGcuPublish(int milliseconds) => _gcuPublishDelayMs = milliseconds;

        public List<string> SnapshotEvents()
        {
            lock (_gate) return new List<string>(Events);
        }

        public void ClearEvents()
        {
            lock (_gate) Events.Clear();
        }

        public void Dispose()
        {
            Hardware.StateChanged -= Program.OnHardwareStateChanged;
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
    /// 空闲恢复后固件的上电回帧（effect/亮度都不变，仅 power Off→On）必须触发**恰好一次**
    /// 重申，最终停留在我们的模式；命中后保护窗口解除，后续同值回帧不得再重申。
    /// </summary>
    [Fact]
    public async Task IdleRestore_FirmwarePowerOnEcho_ReassertsOurModeExactlyOnce()
    {
        using var harness = new Harness();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        // 恢复：上电回帧被抑制（模拟固件上电初始化在约 1s 后才完成）。
        harness.SuppressKeyboardPowerOnEcho();
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);   // 恢复先跑起我们的效果
        Assert.True(Program.KeyboardEffectProtectArmed, "空闲恢复重新上电后必须武装效果保护窗口。");

        int reinitBefore = harness.Keyboard.CustomModeReinitCount;

        // 固件完成上电初始化、重新套用官方（静态）效果：Keyboard/Status 只前进一帧，effect/亮度不变，power=On。
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");

        await WaitUntil(() => harness.Keyboard.CustomModeReinitCount > reinitBefore);

        Assert.Equal(reinitBefore + 1, harness.Keyboard.CustomModeReinitCount);  // 恰好一次重申
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);   // 最终仍是我们的模式
        Assert.False(Program.KeyboardEffectProtectArmed, "命中一次后保护窗口必须解除（不形成重试风暴）。");

        // 后续同值回帧不得再重申。
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");
        await Task.Delay(150);
        Assert.Equal(reinitBefore + 1, harness.Keyboard.CustomModeReinitCount);
    }

    /// <summary>
    /// 排序规则：恢复周期里触发固件重新套用效果的 GCU 写（键盘上电、设备侧计时关闭）
    /// 必须在进入自定义帧模式（第一个 HID 写）之前下发——效果启动不得排在这些 GCU 写之前。
    /// </summary>
    [Fact]
    public async Task IdleRestore_GcuWritesAreOrderedBeforeTheHidEffectStart()
    {
        using var harness = new Harness();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        harness.SuppressKeyboardPowerOnEcho();
        harness.DelayGcuPublish(40);   // GCU 发布异步完成：未 await 的写会落在效果启动之后
        harness.ClearEvents();   // 只看恢复周期的下发顺序

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        List<string> events = harness.SnapshotEvents();
        int powerOn = events.FindLastIndex(e => e == $"GCU:{KeyboardTopic} powerstatus=1");
        int closeTimerOff = events.FindLastIndex(e => e.Contains("Setting/Control") && e.Contains("KEYBOARD_LIGHTBAR_TIMER_OFF"));
        int firstHid = events.FindIndex(e => e.StartsWith("HID:", StringComparison.Ordinal));

        Assert.True(powerOn >= 0, "恢复必须下发键盘上电。事件：" + string.Join(" | ", events));
        Assert.True(closeTimerOff >= 0, "恢复必须下发设备侧计时关闭。事件：" + string.Join(" | ", events));
        Assert.True(firstHid >= 0, "恢复必须启动本地 HID 效果。事件：" + string.Join(" | ", events));
        Assert.True(powerOn < firstHid, "键盘上电必须先于 HID 效果启动。事件：" + string.Join(" | ", events));
        Assert.True(closeTimerOff < firstHid, "设备侧计时关闭必须先于 HID 效果启动。事件：" + string.Join(" | ", events));
    }

    /// <summary>
    /// 真机回归（beta17 现场）：空闲恢复后固件的上电回帧把键盘渲染亮度写成 0 并持久化，
    /// 键盘亮几秒后永久熄灭（灯条/Logo 不受影响）。
    ///
    /// 机制：该帧带 GCU 的键盘亮度寄存器（自定义帧模式下恒为 0，真机日志
    /// <c>KB: effect=5 light=0 brightness=0%</c>），<c>OnHardwareStateChanged</c> 把它当固件接管
    /// 同步进 <c>rgb.Brightness</c> 并 <c>QueueSaveConfig</c>；保护窗口命中的帧只证明固件完成了
    /// 上电初始化，它的亮度不是用户按 Fn 设的值——因此只重申自定义帧模式，绝不镜像亮度。
    /// </summary>
    [Fact]
    public async Task IdleRestore_FirmwarePowerOnEcho_KeepsTheSavedRenderBrightness()
    {
        using var harness = new Harness();
        harness.Keyboard.Brightness = 75;   // 用户保存的渲染亮度；被写成 0 会让键盘全黑

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        // 熄灯周期里 GCU 的键盘回帧就是真机形状：电源 Off、亮度寄存器 0、effect=5。
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"Off\",\"effect\":\"5\",\"light\":\"0\",\"brightNess\":\"0\"}");

        harness.SuppressKeyboardPowerOnEcho();
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");
        Assert.True(Program.KeyboardEffectProtectArmed, "空闲恢复重新上电后必须武装效果保护窗口。");

        int reinitBefore = harness.Keyboard.CustomModeReinitCount;

        // 固件上电初始化完成：只有 power Off→On，effect/亮度与基线相同（保护窗口判据）。
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"effect\":\"5\",\"light\":\"0\",\"brightNess\":\"0\"}");
        await WaitUntil(() => harness.Keyboard.CustomModeReinitCount > reinitBefore);

        Assert.Equal(reinitBefore + 1, harness.Keyboard.CustomModeReinitCount);   // 窗口仍恰好重申一次
        Assert.Equal(75, harness.Keyboard.Brightness);   // 保存的渲染亮度必须原样保留
    }

    /// <summary>
    /// 亮度镜像的另一半契约：真正的固件接管帧（Fn 改亮度，相对基线前进）仍必须落到渲染器，
    /// 否则灯停在旧档（固件新亮度必须保留）。
    /// </summary>
    [Fact]
    public async Task FirmwareBrightnessChange_StillReachesTheRenderer()
    {
        using var harness = new Harness();
        harness.Keyboard.Brightness = 75;
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"effect\":\"5\",\"light\":\"3\",\"brightNess\":\"75\"}");
        Program.MarkKeyboardCustomStatusBaseline();   // 基线 = 当前上报（75%）

        // Fn 降一档：亮度 75%→50%，effect 不变 → 真实固件接管
        int reinitBefore = harness.Keyboard.CustomModeReinitCount;
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"effect\":\"5\",\"light\":\"2\",\"brightNess\":\"50\"}");
        await WaitUntil(() => harness.Keyboard.Brightness == 50);

        Assert.Equal(50, harness.Keyboard.Brightness);
        await WaitUntil(() => harness.Keyboard.CustomModeReinitCount > reinitBefore);   // 等异步重申落地
        await Task.Delay(150);   // 异步恢复周期收尾，避免 Dispose 与 StartMode 竞争
    }

    [Theory]
    [InlineData(true, 10, 9, true, false, true)]    // 窗口内固件上电回帧 → 重申
    [InlineData(true, 9, 9, true, false, false)]    // 版本未前进 → 不是新帧
    [InlineData(true, 10, 9, false, false, false)]  // 电源仍为关 → 不是固件上电回帧
    [InlineData(true, 10, 9, true, true, false)]    // 窗口已过期 → 不重申
    [InlineData(false, 10, 9, true, false, false)]  // 未武装 → 不重申
    public void ProtectPredicate_LocksArmedVersionPowerAndDeadline(
        bool armed, long version, long baselineVersion, bool reportedPower, bool expired, bool expected)
    {
        Assert.Equal(expected, Program.ShouldReassertProtectedKeyboardCustomMode(
            armed, version, baselineVersion, reportedPower, expired));
    }
}
