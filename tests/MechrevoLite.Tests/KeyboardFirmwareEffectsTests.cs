using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// GCU 键盘固件效果表（厂商 RGBKB_Effect 剩余 11 项）与路径分流：
/// GCU 下发 Keyboard/Ctrl SetEffectALL 英文 ID；HID 仍用 BetterRGB 10，中文显示名不上 MQTT。
/// </summary>
public class KeyboardFirmwareEffectsTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";
    const string LightbarTopic = "HidLightbar/Ctrl";

    static readonly string[] VendorIds =
    {
        "Single", "Breathing", "Wave", "Reactive", "Rainbow", "Ripple",
        "Raindrop", "Marquee", "Spark", "Aurora", "Gaming",
    };

    static readonly string[] VendorLabels =
    {
        "单色", "呼吸", "波浪", "按键反应", "彩虹", "涟漪",
        "雨滴", "跑马灯", "火花", "极光", "游戏",
    };

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

    static void WaitUntilSync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++) Thread.Sleep(10);
    }

    static Task SettleAsync() => Task.Delay(150);

    static void DetachUiContext() => SynchronizationContext.SetSynchronizationContext(null);

    static bool IsChineseHidDisplayName(object? value) =>
        RgbForm.HidEffects.Any(hid => hid.Name == value?.ToString());

    sealed class RecordingKeyboardHid : HidDeviceWin
    {
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) => true;
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }
    }

    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly KeyboardRgb Keyboard;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly string Directory;

        readonly object _gate = new();
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;
        readonly bool _previousAudit;

        public Harness(bool keyboardConnected = false, bool kbPowerOn = true, int keyboardType = 0)
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            Directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, Directory);

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    lock (_gate) Written.Add((topic, copy));
                    if (copy.TryGetValue("powerstatus", out object? power) && topic == KeyboardTopic)
                    {
                        string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                        hardware!.HandleMessage("Keyboard/Status",
                            $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                    }
                }
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { Keyboard = true, Lightbar = true, KeyboardType = keyboardType });
            Hardware = hardware;

            Keyboard = keyboardConnected
                ? new KeyboardRgb(Path.Combine(Directory, "rgb.cfg"), new RecordingKeyboardHid()) { KbPowerOn = kbPowerOn }
                : new KeyboardRgb(Path.Combine(Directory, "rgb.cfg")) { KbPowerOn = kbPowerOn };
            if (!keyboardConnected) Keyboard.ResolveDeviceProbe = () => null;

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

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(Written);
        }

        public void Clear()
        {
            lock (_gate) Written.Clear();
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
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    static SettingsForm BuildKeyboardForm(out ComboBox effectCombo)
    {
        var form = new SettingsForm();
        form.CreateControl();
        form.PerformLayout();
        var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
        effectCombo = row.Controls.OfType<ComboBox>().Single();
        return form;
    }

    static string[] ComboLabels(ComboBox combo) =>
        combo.Items.Cast<object>().Select(item => combo.GetItemText(item) ?? "").ToArray();

    [Fact]
    public void VendorTable_ContainsExactlyTheElevenEnglishIdsInVendorOrder()
    {
        Assert.Equal(VendorIds, KeyboardFirmwareEffects.All.Select(e => e.Id).ToArray());
        Assert.Equal(VendorLabels, KeyboardFirmwareEffects.All.Select(e => e.Label).ToArray());
        Assert.Equal(11, KeyboardFirmwareEffects.All.Length);
        Assert.Equal(10, RgbForm.HidEffects.Length);
        Assert.DoesNotContain(KeyboardFirmwareEffects.All, e => IsChineseHidDisplayName(e.Id));
        Assert.DoesNotContain(KeyboardFirmwareEffects.All, e => e.Id is "Impact" or "Flash" or "Mix" or "Thinking"
            or "Devour" or "UserMode" or "BatteryPercent" or "Manual" or "ColorfulWave" or "Dawn"
            or "ColorMarquee" or "Twinkling" or "Sine" or "Interlace" or "Diagonal" or "Music");
    }

    [Fact]
    public async Task GcuPath_EffectCombo_PublishesEnglishSetEffectAllAndPersistsOnKeyboardTopic()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        LightingSettingsStore.Save(LightbarTopic,
            new LightChannelSettings("Impact", 3, 1, Color.White.ToArgb(), PowerOn: true));

        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out ComboBox effectCombo);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        Assert.Equal(VendorLabels, ComboLabels(effectCombo));
        int wave = Array.IndexOf(VendorIds, "Wave");
        Assert.Equal(2, wave);
        effectCombo.SelectedIndex = wave;

        WaitUntilSync(() => harness.CountEffectAll(KeyboardTopic) > 0);
        await SettleAsync();

        var effectAll = harness.Snapshot().Where(entry => entry.Topic == KeyboardTopic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")).ToList();
        Assert.True(effectAll.Count >= 1, "GCU 改效果必须下发 Keyboard/Ctrl SetEffectALL。");
        string sequence = string.Join(" | ", harness.Snapshot().Select(entry =>
            entry.Topic + ":" + (entry.Payload.TryGetValue("function", out object? f) ? f : entry.Payload.GetValueOrDefault("Action"))
            + (entry.Payload.TryGetValue("effect", out object? e) ? "=" + e : "")));
        Assert.All(effectAll, entry =>
        {
            Assert.True(Equals("Wave", entry.Payload["effect"]), "下发序列：" + sequence);
            Assert.Equal("SAVE", entry.Payload["nv_save"]);
            Assert.False(IsChineseHidDisplayName(entry.Payload["effect"]));
        });
        Assert.Equal("Wave", LightingSettingsStore.Load(KeyboardTopic, "Single").Effect);
        Assert.Equal("Impact", LightingSettingsStore.Load(LightbarTopic, "Single").Effect);
    }

    [Fact]
    public async Task HidPath_KeepsBetterRgbTenAndNeverPublishesChineseHidNamesAsMqttEffect()
    {
        using var harness = new Harness(keyboardConnected: true);
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");

        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out ComboBox effectCombo);
        Program.UiAuditMode = false;
        harness.Clear();
        DetachUiContext();

        string[] hidNames = RgbForm.HidEffects.Select(e => e.Name).ToArray();
        Assert.Equal(10, hidNames.Length);
        Assert.Equal(hidNames, ComboLabels(effectCombo));
        Assert.Contains("流畅彩虹", hidNames);

        effectCombo.SelectedIndex = 1;
        await SettleAsync();

        foreach (var entry in harness.Snapshot())
        {
            if (!entry.Payload.TryGetValue("effect", out object? effect)) continue;
            Assert.False(IsChineseHidDisplayName(effect),
                "HID 路径不得把中文显示名当 MQTT effect，实际：" + effect);
        }
    }

    [Fact]
    public async Task GcuRgbForm_EnablesFirmwareEffectBrightnessSpeedAndSingleColor()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        // 呼吸同时采用亮度、速度、颜色（官方规格）；单色不采用速度，参数页不会出现速度（见下方断言）。
        LightingSettingsStore.Save(KeyboardTopic,
            new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));
        DetachUiContext();

        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;

        Control hidPanel = (Control)typeof(RgbForm).GetField("_hidPanel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Assert.Contains(Descendants(hidPanel), c => c is RSlider slider && slider.Enabled);
        var combos = Descendants(hidPanel).OfType<ComboBox>().Where(c => c.Enabled).ToList();
        Assert.DoesNotContain(combos, c => ComboLabels(c).SequenceEqual(VendorLabels));
        Assert.Contains(combos, c => ComboLabels(c).SequenceEqual(new[] { "慢", "中", "快" }));
        Assert.Contains(Descendants(hidPanel), c => c is Button && c.Enabled);
        var status = (Label)typeof(RgbForm).GetField("_lblStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Assert.DoesNotContain("仅电源与亮度", status.Text);

        // 单色效果：固件不采用速度 → 不给出速度控件（点了无效的控件就是伪功能）。
        LightingSettingsStore.Save(KeyboardTopic,
            new LightChannelSettings("Single", 4, 1, Color.White.ToArgb(), PowerOn: true));
        Program.UiAuditMode = true;
        using var singleForm = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;
        Control singlePanel = (Control)typeof(RgbForm).GetField("_hidPanel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(singleForm)!;
        Assert.DoesNotContain(Descendants(singlePanel).OfType<ComboBox>(),
            c => ComboLabels(c).SequenceEqual(new[] { "慢", "中", "快" }));
        Assert.Contains(Descendants(singlePanel), c => c is Button && c.Enabled);
    }

    /// <summary>
    /// ItemSupport\KeyboardType 是官方 RGBKB_Type 序号：1 = SingleZone（EC 单区：单色/彩虹，CCUWinUI
    /// SingleZoneKeyboardView），2 = FourZone（四区 6 项）。旧实现把 1/2 都当单区。
    /// </summary>
    [Fact]
    public void SingleZoneRgb_ExposesOnlySingleAndBreathing()
    {
        Assert.True(KeyboardFirmwareEffects.IsSingleZoneRgb(1));
        Assert.False(KeyboardFirmwareEffects.IsSingleZoneRgb(2));
        Assert.False(KeyboardFirmwareEffects.IsSingleZoneRgb(0));
        Assert.False(KeyboardFirmwareEffects.IsSingleZoneRgb(3));
        Assert.Equal(new[] { "Single", "Rainbow" }, KeyboardFirmwareEffects.Visible(1).Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "Single", "Breathing", "Wave", "Rainbow", "Mix", "Flash" },
            KeyboardFirmwareEffects.Visible(2).Select(e => e.Id).ToArray());
        Assert.Equal(KeyboardFirmwareEffects.All, KeyboardFirmwareEffects.Visible(0));
        Assert.Equal(new[] { "静态", "呼吸" }, RgbForm.VisibleHidEffects(1).Select(e => e.Name).ToArray());
        Assert.Equal(10, RgbForm.VisibleHidEffects(0).Length);
    }

    [Fact]
    public async Task GcuRgbForm_BrightnessSliderPersistsLightAndPublishesSetEffectAll()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        harness.Hardware.HandleMessage("Keyboard/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");
        LightingSettingsStore.Save(KeyboardTopic,
            new LightChannelSettings("Single", 4, 1, Color.White.ToArgb(), PowerOn: true));

        DetachUiContext();
        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;

        Control hidPanel = (Control)typeof(RgbForm).GetField("_hidPanel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        RSlider slider = Descendants(hidPanel).OfType<RSlider>().Single();
        harness.Clear();
        slider.Value = 50;

        Assert.Equal(2, LightingSettingsStore.Load(KeyboardTopic, "Single").Light);
        WaitUntilSync(() => harness.CountEffectAll(KeyboardTopic) > 0);
        Assert.Contains(harness.Snapshot(), entry =>
            entry.Topic == KeyboardTopic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL") &&
            Convert.ToInt32(entry.Payload["light"]) == 2);
    }

    [Fact]
    public async Task PublishGcuFirmwareEffect_Breathing_SendsColor()
    {
        using var harness = new Harness();
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());
        Color cyan = Color.FromArgb(0, 255, 255);
        var settings = new LightChannelSettings("Breathing", 4, 1, cyan.ToArgb(), PowerOn: true);
        LightingSettingsStore.Save(KeyboardTopic, settings);

        DetachUiContext();
        Program.UiAuditMode = true;
        using var form = new RgbForm(harness.Keyboard);
        Program.UiAuditMode = false;
        harness.Clear();

        typeof(RgbForm).GetMethod("PublishGcuFirmwareEffect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { settings });

        WaitUntilSync(() => harness.CountEffectAll(KeyboardTopic) > 0);
        var effectAll = harness.Snapshot().Where(entry => entry.Topic == KeyboardTopic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL")).ToList();
        Assert.True(effectAll.Count >= 1, "Breathing GCU publish must send Keyboard/Ctrl SetEffectALL.");
        Assert.All(effectAll, entry =>
        {
            Assert.Equal("Breathing", entry.Payload["effect"]);
            var color = Assert.IsAssignableFrom<IDictionary<string, object>>(entry.Payload["color"]);
            Assert.Equal(1, Convert.ToInt32(color["ColorBlocks"]));
            object[] buffer = Assert.IsType<object[]>(color["ColorBuffer"]);
            Assert.Single(buffer);
            var rgb = Assert.IsAssignableFrom<IDictionary<string, object>>(buffer[0]);
            Assert.Equal(0, Convert.ToInt32(rgb["R"]));
            Assert.Equal(255, Convert.ToInt32(rgb["G"]));
            Assert.Equal(255, Convert.ToInt32(rgb["B"]));
        });
    }

    [Fact]
    public async Task SingleZoneGcuCombo_OnlyListsSingleAndBreathing()
    {
        using var harness = new Harness(keyboardType: 1);
        Assert.False(await harness.Keyboard.EnsureHidReadyAsync());

        Program.UiAuditMode = true;
        using var form = BuildKeyboardForm(out ComboBox effectCombo);
        Program.UiAuditMode = false;

        Assert.Equal(new[] { "单色", "彩虹" }, ComboLabels(effectCombo));
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
