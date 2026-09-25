using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 防双发（设计 §3/§7.3）：每个键盘电源动作**恰好**一条路径——HID 启动或 GCU 发布。
/// 接缝是 (缓存判定, hidConnected, serviceConnected) 的纯策略 + 调用方独占提前返回：
/// 确定性「不支持」时绝不触碰 HID（连接/旁路/StartMode），Supported/Unknown 保持今天的阶梯。
/// 假 HID 在「判定不支持之后」才变为可枚举——若实现仍走 HID 分支，HID 侧会立刻出现可观测调用，
/// 用例随即失败（这就是「两条路径同时发布」的失败面）。
/// 注：驱动键盘开关前先清掉测试线程上的同步上下文（见 <see cref="DetachUiContext"/>）：
/// 测试线程装着 WinForms 上下文但没有消息泵，即发即弃的 async void 处理器一旦捕获它，
/// 处理器续跑与测试续跑都会永远排不进队列（实测死等）。
/// </summary>
public class KeyboardFallbackNoDoubleApplyTests
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

    static Task SettleAsync() => Task.Delay(150);

    /// <summary>控件事件里裸 await 的处理器需要一个可达的上下文；测试线程的 WinForms 上下文没有消息泵（永不派发）。</summary>
    static void DetachUiContext() => SynchronizationContext.SetSynchronizationContext(null);

    /// <summary>确定性 HID：记录每次调用（打开/feature/write），供断言「HID 分支完全没进」。</summary>
    sealed class SpyKeyboardHid : HidDeviceWin
    {
        readonly Action<string> _record;
        public SpyKeyboardHid(Action<string> record) => _record = record;
        public override bool Open() { _record("HID:Open"); return true; }
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

    /// <summary>假 GCU（记录下发 + 回显状态）+ 可切换的 HID 枚举 seam + 临时配置目录。</summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly SpyKeyboardHid Device;
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
        int _resolves;

        public Harness(bool kbPowerOn = true)
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

            Device = new SpyKeyboardHid(op => { lock (_gate) Events.Add(op); });
            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = kbPowerOn };
            Keyboard.ResolveDeviceProbe = () => { Interlocked.Increment(ref _resolves); return null; };

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;
            _previousAudit = Program.UiAuditMode;

            Program.currentSource = Program.PowerSource.Barrel;
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        /// <summary>判定取到「不支持」之后把设备放开：HID 分支若仍被执行就会立刻产生可观测调用。</summary>
        public void MakeDeviceReachable() => Keyboard.ResolveDeviceProbe =
            () => { Interlocked.Increment(ref _resolves); return Device; };

        public int ResolveCount => Volatile.Read(ref _resolves);

        public void SuppressKeyboardPowerOnEcho() => _suppressKeyboardPowerOnEcho = true;

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
    /// 判定「不支持」：键盘行 ON 只走 GCU（电源恰好一次），HID 分支一步不进——即使设备此刻可枚举、
    /// 应用内开关处于关态（今天最会去连 HID 的那条路）。判定缓存后不得重探。
    /// </summary>
    [Fact]
    public async Task Unsupported_PowerSwitchOn_AppliesThroughGcuOnlyAndNeverTouchesHid()
    {
        using var harness = new Harness(kbPowerOn: false);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());   // 确定性判定：不支持
        Assert.Equal(FeatureAvailability.Unsupported, harness.Keyboard.ControllerAvailability);
        int resolvesAfterProbe = harness.ResolveCount;

        harness.MakeDeviceReachable();   // 假想设备后来可枚举：缓存判定不得自动翻转（§6）
        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out var powerSwitch);
        Program.UiAuditMode = false;     // 处理器入口要真的跑起来
        harness.Clear();
        DetachUiContext();

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.CountGcu(KeyboardTopic, 1) > 0);
        await SettleAsync();

        Assert.True(harness.Keyboard.KbPowerOn, "ON 必须落地应用内意图（供恢复周期走官方通道）。");
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));      // GCU 路径恰好一次
        Assert.Equal(0, harness.CountHidOps());                   // HID 分支一步不进（防双发）
        Assert.Equal(resolvesAfterProbe, harness.ResolveCount);   // 不再枚举/连接
        Assert.Equal(1, harness.Keyboard.ControllerProbeCount);   // 判定已缓存：不重探
    }

    /// <summary>
    /// Supported：键盘行 ON 走 HID 分支（出帧 + 并发固件电源旁路），绝不落到 GCU 回退的效果下发。
    /// 判定未知时由接缝惰性探测（后台线程）取得 Supported。
    /// </summary>
    [Fact]
    public async Task Supported_PowerSwitchOn_AppliesThroughHidOnly()
    {
        using var harness = new Harness(kbPowerOn: false);
        harness.MakeDeviceReachable();
        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out var powerSwitch);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode);
        await SettleAsync();

        Assert.Equal(FeatureAvailability.Supported, harness.Keyboard.ControllerAvailability);
        Assert.True(harness.CountHidOps() > 0, "Supported 必须走 HID 分支出帧。");
        Assert.Equal(0, harness.CountEffectAll(KeyboardTopic));   // 绝不落到 GCU 回退的效果下发
    }

    /// <summary>
    /// Unknown + 审计模式：判定保持 Unknown，按现状走 HID 阶梯（无设备 → GCU 兜底），
    /// 且**绝不**从 UI 启动探测（审计模式不碰真实 HID 设备）。
    /// </summary>
    [Fact]
    public async Task Unknown_AuditMode_KeepsTodaysLadderAndNeverProbes()
    {
        using var harness = new Harness(kbPowerOn: false);
        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out var powerSwitch);
        harness.Clear();
        DetachUiContext();

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.CountGcu(KeyboardTopic, 1) > 0);
        await SettleAsync();

        Assert.Equal(0, harness.Keyboard.ControllerProbeCount);   // 审计模式绝不探测
        Assert.Equal(0, harness.CountHidOps());
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));
        Assert.Equal(1, harness.CountEffectAll(KeyboardTopic));   // 今天的行为：固件通道补发效果
    }

    /// <summary>
    /// 判定「不支持」的空闲恢复：官方通道的电源命令照旧（0→1 各一次），
    /// HID 交接分支一步不进（不连接、不启动效果），且唤醒绝不重探。
    /// </summary>
    [Fact]
    public async Task Unsupported_IdleRestore_SkipsTheHidBranchAndNeverReprobes()
    {
        using var harness = new Harness();
        harness.SuppressKeyboardPowerOnEcho();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        Assert.Equal(FeatureAvailability.Unsupported, harness.Keyboard.ControllerAvailability);
        int probesAfterProbe = harness.Keyboard.ControllerProbeCount;
        int resolvesAfterProbe = harness.ResolveCount;

        harness.MakeDeviceReachable();   // 同上：不可用的判定不得因设备重新出现而翻转
        harness.Clear();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 0));      // 熄灯仍走官方通道

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));      // 上电仍走官方通道
        Assert.Equal(0, harness.CountHidOps());                   // HID 分支一步不进
        Assert.Equal(resolvesAfterProbe, harness.ResolveCount);
        Assert.Equal(probesAfterProbe, harness.Keyboard.ControllerProbeCount);   // 恢复不重探
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
    }

    /// <summary>
    /// 判定「不支持」且用户关灯：OFF 路径（临时熄灯 + 用户关态恢复）只有 GCU 断电命令，
    /// 无任何 HID 调用（设计 §3 对 Program.cs:996-1002 / :1148-1155 的断言）。
    /// </summary>
    [Fact]
    public async Task Unsupported_PowerOffPaths_AreGcuOnly()
    {
        using var harness = new Harness(kbPowerOn: false);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.MakeDeviceReachable();
        harness.Clear();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");

        Assert.Equal(0, harness.CountHidOps());
        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 0));     // 用户关态：只下发一次断电
        Assert.Equal(0, harness.CountGcu(KeyboardTopic, 1));
    }
}
