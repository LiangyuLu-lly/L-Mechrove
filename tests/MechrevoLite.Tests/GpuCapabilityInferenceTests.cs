using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 显卡切换能力的推断规则。这是唯一一类误报会让用户点到本机不具备的破坏性入口的能力，
/// 所以只接受两种证据：显式的 _Support 位，或者能被解析成具体模式的状态字符串。
/// </summary>
public class GpuCapabilityInferenceTests
{
    static MechrevoHw NewHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

    // ---- 可识别状态 == 能力证据 ----

    [Theory]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_ON")]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_IGPU")]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_OFF")]
    [InlineData("dgpu_direct_connection_on")]
    public void RecognizedDgpuDirectStatus_CountsAsEvidence(string status) =>
        Assert.True(MechrevoHw.IsRecognizedDgpuDirectStatus(status));

    [Theory]
    [InlineData("IGPU_ONLY_CONNECT_RB_ON")]
    [InlineData("IGPU_ONLY_CONNECT_RB_AUTO")]
    [InlineData("IGPU_ONLY_CONNECT_RB_OFF")]
    [InlineData("igpu_only_auto")]
    public void RecognizedIgpuOnlyStatus_CountsAsEvidence(string status) =>
        Assert.True(MechrevoHw.IsRecognizedIgpuOnlyStatus(status));

    /// <summary>
    /// 这些值过去都会被「非空即支持」判成支持，是 H4 的核心回归点。
    /// </summary>
    [Theory]
    [InlineData("NOT_SUPPORT")]
    [InlineData("NOT_SUPPORTED")]
    [InlineData("UNKNOWN")]
    [InlineData("NONE")]
    [InlineData("SUPPORT")]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData(null)]
    public void UnrecognisedStatusIsNotEvidenceForEitherSwitchFamily(string? status)
    {
        Assert.False(MechrevoHw.IsRecognizedDgpuDirectStatus(status));
        Assert.False(MechrevoHw.IsRecognizedIgpuOnlyStatus(status));
    }

    /// <summary>
    /// 防漂移守卫：只要一个状态能被 ResolveGpuModeStatus 解析成具体模式，
    /// 它就必须同时被当作能力证据。否则两边的记法表会各自演化，
    /// 出现「能读出模式却认为不支持」的死角。
    /// 反向不要求相等——能力证据是个超集，例如 DGPU_DIRECT_CONNECT_TOGGLE_OFF
    /// 单独出现时无法定位到具体模式，但它确实证明这套命令族存在。
    /// </summary>
    [Theory]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_ON", null)]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_IGPU", null)]
    [InlineData("DGPU_DIRECT_CONNECTION_ON", null)]
    [InlineData("DGPU_DIRECT_CONNECTION_OFF", null)]
    [InlineData(null, "IGPU_ONLY_CONNECT_RB_ON")]
    [InlineData(null, "IGPU_ONLY_CONNECT_RB_AUTO")]
    [InlineData(null, "IGPU_ONLY_CONNECT_RB_OFF")]
    [InlineData(null, "IGPU_ONLY_ON")]
    [InlineData(null, "IGPU_ONLY_AUTO")]
    [InlineData(null, "IGPU_ONLY_OFF")]
    public void EveryStatusThatResolvesToAConcreteModeIsAlsoCapabilityEvidence(string? direct, string? igpu)
    {
        const int sentinel = -99;

        int resolved = MechrevoHw.ResolveGpuModeStatus(sentinel, direct, igpu);

        Assert.NotEqual(sentinel, resolved);
        Assert.True(
            MechrevoHw.IsRecognizedDgpuDirectStatus(direct) ||
            MechrevoHw.IsRecognizedIgpuOnlyStatus(igpu),
            $"'{direct}' / '{igpu}' 能解析成模式 {resolved}，却没有被当作能力证据");
    }

    // ---- 端到端：报文 → 能力 ----

    /// <summary>
    /// H4 主回归：支持位是无法识别的字符串 + 状态是无法识别的字符串，
    /// 过去会推断出「支持」并覆盖 ItemSupport 画像，UI 因此暴露显卡切换入口。
    /// </summary>
    [Fact]
    public void UnparsableSupportBitWithUnrecognisedStatusDoesNotFabricateSupport()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {
              "DGpuDirectConnectionSwitch_Support": "NOT_SUPPORT",
              "DiscreteGpuDirectConnectionSwitch_Status": "UNKNOWN",
              "IGpuOnlyConnectionSwitch_Support": "NOT_SUPPORT",
              "IGpuOnlyConnectionSwitch_Status": "NONE"
            }
            """);

        Assert.False(hardware.SupportsDgpuDirect);
        Assert.False(hardware.SupportsIgpuOnly);
        Assert.False(hardware.SupportsGpuHotSwap);
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuDgpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuAuto));
    }

    /// <summary>
    /// 保留原本的合理意图：旧版 GCU 只上报状态、不上报独立的支持位时，
    /// 可识别的状态仍然应该推断出支持。
    /// </summary>
    [Fact]
    public void RecognizedStatusWithoutSupportBitStillEnablesTheSwitchFamily()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {
              "DiscreteGpuDirectConnectionSwitch_Status": "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
              "IGpuOnlyConnectionSwitch_Status": "IGPU_ONLY_CONNECT_RB_OFF"
            }
            """);

        Assert.True(hardware.SupportsDgpuDirect);
        Assert.True(hardware.SupportsIgpuOnly);
        Assert.Equal(MechrevoService.GpuStandard, hardware.GpuMode);
    }

    /// <summary>
    /// 离开纯集显时官方会再发 <c>DGPU_DIRECT_CONNECT_TOGGLE_OFF</c> 重开独显通路。
    /// TOGGLE_OFF 的语义是「不是独显直连」，不是「已经是混合」。RB 仍为 ON 时机器还在集显；
    /// 若 TOGGLE_OFF 一律映射成 GpuStandard，GpuSwitchPolicy 会把离开集显判成 NoChange。
    /// </summary>
    [Fact]
    public void ResolveGpuModeStatus_ToggleOff_DoesNotMaskIgpuLeave()
    {
        int resolved = MechrevoHw.ResolveGpuModeStatus(
            MechrevoService.GpuIGpu,
            "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
            "IGPU_ONLY_CONNECT_RB_ON");

        Assert.NotEqual(MechrevoService.GpuStandard, resolved);
        Assert.Equal(MechrevoService.GpuIGpu, resolved);

        GpuSwitchPlan plan = GpuSwitchPolicy.Resolve(
            resolved,
            automaticRuntime: 2,
            MechrevoService.GpuStandard,
            supportsHotSwap: true);
        Assert.NotEqual(GpuSwitchRoute.NoChange, plan.Route);
    }

    /// <summary>
    /// 显式上报为不支持时，后续可识别的状态不得把它翻回支持。
    /// </summary>
    [Fact]
    public void ExplicitNegativeSupportBitIsNotOverriddenByALaterRecognizedStatus()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", "{\"IGpuOnlyConnectionSwitch_Support\":false}");
        Assert.False(hardware.SupportsIgpuOnly);

        hardware.HandleMessage("Setting/Status",
            "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_AUTO\"}");

        Assert.False(hardware.SupportsIgpuOnly);
        Assert.False(hardware.SupportsGpuHotSwap);
    }

    /// <summary>
    /// 支持位现在能读懂 SUPPORT / NOT_SUPPORT 这类字符串记法。
    /// </summary>
    [Theory]
    [InlineData("SUPPORT", true)]
    [InlineData("Supported", true)]
    [InlineData("1", true)]
    [InlineData("NOT_SUPPORT", false)]
    [InlineData("UNSUPPORTED", false)]
    [InlineData("0", false)]
    public void SupportBitUnderstandsStringSpellings(string raw, bool expected)
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", $"{{\"IGpuOnlyConnectionSwitch_Support\":\"{raw}\"}}");

        Assert.Equal(expected, hardware.SupportsIgpuOnly);
    }

    /// <summary>
    /// 热切换的门禁是「注册表画像 AND 核显直通」。伪造的核显直通不能再撬开它，
    /// 这正是 GPU 热切换计划里那条全局约束的要求。
    /// </summary>
    [Fact]
    public void HotSwapStillRequiresTheRegistryGateEvenWithRecognizedStatus()
    {
        using var withoutRegistryGate = new MechrevoHw(
            (_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { GpuHotSwap = false });
        using var withRegistryGate = new MechrevoHw(
            (_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { GpuHotSwap = true });

        const string status = "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}";
        withoutRegistryGate.HandleMessage("Setting/Status", status);
        withRegistryGate.HandleMessage("Setting/Status", status);

        Assert.True(withoutRegistryGate.SupportsIgpuOnly);
        Assert.False(withoutRegistryGate.SupportsGpuHotSwap);
        Assert.True(withRegistryGate.SupportsGpuHotSwap);
    }

    // ---- 风扇独立控制能力 ----

    /// <summary>
    /// FanControlRespective 过去是裸字符串比较，"ON" 会被判成 false，
    /// 而且无论能否识别都把 Seen 置位——SupportsFanRespective 完全依赖该标志。
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("\"ON\"", true)]
    [InlineData("\"ENABLE\"", true)]
    [InlineData("false", false)]
    [InlineData("\"OFF\"", false)]
    public void FanRespective_UnderstandsEveryKnownSpelling(string raw, bool expected)
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", $"{{\"FanControlRespective\":{raw}}}");

        Assert.True(hardware.SupportsFanRespective);
        Assert.Equal(expected, hardware.FanRespective);
    }

    [Fact]
    public void FanRespective_UnrecognisedValueDoesNotClaimTheCapability()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"FanControlRespective\":\"WHATEVER\"}");

        Assert.False(hardware.SupportsFanRespective);
    }
}
