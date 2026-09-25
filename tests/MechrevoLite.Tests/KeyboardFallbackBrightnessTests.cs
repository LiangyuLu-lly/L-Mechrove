using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// T5 亮度载体（设计 §4/§7.4）：回退（确定性「不支持」）通道没有独立的「设亮度」命令——
/// light 只能随 SetEffectALL 打包下发，所以改亮度 = 重发 GCU 当前回报的效果名 + 新的 light 档位。
/// 断言：light ∈ 0–4（由 UI 亮度 0–100 映射为 5 档）、effect == GCU 回报的效果名（保留），
/// 线上绝不出现 hid.Name 之类的中文显示名（修掉既有回退的隐性 bug）。
/// 注：驱动键盘开关前先清掉测试线程上的同步上下文（同 <c>KeyboardFallbackNoDoubleApplyTests</c>：
/// 没有消息泵的 WinForms 上下文会让即发即弃的 async void 处理器永远排不进队列）。
/// </summary>
public class KeyboardFallbackBrightnessTests
{
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

    static Task SettleAsync() => Task.Delay(150);

    /// <summary>同步轮询（不 await）：RgbForm 回退路径会在测试线程上留下带句柄的窗体与
    /// 排队到 xunit 同步上下文的续体，await 轮询在该形态下会停摆；同步轮询绕开上下文。</summary>
    static void WaitUntilSync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++) Thread.Sleep(10);
    }

    static void DetachUiContext() => SynchronizationContext.SetSynchronizationContext(null);

    static bool IsChineseDisplayName(object? value) =>
        RgbForm.HidEffects.Any(hid => hid.Name == value?.ToString());

    /// <summary>确定性假 HID：记录写入，不触真实设备（同 KeyboardFallbackCharacterizationTests 的接缝）。</summary>
    sealed class RecordingKeyboardHid : HidDeviceWin
    {
        readonly Harness _harness;
        public RecordingKeyboardHid(Harness harness) => _harness = harness;
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) { _harness.RecordHid("HID:SetFeature"); return true; }
        public override bool Write(byte[] report) { _harness.RecordHid("HID:Write"); return true; }
        public override void Dispose() { }
    }

    /// <summary>假 GCU（记录下发 + 回显键盘电源状态）+ 判定为「不支持」的键盘 + 临时配置目录。</summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly List<string> HidOps = new();

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool _previousAudit;

        public Harness(bool kbPowerOn = true, bool keyboardConnected = false)
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    lock (_gate) Written.Add((topic, copy));

                    // 电源命令回读：与真机 GCU 一致地把 powerStatus 回显到 Keyboard/Status，
                    // 让 SetLightPower 的确认走 180ms 快路径（真机本机键盘长期 not confirmed）。
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

            Keyboard = keyboardConnected
                ? new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"), new RecordingKeyboardHid(this)) { KbPowerOn = kbPowerOn }
                : new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = kbPowerOn };
            Keyboard.ResolveDeviceProbe = () => null;   // 无候选 = 确定性「不支持」（设计 §1）

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

        /// <summary>让 GCU 回报当前键盘效果名（亮度载体要保留的官方英文名）。</summary>
        public void ReportKeyboardEffect(string effect) =>
            Hardware.HandleMessage("Keyboard/Status",
                $"{{\"type\":\"MEZone_Lighbar4\",\"effect\":\"{effect}\",\"light\":\"4\",\"powerStatus\":\"On\"}}");

        public void RecordHid(string op)
        {
            lock (_gate) HidOps.Add(op);
        }

        public int CountHidOps()
        {
            lock (_gate) return HidOps.Count;
        }

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(Written);
        }

        public void Clear()
        {
            lock (_gate) Written.Clear();
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

    static SettingsForm BuildKeyboardForm(out RCheckBox powerSwitch, out ComboBox effectCombo)
    {
        var form = new SettingsForm();
        form.CreateControl();
        form.PerformLayout();
        var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
        powerSwitch = row.Controls.OfType<RCheckBox>().Single();
        effectCombo = row.Controls.OfType<ComboBox>().Single();
        return form;
    }

    /// <summary>取唯一一条 SetEffectALL 载荷；数量不为 1 直接失败并打印整条下发流水。</summary>
    static Dictionary<string, object> SingleEffectAll(Harness harness)
    {
        List<(string Topic, Dictionary<string, object> Payload)> written = harness.Snapshot();
        var effectAll = written.Where(entry => entry.Topic == KeyboardTopic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")).ToList();
        Assert.True(effectAll.Count == 1, "亮度载体必须恰好下发一次 SetEffectALL。下发：" +
            string.Join(" | ", written.Select(entry => entry.Topic + " " +
                string.Join(",", entry.Payload.Select(pair => pair.Key + "=" + pair.Value)))));
        return effectAll[0].Payload;
    }

    /// <summary>线上任何载荷都不得携带 HID 中文显示名（如「流畅彩虹」）当效果名。</summary>
    static void AssertNoChineseDisplayNameOnTheWire(Harness harness)
    {
        foreach (var entry in harness.Snapshot())
        {
            if (!entry.Payload.TryGetValue("effect", out object? effect)) continue;
            Assert.False(IsChineseDisplayName(effect),
                "回退通道不得下发中文显示名当效果名，实际：" + effect);
        }
    }

    /// <summary>
    /// 回退 + 键盘 ON：电源照旧走官方通道；亮度载体随后下发，light = UI 亮度 60 → 最近档 2，
    /// effect = GCU 回报的英文名（保留），绝不用中文显示名。
    /// </summary>
    [Fact]
    public async Task Unsupported_PowerSwitchOn_CarriesTheUiBrightnessAndKeepsTheReportedEffect()
    {
        using var harness = new Harness(kbPowerOn: false);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());   // 确定性判定：不支持
        harness.ReportKeyboardEffect("Rainbow");
        harness.Keyboard.Brightness = 60;   // UI 亮度（0–100）→ 5 档中的 2（50）

        Program.UiAuditMode = true;         // 表单构造不碰真实设备
        using var form = BuildKeyboardForm(out var powerSwitch, out _);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        powerSwitch.Checked = true;
        await WaitUntil(() => harness.CountEffectAll(KeyboardTopic) > 0);
        await SettleAsync();

        Assert.Equal(1, harness.CountGcu(KeyboardTopic, 1));      // 电源仍是官方通道的既有下发
        Dictionary<string, object> payload = SingleEffectAll(harness);
        Assert.Equal("2", payload["light"]);                      // light ∈ 0–4，且 = UI 亮度映射档
        Assert.Equal(harness.Hardware.KeyboardEffect, payload["effect"]);   // 效果保留
        Assert.Equal("Rainbow", payload["effect"]);
        AssertNoChineseDisplayNameOnTheWire(harness);
    }

    /// <summary>
    /// 回退 + 改效果下拉：GCU 目录第二项是 Breathing，线上发英文 ID，绝不用 HID 中文显示名。
    /// </summary>
    [Fact]
    public async Task Unsupported_EffectSelection_PublishesTheBrightnessCarrierInsteadOfTheChineseDisplayName()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.ReportKeyboardEffect("Rainbow");
        harness.Keyboard.Brightness = 75;   // store 默认 light=4；本用例只锁效果名

        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out _, out var effectCombo);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        effectCombo.SelectedIndex = 1;   // Breathing
        await WaitUntil(() => harness.CountEffectAll(KeyboardTopic) > 0);
        await SettleAsync();

        Dictionary<string, object> payload = SingleEffectAll(harness);
        Assert.Equal("Breathing", payload["effect"]);
        AssertNoChineseDisplayNameOnTheWire(harness);
    }

    /// <summary>GCU 从未回报效果名：亮度载体用唯一规范默认 "Single"（设计 §4）。</summary>
    [Fact]
    public async Task MissingReportedEffect_UsesTheCanonicalSingleDefault()
    {
        using var harness = new Harness(kbPowerOn: false);
        Assert.Equal("", harness.Hardware.KeyboardEffect);   // 未回报

        Assert.True(await Program.service!.SetKeyboardBrightnessPreservingEffect(3));

        Dictionary<string, object> payload = SingleEffectAll(harness);
        Assert.Equal("Single", payload["effect"]);
        Assert.Equal("3", payload["light"]);
    }

    /// <summary>
    /// 呼吸也吃单色 ColorBuffer。亮度载体不得只在 effect==Single 时带色，否则呼吸会掉回 7 色默认盘。
    /// </summary>
    [Fact]
    public async Task BrightnessPreserve_Breathing_SendsColor()
    {
        using var harness = new Harness();
        Color cyan = Color.FromArgb(0, 255, 255);
        LightingSettingsStore.Save(KeyboardTopic,
            new LightChannelSettings("Breathing", 4, 2, cyan.ToArgb(), PowerOn: true));

        Assert.True(await Program.service!.SetKeyboardBrightnessPreservingEffect(3));

        Dictionary<string, object> payload = SingleEffectAll(harness);
        Assert.Equal("Breathing", payload["effect"]);
        Assert.Equal("3", payload["light"]);
        Assert.Equal("2", payload["speed"]);
        var color = Assert.IsType<Dictionary<string, object>>(payload["color"]);
        Assert.Equal(1, color["ColorBlocks"]);
        var buffer = Assert.IsType<object[]>(color["ColorBuffer"]);
        Assert.Single(buffer);
        var rgb = Assert.IsType<Dictionary<string, object>>(buffer[0]);
        Assert.Equal(0, rgb["R"]);
        Assert.Equal(255, rgb["G"]);
        Assert.Equal(255, rgb["B"]);
    }

    /// <summary>UI 亮度 0–100 → GCU 5 档（0–4）：整档与既有正向映射互逆。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 1)]
    [InlineData(50, 2)]
    [InlineData(75, 3)]
    [InlineData(100, 4)]
    public void UiBrightness_MapsToTheFiveHardwareLevels(int brightness, int expected)
    {
        Assert.Equal(expected, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(brightness));
        Assert.Equal(brightness, KeyboardRgb.MapHardwareBrightnessLevel(expected));
    }

    /// <summary>非整档取最近档；越界收口到 0–4。</summary>
    [Theory]
    [InlineData(60, 2)]   // 50 比 75 近
    [InlineData(90, 4)]   // 100 比 75 近
    [InlineData(13, 1)]   // 25 比 0 近
    [InlineData(-5, 0)]
    [InlineData(120, 4)]
    public void UiBrightness_RoundsToTheNearestHardwareLevel(int brightness, int expected) =>
        Assert.Equal(expected, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(brightness));

    // ---------- RgbForm 回退态亮度滑条（beta17 收尾：状态行承诺「仅电源与亮度」） ----------

    static void ApplyModeSelectionViaReflection(RgbForm form)
    {
        MethodInfo? apply = typeof(RgbForm).GetMethod("ApplyModeSelection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);
        apply!.Invoke(form, new object?[] { true });
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    static Control HidPanelOf(RgbForm form) => (Control)typeof(RgbForm).GetField("_hidPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    /// <summary>
    /// 回退态 RgbForm：亮度滑条保持可用（状态行承诺「仅电源与亮度」），拖到 5 个档位各自经
    /// 官方通道下发恰好一条亮度载体（light = 档位、effect = GCU 回报名）；其余 HID 专属控件
    /// 仍禁用，HID 亮度路径不被触碰（无 HID 连接、无效果线程启动）。
    /// </summary>
    [Fact]
    public async Task Unsupported_RgbFormBrightnessSlider_PublishesTheCarrierAtEachOfTheFiveLevels()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());   // 确定性判定：不支持
        harness.ReportKeyboardEffect("Rainbow");
        harness.Keyboard.Brightness = 13;   // 非整档初值：5 个档位都与当前值不同，滑条必触发

        Program.UiAuditMode = true;         // 构造不启动模式跟随计时器
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;        // 滑条发布路径不走审计门（审计模式本就不进回退分支）
        harness.Clear();

        ApplyModeSelectionViaReflection(form);   // 进入回退分支

        Control hidPanel = HidPanelOf(form);
        RSlider slider = Descendants(hidPanel).OfType<RSlider>().First();
        Assert.True(slider.Enabled, "回退态下亮度滑条必须可用，否则用户无法调亮度。");

        int effectGenerationBefore = harness.Keyboard.EffectGeneration;
        foreach (int step in new[] { 0, 25, 50, 75, 100 })
        {
            harness.Clear();
            slider.Value = step;
            WaitUntilSync(() => harness.CountEffectAll(KeyboardTopic) > 0);
            Thread.Sleep(150);   // 去抖窗口过后再断言唯一性（迟到的旧档不得再发）

            Dictionary<string, object> payload = SingleEffectAll(harness);
            Assert.Equal((step / 25).ToString(), payload["light"]);   // 0/25/50/75/100 ⇒ 档 0..4
            Assert.Equal("Rainbow", payload["effect"]);               // 效果保留（GCU 回报名）
            AssertNoChineseDisplayNameOnTheWire(harness);
        }

        Assert.Equal(100, harness.Keyboard.Brightness);   // UI 亮度随滑条持久化（载体输入档）
        Assert.Equal(effectGenerationBefore, harness.Keyboard.EffectGeneration);   // HID 效果线程未被触碰
        Assert.False(harness.Keyboard.IsConnected);       // HID 路径未被触碰：从不连接
        Assert.Equal(0, harness.CountHidOps());           // 无任何 HID 写入
    }

    /// <summary>Supported 机型上滑条保持今天的 HID 行为：只写渲染器亮度，绝不发 GCU 载体。</summary>
    [Fact]
    public async Task Supported_RgbFormBrightnessSlider_KeepsTheHidPathAndNeverPublishesTheCarrier()
    {
        using var harness = new Harness(keyboardConnected: true);   // Supported：HID 已连接（假设备接缝）
        // 固件电源已开：连接态路径不补发 GCU 电源（同 KeyboardFallbackCharacterizationTests 表征 2）。
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");

        Program.UiAuditMode = true;   // 构造不启动模式跟随计时器；全程保持（同表征 2）
        using var form = new RgbForm(harness.Keyboard);
        harness.Clear();

        ApplyModeSelectionViaReflection(form);   // 走 HID 分支（已连接）
        await WaitUntil(() => harness.Keyboard.ActiveMode == harness.Keyboard.KbHidMode);

        Control hidPanel = HidPanelOf(form);
        RSlider slider = Descendants(hidPanel).OfType<RSlider>().First();
        Assert.True(slider.Enabled);

        harness.Clear();
        slider.Value = 25;
        await WaitUntil(() => harness.Keyboard.Brightness == 25);
        await SettleAsync();

        Assert.Equal(0, harness.CountEffectAll(KeyboardTopic));   // 绝不发 GCU 亮度载体
        Assert.Equal(25, harness.Keyboard.Brightness);            // HID 渲染器亮度照旧
    }

    // ---------- RgbForm teardown 幂等（同一实例的 FormClosed 会触发两次） ----------

    /// <summary>
    /// Application.Exit 第二阶段的 while 循环按反序处理 s_forms（最后显示的窗体先关）：
    /// OnFormClosed 会把它移出 Application.OpenForms，随后主窗体（拥有窗体）的
    /// RaiseFormClosedOnAppExit 再看自己的被拥有窗体，此时已不在 OpenForms，于是对同一实例
    /// 再调一次 OnFormClosed。用反射直接走这条 protected 路径，等价于真机上的两次触发。
    /// </summary>
    static void RaiseFormClosed(RgbForm form)
    {
        MethodInfo? onClosed = typeof(Form).GetMethod("OnFormClosed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onClosed);
        onClosed!.Invoke(form, new object[] { new FormClosedEventArgs(CloseReason.ApplicationExitCall) });
    }

    static SemaphoreSlim BrightnessLockOf(RgbForm form) =>
        (SemaphoreSlim)typeof(RgbForm).GetField("_gcuBrightLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    /// <summary>第二次 FormClosed 不得再 Wait/Dispose 已销毁的信号量（21cd310 回归：进程静默死亡）。</summary>
    [Fact]
    public void RgbFormTeardown_IsIdempotent_WhenFormClosedFiresTwice()
    {
        using var harness = new Harness();
        Program.UiAuditMode = true;   // 构造不启动模式跟随计时器
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;

        RaiseFormClosed(form);        // 第一次：反序循环先关被拥有窗体

        Exception? thrown = Record.Exception(() => RaiseFormClosed(form));   // 第二次：拥有窗体循环再关一次
        Exception? cause = thrown?.InnerException ?? thrown;
        Assert.True(thrown is null,
            "FormClosed 会对同一实例触发两次，teardown 必须幂等。实际抛出：" +
            cause?.GetType().Name + " " + cause?.Message);
    }

    /// <summary>修复不得静默跳过原意图：正常单次关闭仍要有界等待进行中的亮度发布，拿到锁后 Release 并 Dispose。</summary>
    [Fact]
    public void RgbFormTeardown_SingleClose_StillDrainsAndDisposesTheBrightnessLock()
    {
        using var harness = new Harness();
        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;

        RaiseFormClosed(form);

        Assert.Throws<ObjectDisposedException>(() => BrightnessLockOf(form).Wait(0));
    }
}
