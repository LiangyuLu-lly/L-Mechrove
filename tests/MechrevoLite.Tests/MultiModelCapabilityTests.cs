using MechrevoLite;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 本项目要覆盖机械革命全系机型（官方 <c>ProjectID</c> 枚举 45 项、
/// <c>BIOS_PROJECT_ID</c> 37 项，见 docs/hardware/project-ids.json），
/// 所以任何能力判定都不能写成开发机的实测结果。
///
/// 这一组测试锁的就是这条：适配层必须按传入的机型画像回答，而不是返回常量。
/// 每个用例都构造一份「与开发机不同」的画像，如果实现退回硬编码就会失败。
/// </summary>
public class MultiModelCapabilityTests : IDisposable
{
    public MultiModelCapabilityTests() => MechrevoDeviceCapabilities.OverrideCurrent(null);

    public void Dispose() => MechrevoDeviceCapabilities.OverrideCurrent(null);

    static MechrevoDeviceCapabilities Profile(params (string Key, object Value)[] values)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object value) in values) map[key] = value;
        return MechrevoDeviceCapabilities.FromValues(map);
    }

    // ---------- 显卡厂商不能写死 ----------

    [Fact]
    public void NvidiaGpu_FollowsTheDeviceProfileNotTheDevelopmentMachine()
    {
        // 开发机是 NVIDIA + Intel，此前 AsusACPI.IsNVidiaGPU() 直接 => true。
        // AMD 独显机型上那就是错的。
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsNvGpu", 0)));
        Assert.False(new AsusACPI().IsNVidiaGPU());

        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsNvGpu", 1)));
        Assert.True(new AsusACPI().IsNVidiaGPU());
    }

    [Fact]
    public void AmdPlatform_FollowsTheDeviceProfile()
    {
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsAMDPlatform", 1)));
        Assert.True(new AsusACPI().IsAllAmdPPT());

        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsAMDPlatform", 0)));
        Assert.False(new AsusACPI().IsAllAmdPPT());
    }

    [Fact]
    public void LcdOverdrive_FollowsTheDeviceProfile()
    {
        // 开发机 LCDOverdriveSupport=0，此前实现恒返回 false，
        // 支持该功能的机型上入口会永久消失。
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("LCDOverdriveSupport", 1)));
        Assert.True(new AsusACPI().IsOverdriveSupported());

        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("LCDOverdriveSupport", 0)));
        Assert.False(new AsusACPI().IsOverdriveSupported());
    }

    [Fact]
    public void DeviceCodeSupport_FollowsTheDeviceProfile()
    {
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(
            ("LCDOverdriveSupport", 1), ("LocalDimmingSupport", 1)));
        var acpi = new AsusACPI();
        Assert.True(acpi.IsSupported(AsusACPI.ScreenOverdrive));
        Assert.True(acpi.IsSupported(AsusACPI.ScreenMiniled1));
        Assert.True(acpi.IsSupported(AsusACPI.ScreenMiniled2));

        MechrevoDeviceCapabilities.OverrideCurrent(Profile(
            ("LCDOverdriveSupport", 0), ("LocalDimmingSupport", 0)));
        acpi = new AsusACPI();
        Assert.False(acpi.IsSupported(AsusACPI.ScreenOverdrive));
        Assert.False(acpi.IsSupported(AsusACPI.ScreenMiniled1));
    }

    [Fact]
    public void UnknownDeviceCode_IsNeverReportedAsSupported()
    {
        // 未映射的设备码必须与 DeviceGet 返回 NotSupported 保持同一套语义，
        // 否则继承代码会基于「看起来可用」去调用空实现。
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(
            ("LCDOverdriveSupport", 1), ("LocalDimmingSupport", 1), ("IsNvGpu", 1)));
        var acpi = new AsusACPI();
        Assert.False(acpi.IsSupported(AsusACPI.ScreenOptimalBrightness));
        Assert.False(acpi.IsSupported(AsusACPI.ScreenFHD));
        // 字面量取代已删除的 AsusACPI.ChargerMode：该设备码没有数据来源。
        Assert.False(acpi.IsSupported(0x0012006C));
    }

    [Fact]
    public void XgMobile_IsAlwaysAbsentBecauseNoMechrevoModelHasIt()
    {
        // 这一条不是「未适配」：XG Mobile 是 ASUS 的外置显卡坞接口，
        // 机械革命全系都没有。恒 false 是正确答案，不是单机假设。
        foreach (int nvidia in new[] { 0, 1 })
        {
            MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsNvGpu", nvidia)));
            Assert.False(new AsusACPI().IsXGConnected());
        }
    }

    // ---------- 能力位别名容错必须覆盖全部已知写法 ----------

    [Theory]
    [InlineData("iGPUModeOnlySupport")]
    [InlineData("IGpuOnlyModeSupport")]
    [InlineData("IntegratedGpuOnlySupport")]
    public void IgpuOnly_AcceptsEveryKnownRegistrySpelling(string key)
    {
        // 不同 GCU 版本用不同拼写。只认一种就等于在部分机型上丢功能。
        Assert.True(Profile((key, 1)).IgpuOnly, $"能力位别名 {key} 未被识别。");
    }

    [Theory]
    [InlineData("DGpuDirectConnectionSupport")]
    [InlineData("DiscreteGpuDirectConnectionSupport")]
    [InlineData("MuxSwitchSupport")]
    public void DgpuDirect_AcceptsEveryKnownRegistrySpelling(string key)
    {
        Assert.True(Profile((key, 1)).DgpuDirect, $"能力位别名 {key} 未被识别。");
    }

    [Theory]
    [InlineData("LiquidCoolingSupport")]
    [InlineData("WaterCoolingSupport")]
    public void LiquidCooling_AcceptsEveryKnownRegistrySpelling(string key)
    {
        Assert.True(Profile((key, 1)).LiquidCooling, $"能力位别名 {key} 未被识别。");
    }

    // ---------- 功耗常量是外层边界，不是机型限值 ----------

    [Fact]
    public void PowerLimitConstants_AreOuterBoundsNotPerModelLimits()
    {
        // docs/hardware/fan-table-defaults.json 里 PH4TRX1 的出厂表是 PL1=35 / PL2=60，
        // 而开发机（IDY）实测上限是 210。这些常量只能当荒谬值过滤器，
        // 面向用户的范围必须用 MechrevoHw 的运行时 Pl1Minimum/Pl1Maximum。
        Assert.True(AsusACPI.MaxTotal >= 210, "外层上限不能低于已知机型的实际上限。");
        Assert.True(AsusACPI.MinTotal <= 35, "外层下限不能高于已知机型的出厂 PL1。");
        Assert.True(AsusACPI.MinCPU <= 35);
    }

    [Fact]
    public void RuntimeRangesAreUnsetUntilTheServiceReportsThem()
    {
        // 没有运行时上报时必须表现为「不可调」，不能拿常量顶上去当成机型能力。
        using var hardware = new MechrevoHw(null, Profile(("FanSettingsSupport", 1)));
        Assert.False(hardware.Pl1Adjustable);
        Assert.False(hardware.Pl2Adjustable);
        Assert.False(hardware.TccAdjustable);
        Assert.False(hardware.GpuTgpAdjustable);
        Assert.False(hardware.GpuDynamicBoostAdjustable);
    }

    // ---------- 共享画像的缓存语义 ----------

    [Fact]
    public void CurrentProfile_IsCachedAndInvalidatable()
    {
        MechrevoDeviceCapabilities first = Profile(("IsNvGpu", 1));
        MechrevoDeviceCapabilities.OverrideCurrent(first);
        Assert.Same(first, MechrevoDeviceCapabilities.Current);

        MechrevoDeviceCapabilities second = Profile(("IsNvGpu", 0));
        MechrevoDeviceCapabilities.OverrideCurrent(second);
        Assert.Same(second, MechrevoDeviceCapabilities.Current);
    }

    [Fact]
    public void ConnectedInstanceProfileWinsOverTheSharedSnapshot()
    {
        // 已连接实例上的画像含 MQTT 运行时纠偏，优先级高于进程级快照。
        // 这里只能验证快照侧；实例侧由 MechrevoHw 的构造参数覆盖，见上面的用例。
        MechrevoDeviceCapabilities.OverrideCurrent(Profile(("IsNvGpu", 0)));
        Assert.False(MechrevoDeviceCapabilities.Current.NvidiaGpu);

        using var hardware = new MechrevoHw(null, Profile(("IsNvGpu", 1)));
        Assert.True(hardware.Capabilities.NvidiaGpu);
    }
}
