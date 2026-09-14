using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// run5 液冷组摘要档位 + 屏幕校色行头下拉（2026-09-14 用户要求）：
/// - 摘要在手动档也要显示档位（「已连接 · 泵60% · 风扇自动」），最高档读「最大」；
/// - 校色二级弹窗（P3/AdobeRGB 不生效）删除，改为屏幕行头内联下拉（默认/sRGB 两档）。
/// </summary>
public class Run5LcCalibTests
{
    const string UnsetSentinel = "__unset__";

    static (int Pump, int Fan) SaveLcProfiles()
    {
        int pump = AppConfig.Get("lc_pump_profile", WaterCoolerBle.ProfileUnset);
        int fan = AppConfig.Get("lc_fan_profile", WaterCoolerBle.ProfileUnset);
        return (pump, fan);
    }

    static void RestoreLcProfiles((int Pump, int Fan) saved)
    {
        AppConfig.Set("lc_pump_profile", saved.Pump);
        AppConfig.Set("lc_fan_profile", saved.Fan);
    }

    static string SummaryWithProfiles(int pump, int fan)
    {
        var saved = SaveLcProfiles();
        WaterCoolerBle? savedBle = Program.ble;
        Program.ble = null;   // 走 GCU 通道的 AppConfig 路径（测试环境无 BLE 实例）
        try
        {
            AppConfig.Set("lc_pump_profile", pump);
            AppConfig.Set("lc_fan_profile", fan);
            return SettingsForm.LcSummaryText("GCU 已连接水冷");
        }
        finally
        {
            Program.ble = savedBle;
            RestoreLcProfiles(saved);
        }
    }

    /// <summary>摘要档位映射表（泵 45/60/90、风扇 40/50/60/90，最高档=最大）。</summary>
    [Theory]
    [InlineData(WaterCoolerBle.ProfileAutomatic, WaterCoolerBle.ProfileAutomatic, "已连接 · 泵自动 · 风扇自动")]
    [InlineData(1, 1, "已连接 · 泵60% · 风扇50%")]
    [InlineData(1, WaterCoolerBle.ProfileAutomatic, "已连接 · 泵60% · 风扇自动")]
    [InlineData(WaterCoolerBle.ProfileAutomatic, 1, "已连接 · 泵自动 · 风扇50%")]
    [InlineData(WaterCoolerBle.TopPumpProfile, WaterCoolerBle.TopFanProfile, "已连接 · 泵最大 · 风扇最大")]
    [InlineData(0, 2, "已连接 · 泵45% · 风扇60%")]
    [InlineData(WaterCoolerBle.ProfileUnset, WaterCoolerBle.ProfileUnset, "已连接")]
    public void LcSummary_ShowsTheSavedGearPerChannel(int pump, int fan, string expected)
    {
        Assert.Equal(expected, SummaryWithProfiles(pump, fan));
    }

    /// <summary>最高档措辞必须与档位下拉一致（同一张表 + GearLabel）。</summary>
    [Fact]
    public void GearSummaryLabel_TopGearReadsMaximumLikeTheDropdown()
    {
        Assert.Equal("泵最大", LiquidCoolingDisplayPolicy.GearSummaryLabel(
            "泵", LiquidCoolingDisplayPolicy.PumpGearLabels, WaterCoolerBle.TopPumpProfile, WaterCoolerBle.TopPumpProfile));
        Assert.Equal("风扇最大", LiquidCoolingDisplayPolicy.GearSummaryLabel(
            "风扇", LiquidCoolingDisplayPolicy.FanGearLabels, WaterCoolerBle.TopFanProfile, WaterCoolerBle.TopFanProfile));
        Assert.Null(LiquidCoolingDisplayPolicy.GearSummaryLabel(
            "泵", LiquidCoolingDisplayPolicy.PumpGearLabels, WaterCoolerBle.ProfileUnset, WaterCoolerBle.TopPumpProfile));
    }

    /// <summary>校色下拉只有两档：默认(1)/sRGB(2)——P3/AdobeRGB 已删。</summary>
    [Fact]
    public void ColorCalibrationCombo_HasExactlyDefaultAndSrgb()
    {
        using var form = NewAuditSettingsForm();
        ComboBox combo = form.Controls.Find("comboColorCalibration", true).OfType<ComboBox>().Single();

        Assert.Equal(2, combo.Items.Count);
        Assert.Equal("默认", Assert.IsType<KeyValuePair<string, int>>(combo.Items[0]).Key);
        Assert.Equal(1, Assert.IsType<KeyValuePair<string, int>>(combo.Items[0]).Value);
        Assert.Equal("sRGB", Assert.IsType<KeyValuePair<string, int>>(combo.Items[1]).Key);
        Assert.Equal(2, Assert.IsType<KeyValuePair<string, int>>(combo.Items[1]).Value);
    }

    /// <summary>回显：同帧上报的当前档（CurrentColorCalibration）决定下拉选中项。</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void ColorCalibrationCombo_ReflectsTheCurrentMode(int mode, int expectedIndex)
    {
        MechrevoHw? previousHardware = Program.hw;
        using var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities { ColorCalibration = true });
        hardware.HandleMessage("Setting/Status", $"{{\"CurrentColorCalibration\":{mode}}}");
        Program.hw = hardware;
        try
        {
            using var form = NewAuditSettingsForm();
            ComboBox combo = form.Controls.Find("comboColorCalibration", true).OfType<ComboBox>().Single();
            Assert.Equal(expectedIndex, combo.SelectedIndex);
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    /// <summary>旧档位（3/4 已删）与未知值回落 默认，不发明新档。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(99, 0)]
    public void CalibComboIndex_MapsOnlyTheTwoLiveModes(int mode, int expectedIndex)
    {
        Assert.Equal(expectedIndex, SettingsForm.CalibComboIndexFor(mode));
    }

    /// <summary>行头布局：校色下拉在 自动刷新率 开关**左侧**，同一行头 TableLayoutPanel 内。</summary>
    [Fact]
    public void ScreenHeadRow_PlacesCalibComboLeftOfTheAutoRefreshSwitch()
    {
        using var form = NewAuditSettingsForm();
        ComboBox combo = form.Controls.Find("comboColorCalibration", true).OfType<ComboBox>().Single();
        CheckBox autoHz = form.Controls.Find("checkAutoRefreshRate", true).OfType<CheckBox>().Single();

        // 可见性由 RefreshDeviceCapabilities 的能力位控制（审计/测试环境不触发），
        // 这里只锁几何：两控件同处一个行头 TLP，下拉整体在开关左侧、不裁切。
        Assert.Same(autoHz.Parent, combo.Parent);
        var head = Assert.IsAssignableFrom<TableLayoutPanel>(combo.Parent);
        Assert.True(head.GetColumn(combo) < head.GetColumn(autoHz),
            $"校色下拉（列 {head.GetColumn(combo)}）必须在 自动刷新率 开关（列 {head.GetColumn(autoHz)}）左侧。");
        // 下拉自身必须装进行头（开关的几何由 UI 审计护栏覆盖，这里不重复断言）。
        Assert.True(combo.Bottom <= head.Bottom,
            $"行头高 {head.Height} 装不下下拉（Bottom={combo.Bottom}）。");
    }

    /// <summary>写入路径：下拉沿用旧弹窗的 MechrevoService.SetColorCalibration（原版协议）。</summary>
    [Fact]
    public void ColorCalibrationCombo_WritesThroughTheExistingServicePath()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "Settings.cs"));
        int comboHandler = source.IndexOf("comboColorCalibration", StringComparison.Ordinal);
        int writeCall = source.IndexOf("await Program.service.SetColorCalibration(requestedMode)", StringComparison.Ordinal);
        Assert.True(comboHandler >= 0 && writeCall > comboHandler,
            "校色下拉的 SelectedIndexChanged 处理器必须经 Program.service.SetColorCalibration 写入。");
    }

    /// <summary>死路径护栏：校色表单文件已删，UI 层（行头/弹窗/审计）不得再出现 P3/AdobeRGB。
    /// （Hardware/MechrevoService 的 mode 3/4 是厂商协议解析层，保留——UI 已无入口，见报告。）</summary>
    [Fact]
    public void RemovedGamutModes_StayRemoved()
    {
        Assert.False(File.Exists(RepoRootPath("src", "MechrevoLiteWin", "ColorCalibrationForm.cs")));
        foreach (string uiFile in new[]
        {
            RepoRootPath("src", "MechrevoLiteWin", "Settings.cs"),
            RepoRootPath("src", "MechrevoLiteWin", "SettingsDialog.cs"),
            RepoRootPath("src", "MechrevoLiteWin", "UI", "UiAuditRunner.cs"),
        })
        {
            string text = File.ReadAllText(uiFile);
            Assert.DoesNotContain("\"P3\"", text);
            Assert.DoesNotContain("\"AdobeRGB\"", text);
            Assert.DoesNotContain("ColorCalibrationForm", text);
        }
    }

    static SettingsForm NewAuditSettingsForm()
    {
        bool previousAuditMode = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            return form;
        }
        finally
        {
            Program.UiAuditMode = previousAuditMode;
        }
    }

    static string RepoRootPath(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return System.IO.Path.Combine(directory!.FullName, System.IO.Path.Combine(tail));
    }
}
