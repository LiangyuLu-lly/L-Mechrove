using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯光行「先改效果、再开电源」的回归锁。
///
/// 缺陷：通道关闭时改下拉仍会下发 SetEffectALL（闪 #1），随后开电源只发 SetPower，
/// 固件按上电默认效果初始化（闪 #2，且停在默认的常亮）——用户选的效果丢失。
/// 正确行为：关闭时只持久化选择；电源确认后按存档效果补发一次 SetEffectALL，
/// 最终态 = 用户选择的效果，且整条链路上只补发一次。
/// </summary>
public class LightRowPowerEffectTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";

    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    static (MechrevoHw Hardware, List<(string Topic, Dictionary<string, object> Payload)> Written) NewHardware()
    {
        var written = new List<(string, Dictionary<string, object>)>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                var copy = new Dictionary<string, object>(values);
                written.Add((topic, copy));
                // 回显电源状态，让 SetLightPower 的确认链路（WaitForStateAsync）收敛。
                if (copy.TryGetValue("powerstatus", out object? power) &&
                    (topic == LightbarTopic || topic == LogoTopic))
                {
                    string statusTopic = topic == LogoTopic ? "HidLightbar_Logo/Status" : "HidLightbar/Status";
                    string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                    hardware!.HandleMessage(statusTopic,
                        $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, MbaLogo = true });
        hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
        return (hardware, written);
    }

    static int EffectAllCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic, string? effect = null) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL") &&
            (effect is null || entry.Payload.TryGetValue("effect", out object? value) && Equals(value, effect)));

    static int PowerCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic, int? on = null) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("powerstatus", out object? power) &&
            (on is null || Convert.ToInt32(power) == on));

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
            await Task.Delay(10);
    }

    static TableLayoutPanel Row(Form form, string name) =>
        form.Controls.Find(name, true).OfType<TableLayoutPanel>().Single();

    static RCheckBox SwitchOf(TableLayoutPanel row) => row.Controls.OfType<RCheckBox>().Single();

    static ComboBox ComboOf(TableLayoutPanel row) => row.Controls.OfType<ComboBox>().Single();

    /// <summary>
    /// 每条用例都独占一份临时灯光配置目录，绝不触碰用户真实的 lightbar.cfg / logolight.cfg。
    /// 构造期置于 UiAuditMode（不触发任何下发），构造完成后再放开事件处理器以模拟真实交互。
    /// </summary>
    sealed class LightRowHarness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written;
        public readonly SettingsForm Form;

        readonly string _directory;
        readonly string? _previousOverride;
        readonly bool _previousAudit;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;

        /// <param name="secondChannel">可选副通道种子：两条灯行各自存档一条通道，
        /// 用于断言它们互不串台（topic、订阅出版、存档全独立）。</param>
        public LightRowHarness(string topic, string effect, bool powerOn,
            (string Topic, string Effect, bool PowerOn)? secondChannel = null)
        {
            _previousOverride = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);
            LightingSettingsStore.Save(topic, new LightChannelSettings(effect, 4, 1, Color.White.ToArgb(), powerOn));
            if (secondChannel is { } second)
                LightingSettingsStore.Save(second.Topic, new LightChannelSettings(second.Effect, 4, 1, Color.White.ToArgb(), second.PowerOn));

            (Hardware, Written) = NewHardware();
            _previousAudit = Program.UiAuditMode;
            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;

            Program.UiAuditMode = true;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Form = new SettingsForm();
            Form.CreateControl();
            Form.PerformLayout();
            Program.UiAuditMode = false;
        }

        public void Dispose()
        {
            Form.Dispose();
            Program.rgb = _previousRgb!;
            Program.service = _previousService!;
            Program.hw = _previousHardware!;
            Program.UiAuditMode = _previousAudit;
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousOverride);
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task LightbarCombo_WhileChannelOff_PersistsWithoutPublishing()
    {
        using var harness = new LightRowHarness(LightbarTopic, "Breathing", powerOn: false);
        var row = Row(harness.Form, "rowLightbar");
        var powerSwitch = SwitchOf(row);
        var effectCombo = ComboOf(row);
        Assert.False(powerSwitch.Checked, "前置：通道关闭时开关应为未勾选。");

        int target = Array.FindIndex(LightForm.LightbarEffects, e => e.Effect == "Wave");
        Assert.True(target >= 0, "灯条效果目录应包含 Wave。");
        effectCombo.SelectedIndex = target;

        await WaitUntil(() => LightingSettingsStore.Load(LightbarTopic, "Single").Effect == "Wave");

        Assert.Equal("Wave", LightingSettingsStore.Load(LightbarTopic, "Single").Effect);
        Assert.Equal(0, EffectAllCount(harness.Written, LightbarTopic));
    }

    [Fact]
    public async Task LightbarPowerOn_AppliesTheStoredEffectExactlyOnce()
    {
        using var harness = new LightRowHarness(LightbarTopic, "Breathing", powerOn: false);
        var row = Row(harness.Form, "rowLightbar");
        var powerSwitch = SwitchOf(row);
        Assert.False(powerSwitch.Checked, "前置：通道关闭时开关应为未勾选。");

        powerSwitch.Checked = true;

        await WaitUntil(() => EffectAllCount(harness.Written, LightbarTopic, "Breathing") >= 1);
        await Task.Delay(250);   // 收敛窗口：确认没有第二次补发

        Assert.Equal(1, EffectAllCount(harness.Written, LightbarTopic, "Breathing"));
        Assert.Equal(1, EffectAllCount(harness.Written, LightbarTopic));
        Assert.True(LightingSettingsStore.Load(LightbarTopic, "Single").PowerOn);
    }

    [Fact]
    public async Task LogoPowerOn_AppliesTheStoredEffectExactlyOnce()
    {
        using var harness = new LightRowHarness(LogoTopic, "Breathing", powerOn: false);
        var row = Row(harness.Form, "rowLogo");
        var powerSwitch = SwitchOf(row);
        Assert.False(powerSwitch.Checked, "前置：通道关闭时开关应为未勾选。");

        powerSwitch.Checked = true;

        await WaitUntil(() => EffectAllCount(harness.Written, LogoTopic, "Breathing") >= 1);
        await Task.Delay(250);   // 收敛窗口：确认没有第二次补发

        Assert.Equal(1, EffectAllCount(harness.Written, LogoTopic, "Breathing"));
        Assert.Equal(1, EffectAllCount(harness.Written, LogoTopic));
        Assert.True(LightingSettingsStore.Load(LogoTopic, "Single").PowerOn);
    }

    // ---------- 通道接线锁：两行各绑各的 topic / 存档，互不串台 ----------

    /// <summary>
    /// 两行引用的具名主题常量必须逐字节等于固件实际主题（接线锁用例的出版过滤同值）。
    /// 常量改错、或两行换用对方常量时，这里与上面的接线锁一起失败。
    /// </summary>
    [Fact]
    public void LightRowTopics_MatchFirmwareTopics()
    {
        Assert.Equal(LightbarTopic, MqttTopics.LightbarCtrl);
        Assert.Equal(LogoTopic, MqttTopics.LogoLightCtrl);
    }

    /// <summary>
    /// 灯条行只对 HidLightbar/Ctrl 出版（电源一次 + 存档效果一次），Logo 通道零出版、
    /// 零存档写入、开关不被联动。两行构造若把 topic/状态传串，本用例必须失败。
    /// </summary>
    [Fact]
    public async Task LightbarToggle_PublishesOnlyToLightbarTopic()
    {
        using var harness = new LightRowHarness(LightbarTopic, "Breathing", powerOn: false,
            secondChannel: (LogoTopic, "Mix", false));
        var lightbarSwitch = SwitchOf(Row(harness.Form, "rowLightbar"));
        var logoSwitch = SwitchOf(Row(harness.Form, "rowLogo"));
        Assert.False(lightbarSwitch.Checked, "前置：通道关闭时灯条开关应为未勾选。");
        Assert.False(logoSwitch.Checked, "前置：通道关闭时 Logo 开关应为未勾选。");

        lightbarSwitch.Checked = true;

        await WaitUntil(() => EffectAllCount(harness.Written, LightbarTopic, "Breathing") >= 1);
        await Task.Delay(250);   // 收敛窗口：确认没有第二次补发/串通道出版

        Assert.Equal(1, PowerCount(harness.Written, LightbarTopic, on: 1));
        Assert.Equal(1, EffectAllCount(harness.Written, LightbarTopic, "Breathing"));
        Assert.Equal(1, EffectAllCount(harness.Written, LightbarTopic));
        Assert.Equal(0, PowerCount(harness.Written, LogoTopic));
        Assert.Equal(0, EffectAllCount(harness.Written, LogoTopic));
        Assert.False(logoSwitch.Checked, "灯条切换不得联动 Logo 开关。");
        Assert.False(LightingSettingsStore.Load(LogoTopic, "Single").PowerOn, "灯条切换不得写 Logo 通道存档。");
        Assert.True(LightingSettingsStore.Load(LightbarTopic, "Single").PowerOn);
    }

    /// <summary>Logo 行只对 HidLightbar_Logo/Ctrl 出版；灯条通道零出版、零存档写入。</summary>
    [Fact]
    public async Task LogoToggle_PublishesOnlyToLogoTopic()
    {
        using var harness = new LightRowHarness(LogoTopic, "Mix", powerOn: false,
            secondChannel: (LightbarTopic, "Breathing", false));
        var lightbarSwitch = SwitchOf(Row(harness.Form, "rowLightbar"));
        var logoSwitch = SwitchOf(Row(harness.Form, "rowLogo"));
        Assert.False(lightbarSwitch.Checked, "前置：通道关闭时灯条开关应为未勾选。");
        Assert.False(logoSwitch.Checked, "前置：通道关闭时 Logo 开关应为未勾选。");

        logoSwitch.Checked = true;

        await WaitUntil(() => EffectAllCount(harness.Written, LogoTopic, "Mix") >= 1);
        await Task.Delay(250);   // 收敛窗口：确认没有第二次补发/串通道出版

        Assert.Equal(1, PowerCount(harness.Written, LogoTopic, on: 1));
        Assert.Equal(1, EffectAllCount(harness.Written, LogoTopic, "Mix"));
        Assert.Equal(1, EffectAllCount(harness.Written, LogoTopic));
        Assert.Equal(0, PowerCount(harness.Written, LightbarTopic));
        Assert.Equal(0, EffectAllCount(harness.Written, LightbarTopic));
        Assert.False(lightbarSwitch.Checked, "Logo 切换不得联动灯条开关。");
        Assert.False(LightingSettingsStore.Load(LightbarTopic, "Single").PowerOn, "Logo 切换不得写灯条通道存档。");
        Assert.True(LightingSettingsStore.Load(LogoTopic, "Single").PowerOn);
    }

    /// <summary>
    /// 通道关闭时改选效果只落本通道存档、不出版、不碰另一通道存档——
    /// 两行各有各的存档与开关状态，不存在共享门。
    /// </summary>
    [Fact]
    public async Task LightbarCombo_WhileOff_TouchesOnlyTheLightbarStore()
    {
        using var harness = new LightRowHarness(LightbarTopic, "Breathing", powerOn: false,
            secondChannel: (LogoTopic, "Mix", false));
        var lightbarCombo = ComboOf(Row(harness.Form, "rowLightbar"));

        int target = Array.FindIndex(LightForm.LightbarEffects, e => e.Effect == "Wave");
        Assert.True(target >= 0, "灯条效果目录应包含 Wave。");
        lightbarCombo.SelectedIndex = target;

        await WaitUntil(() => LightingSettingsStore.Load(LightbarTopic, "Single").Effect == "Wave");
        await Task.Delay(100);   // 收敛窗口：确认不出版

        Assert.Equal("Wave", LightingSettingsStore.Load(LightbarTopic, "Single").Effect);
        Assert.Equal("Mix", LightingSettingsStore.Load(LogoTopic, "Single").Effect);
        Assert.Equal(0, EffectAllCount(harness.Written, LightbarTopic));
        Assert.Equal(0, EffectAllCount(harness.Written, LogoTopic));
        Assert.Equal(0, PowerCount(harness.Written, LightbarTopic));
        Assert.Equal(0, PowerCount(harness.Written, LogoTopic));
    }
}
