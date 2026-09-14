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
        }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true });
        return (hardware, written);
    }

    static int EffectAllCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic, string? effect = null) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL") &&
            (effect is null || entry.Payload.TryGetValue("effect", out object? value) && Equals(value, effect)));

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

        public LightRowHarness(string topic, string effect, bool powerOn)
        {
            _previousOverride = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);
            LightingSettingsStore.Save(topic, new LightChannelSettings(effect, 4, 1, Color.White.ToArgb(), powerOn));

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
}
