using System.Drawing;
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

    static void DetachUiContext() => SynchronizationContext.SetSynchronizationContext(null);

    static bool IsChineseDisplayName(object? value) =>
        RgbForm.HidEffects.Any(hid => hid.Name == value?.ToString());

    /// <summary>假 GCU（记录下发 + 回显键盘电源状态）+ 判定为「不支持」的键盘 + 临时配置目录。</summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool _previousAudit;

        public Harness(bool kbPowerOn = true)
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

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = kbPowerOn };
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
    /// 回退 + 改效果下拉：回退通道不承载任意 HID 效果，线上只下发亮度载体
    /// （light = UI 亮度 75 → 档 3；effect = GCU 回报名），旧代码的「静态」中文名不再上线。
    /// </summary>
    [Fact]
    public async Task Unsupported_EffectSelection_PublishesTheBrightnessCarrierInsteadOfTheChineseDisplayName()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.ReportKeyboardEffect("Rainbow");
        harness.Keyboard.Brightness = 75;   // → 档 3（75）

        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out _, out var effectCombo);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        effectCombo.SelectedIndex = 1;   // 静态：旧回退会把「静态」这个中文名直接发上线
        await WaitUntil(() => harness.CountEffectAll(KeyboardTopic) > 0);
        await SettleAsync();

        Dictionary<string, object> payload = SingleEffectAll(harness);
        Assert.Equal("3", payload["light"]);
        Assert.Equal("Rainbow", payload["effect"]);
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
}
