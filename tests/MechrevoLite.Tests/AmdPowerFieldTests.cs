using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// AMD 功耗字段体系的判定。判错平台的代价是功耗墙永久设不上：
/// 写入会走 CpuAmdSPL/CpuAmdSPPT 键，被 GCU 忽略，确认必然超时。
/// </summary>
public class AmdPowerFieldTests
{
    static MechrevoHw NewIntelLikeHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { AmdPlatform = false });

    static MechrevoHw NewAmdProfileHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { AmdPlatform = true });

    /// <summary>
    /// H5 主回归：Intel 机型上报了一个占位的 CPU_AmdSPL: 0。
    /// 过去这会把平台永久判成 AMD，并让 Pl1 永久锁在 0 W，真实的 CPU_PL1 被丢弃。
    /// </summary>
    [Fact]
    public void PlaceholderZeroAmdFieldDoesNotSwitchAnIntelMachineToTheAmdChannel()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_AmdSPL": 0,
              "CPU_AmdSPPT": 0,
              "CPU_AmdFPPT": 0,
              "CPU_PL1": 45,
              "CPU_PL2": 65
            }
            """);

        Assert.False(hardware.AmdPowerStatusSeen);
        Assert.False(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
        Assert.Equal(-1, hardware.CpuAmdSpl);
        Assert.Equal(-1, hardware.CpuAmdSppt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"NOT_SUPPORT\"")]
    [InlineData("-1")]
    [InlineData("0")]
    public void UnusableAmdFieldValuesAreNotPlatformEvidence(string raw)
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", $"{{\"CPU_AmdSPL\":{raw},\"CPU_PL1\":45}}");

        Assert.False(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
    }

    /// <summary>
    /// 真正的 AMD 机型：一旦上报可用瓦数，平台判定 latch，Pl1/Pl2 由 AMD 字段接管。
    /// </summary>
    [Fact]
    public void UsableAmdFieldValuesTakeOverThePowerReadback()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", """
            { "CPU_AmdSPL": 90, "CPU_AmdSPPT": 100, "CPU_AmdFPPT": 110, "CPU_PL1": 45, "CPU_PL2": 65 }
            """);

        Assert.True(hardware.AmdPowerStatusSeen);
        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
        Assert.Equal(110, hardware.CpuAmdFppt);
    }

    /// <summary>
    /// 部分帧不该擦除已知的 AMD 读回值——这是 OptionalInt 用上一次值兜底的正当理由。
    /// </summary>
    [Fact]
    public void PartialFrameKeepsThePreviouslyReportedAmdValues()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();
        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":90,\"CPU_AmdSPPT\":100}");

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        Assert.Equal(90, hardware.CpuAmdSpl);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
    }

    /// <summary>
    /// 已经 latch 成 AMD 之后又收到一个占位 0，不能把显示值打回 0 W。
    /// </summary>
    [Fact]
    public void LatchedAmdPlatformIgnoresALaterPlaceholderZero()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();
        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":90,\"CPU_AmdSPPT\":100}");

        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":0,\"CPU_AmdSPPT\":0}");

        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
    }

    /// <summary>
    /// ItemSupport 画像声明是 AMD 平台时，即使运行时还没报可用值也走 AMD 通道；
    /// 此时 Pl1 应回落到通用字段而不是显示 0。
    /// </summary>
    [Fact]
    public void AmdProfileWithoutRuntimeValuesStillUsesTheAmdChannelButReadsGenericWatts()
    {
        using MechrevoHw hardware = NewAmdProfileHardware();

        hardware.HandleMessage("Fan/Status", "{\"CPU_PL1\":45,\"CPU_PL2\":65}");

        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
    }

    /// <summary>
    /// 写入通道的键名选择必须跟随平台判定，这是误判之后真正出故障的地方。
    /// </summary>
    [Fact]
    public async Task PowerLimitWriteUsesTheIntelKeysWhenOnlyPlaceholderAmdFieldsWereSeen()
    {
        var written = new List<Dictionary<string, object>>();
        using var hardware = new MechrevoHw(
            (_, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                    written.Add(new Dictionary<string, object>(values));
                return Task.CompletedTask;
            },
            new MechrevoDeviceCapabilities { AmdPlatform = false });

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_AmdSPL": 0,
              "CPU_PL1": 45,
              "CPU_PL2": 65,
              "CPU_PL1Minimum": 20,
              "CPU_PL1Maximum": 80,
              "CPU_PL2Minimum": 20,
              "CPU_PL2Maximum": 120
            }
            """);

        await hardware.SetPl1Pl2(50, 70);

        Assert.Contains(written, command => command.ContainsKey("PL1"));
        Assert.Contains(written, command => command.ContainsKey("PL2"));
        Assert.DoesNotContain(written, command => command.ContainsKey("CpuAmdSPL"));
        Assert.DoesNotContain(written, command => command.ContainsKey("CpuAmdSPPT"));
    }
}
