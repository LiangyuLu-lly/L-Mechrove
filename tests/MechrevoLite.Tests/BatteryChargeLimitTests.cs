using System.Windows.Forms;
using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 充电上限：直写 EC 的阈值寄存器对（上限 0x7B9 + 复充下限 0x7D0）。
///
/// 寄存器选型经过两次真机返工（2026-09-11）。第一次：官方三档只往模式位 DBAP(0x7A6) 写
/// 0x08/0x18/0x28，全程不碰阈值寄存器，所以"性能/平衡/健康"在硬件上根本不限制充电。
/// 第二次：固件字段表里的 CGLM @ 0x78F 能写能回读，充电却完全不受它控制（设 80% 时
/// 一路涨到 86%）。真正生效的是官方服务常量里的一对 0x7B9/0x7D0——真机写 80/60 后充电
/// 立即停住，界面上设 96% 后停在 95%。
///
/// 这里锁住寄存器值语义、"下限必须严格小于上限"这条硬件约束，以及"写不进去就如实弹回、
/// 不谎报"的诚实回显契约。
/// </summary>
public class BatteryChargeLimitTests
{
    [Theory]
    [InlineData(100, 0)]    // 100% = 固件的「无上限」值（出厂状态就是 0）
    [InlineData(80, 80)]
    [InlineData(60, 60)]
    [InlineData(40, 40)]
    public void ChargeLimitRegisterValueKeepsThePercentAndMapsFullToNoLimit(int percent, int expected) =>
        Assert.Equal(expected, EcChargeLimit.ValueFor(percent));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(60, 60)]
    [InlineData(100, 100)]
    public void RegisterReadbackMapsBackToAPercent(int registerValue, int expected) =>
        Assert.Equal(expected, EcChargeLimit.PercentFor(registerValue));

    /// <summary>
    /// 复充下限必须严格小于上限（厂商服务与社区工具都强制这条约束），
    /// 且留出迟滞，避免电量在阈值附近反复充放。
    /// </summary>
    [Theory]
    [InlineData(100, 0)]    // 满充档：无上限，复充下限也归零
    [InlineData(80, 75)]
    [InlineData(61, 56)]
    [InlineData(40, 35)]
    public void RechargeLowerBoundStaysBelowTheUpperBound(int percent, int expectedLower) =>
        Assert.Equal(expectedLower, EcChargeLimit.LowerValueFor(percent));

    /// <summary>整条可调区间内，写入的那一对寄存器都必须满足 0 ≤ 下限 &lt; 上限 ≤ 100。</summary>
    [Fact]
    public void EveryAdjustableLimitProducesAValidRegisterPair()
    {
        for (int percent = EcChargeLimit.MinimumPercent; percent <= EcChargeLimit.MaximumPercent; percent++)
        {
            int upper = EcChargeLimit.ValueFor(percent);
            int lower = EcChargeLimit.LowerValueFor(percent);
            bool unlimited = upper == 0;

            Assert.InRange(upper, 0, 100);
            Assert.InRange(lower, 0, 100);
            if (!unlimited) Assert.True(lower < upper, $"{percent}% → 上限 {upper}、下限 {lower}：下限没有严格小于上限");
        }
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    [InlineData(73, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void OnlyTheAdjustableRangeIsAccepted(int percent, bool expected) =>
        Assert.Equal(expected, EcChargeLimit.IsSupportedLimit(percent));

    /// <summary>
    /// 官方那条路已整体摘除：产品里不能再有调用 BatteryProtection/Control 三档的入口
    /// （实测它只写模式位 DBAP，不限制充电）。协议方法本身留给诊断与将来复用。
    /// </summary>
    [Fact]
    public void TheOfficialBatteryProtectionFlowIsNoLongerWiredIntoTheProduct()
    {
        string source = File.ReadAllText(RepoFile(Path.Combine("src", "MechrevoLiteWin", "Battery", "BatteryControl.cs")));

        Assert.DoesNotContain("ApplyBatteryProtection", source);
        Assert.DoesNotContain("BatteryProtection/Control", source);
        Assert.DoesNotContain("PERFORMANCEDMODE", source);
    }

    /// <summary>
    /// 界面契约：电池面板里必须有一条 40..100 的连续滑条，键盘/滚轮步长 1%
    /// （SmallChange=1；真机实测 EC 接受任意百分比，精确到 1% 才有意义）。
    /// 它曾被三档按钮整条替换掉（Remove + Dispose）。
    /// </summary>
    [Fact]
    public void TheBatteryPanelOffersAContinuousOnePercentSliderWithAValueReadout()
    {
        using var form = new SettingsForm();
        var slider = form.Controls.Find("sliderBattery", true).OfType<RSlider>().Single();
        var panel = form.Controls.Find("panelBattery", true).OfType<Panel>().Single();

        Assert.Equal(40, slider.Minimum);
        Assert.Equal(100, slider.Maximum);
        Assert.Equal(1, slider.SmallChange);
        Assert.Same(slider, panel.Controls.Find("sliderBattery", true).OfType<RSlider>().Single());

        // 三档预设按钮已移除（用户决定：只留滑条）；百分比读数在滑条右侧。
        Assert.Empty(form.Controls.Find("buttonBatteryMode0", true));
        var value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
        form.VisualiseBatteryTitle(73);
        Assert.Equal("73%", value.Text);
    }

    /// <summary>沿目录向上找仓库根（含 MechrevoLite.slnx 的那一层）。</summary>
    static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }

    /// <summary>
    /// 机型门禁：EC 字段布局随机型而变，判定改由 FeatureMatrix + F3（不再是机型串匹配）。
    /// 三态与强制开关语义的完整覆盖见 <see cref="ChargeLimitGatingTests"/>。
    /// </summary>
    [Fact]
    public void OnlyVerifiedMachinesMayWriteTheRegister()
    {
        string? previous = AppConfig.GetString("ec_charge_limit");
        try
        {
            AppConfig.Remove("ec_charge_limit");
            var profile = FeatureMatrix.FromValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["KeyboardSupport"] = 1,
            });
            var empty = FeatureMatrix.FromValues(new Dictionary<string, object?>());
            var supported = new SupportDecision(true, SupportReason.Ok, "PH4TRX1");
            var unparsable = SupportDecision.Unparsable();

            Assert.True(EcChargeLimit.IsSupportedMachine(supported, profile));
            Assert.False(EcChargeLimit.IsSupportedMachine(unparsable, profile));
            Assert.False(EcChargeLimit.IsSupportedMachine(supported, empty));

            AppConfig.Set("ec_charge_limit", "1");
            Assert.True(EcChargeLimit.IsSupportedMachine(unparsable, empty));
            AppConfig.Set("ec_charge_limit", "0");
            Assert.False(EcChargeLimit.IsSupportedMachine(supported, profile));
        }
        finally
        {
            if (previous is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", previous);
        }
    }

}

/// <summary>
/// 电池健康信息的展示。官方推流一直带着循环次数与设计容量，官方界面却不展示，
/// 而它比 powercfg /batteryreport 实时得多。
/// </summary>
public class BatteryHealthDisplayTests
{
    static MechrevoLite.Hardware.MechrevoHw NewHardware() => new();

    /// <summary>什么都不知道时不能显示占位值——-1 之类的数字比不显示更糟。</summary>
    [Fact]
    public void NothingIsShownBeforeTheDeviceReportsAnything()
    {
        using var hardware = NewHardware();

        Assert.Equal("", MechrevoLite.SettingsForm.BatteryHealthSuffix(hardware));
    }

    [Fact]
    public void NullHardwareYieldsNoSuffix() =>
        Assert.Equal("", MechrevoLite.SettingsForm.BatteryHealthSuffix(null));

    [Fact]
    public void CycleCountIsShownOnceReported()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/BatteryInfo", "{\"BatteryCycleCount\":137}");

        string suffix = MechrevoLite.SettingsForm.BatteryHealthSuffix(hardware);
        Assert.Contains("137", suffix);
    }

    [Fact]
    public void CapacityAndAbnormalFlagAreShownTogetherWithCycles()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/BatteryInfo", """
            {"BatteryCycleCount":42,"BatteryCapacity":"80000 mWh","BatteryAbnormal":true}
            """);

        string suffix = MechrevoLite.SettingsForm.BatteryHealthSuffix(hardware);
        Assert.Contains("42", suffix);
        Assert.Contains("80000 mWh", suffix);
        Assert.Contains("电池异常", suffix);
    }

    /// <summary>电池正常时不该出现异常字样。</summary>
    [Fact]
    public void NoAbnormalWarningWhenTheBatteryIsHealthy()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/BatteryInfo", "{\"BatteryCycleCount\":10,\"BatteryAbnormal\":false}");

        Assert.DoesNotContain("异常", MechrevoLite.SettingsForm.BatteryHealthSuffix(hardware));
    }
}

/// <summary>
/// 真机运行发现的缺陷回归：设计容量报的是 "0 mWh"。
///
/// 开发机实测日志：BatteryInfo percent=100 cycles=15 capacity=0 mWh abnormal=False
/// 字段非空但数值是零，直接展示等于告诉用户「这块电池容量是 0」。
/// </summary>
public class BatteryCapacityDisplayRegressionTests
{
    [Theory]
    [InlineData("0 mWh")]
    [InlineData("0")]
    [InlineData("0 mAh")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("unknown")]
    public void AZeroOrUnparseableCapacityIsNotWorthShowing(string? capacity) =>
        Assert.False(MechrevoLite.SettingsForm.HasMeaningfulCapacity(capacity));

    [Theory]
    [InlineData("80000 mWh")]
    [InlineData("99900 mWh")]
    [InlineData("4500 mAh")]
    public void ARealCapacityIsShown(string capacity) =>
        Assert.True(MechrevoLite.SettingsForm.HasMeaningfulCapacity(capacity));

    /// <summary>
    /// 用开发机实测的整条载荷验证：循环次数要显示，"0 mWh" 的容量不能显示。
    /// </summary>
    [Fact]
    public void TheRealDevMachinePayloadShowsCyclesButNotTheZeroCapacity()
    {
        using var hardware = new MechrevoLite.Hardware.MechrevoHw();

        hardware.HandleMessage("System/BatteryInfo", """
            {"BatteryLifePercent":100,"BatteryCycleCount":15,"BatteryCapacity":"0 mWh","BatteryAbnormal":false}
            """);

        string suffix = MechrevoLite.SettingsForm.BatteryHealthSuffix(hardware);
        Assert.Contains("15", suffix);
        Assert.DoesNotContain("0 mWh", suffix);
        Assert.DoesNotContain("异常", suffix);
    }
}
