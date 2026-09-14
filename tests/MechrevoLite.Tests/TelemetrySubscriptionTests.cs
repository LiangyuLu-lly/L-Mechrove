using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 本轮补齐的订阅：电池健康、网络吞吐、机型/固件铭牌、风扇异常告警。
///
/// 这些字段官方一直在推，但此前一个都没解析。全部按「服务端报过才算有」判定。
///
/// 第三颗风扇不在这个清单里，理由见下面 MidFan 那一组测试——
/// 这套协议里根本没有第三颗风扇的读数。
/// </summary>
public class TelemetrySubscriptionTests
{
    static MechrevoHw NewHardware() =>
        new(null, MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));

    // ---------- 第三颗风扇：确认它在协议里不存在 ----------

    /// <summary>
    /// 两颗风扇的读数要正常解析。这是 System/FanInfo 的全部内容。
    /// </summary>
    [Fact]
    public void FanInfoParsesBothReportedFans()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanInfo",
            """{"CpuFanDuty":25,"GpuFanDuty":25,"CpuFanRpm":1509,"GpuFanRpm":1746}""");

        Assert.Equal(25, hardware.CpuFanDuty);
        Assert.Equal(25, hardware.GpuFanDuty);
        Assert.Equal(1509, hardware.CpuFanRpm);
        Assert.Equal(1746, hardware.GpuFanRpm);
    }

    /// <summary>
    /// 第三颗风扇恒判为「没有」，任何载荷都不能把它翻成有。
    ///
    /// 这条锁的是一个曾经真的犯过的错：当时在 System/FanInfo 里加了一段
    /// 「第三颗风扇解析」，字段名是自己编的（MidFanDuty / RamFanDuty / ThirdFanDuty /
    /// Fan3Duty / MiddleFanDuty），一个都不存在于协议里，于是那条显示路径依然是死的，
    /// 却因为「代码看起来接上了」而被当成修好了。
    ///
    /// 事实依据：官方 System/FanInfo 的处理只读 CpuFanDuty / GpuFanDuty / CpuFanRpm /
    /// GpuFanRpm 四个字段；官方 58 个 MQTT 主题里与风扇有关的只有 System/FanInfo 与
    /// System/FanErrorInfo；官方界面全机型共用且只显示两颗。
    /// 内存风扇（RamFan1p5Support）在 EC 侧只有三个风扇表寄存器、没有转速寄存器，
    /// 且由 GCUService 内部随风扇表自动管理。
    ///
    /// 所以「中置风扇」是 g-helper 的华硕概念，在这套协议上没有对应物。
    /// 想恢复这块显示，前提是先拿到真实存在的字段名或 EC 寄存器——不是再编一批名字。
    /// </summary>
    [Theory]
    [InlineData("""{"CpuFanDuty":25,"GpuFanDuty":25,"CpuFanRpm":1509,"GpuFanRpm":1746}""")]
    [InlineData("""{"MidFanDuty":42,"MidFanRpm":1800}""")]
    [InlineData("""{"RamFanDuty":42,"RamFanRpm":1800}""")]
    [InlineData("""{"ThirdFanDuty":42,"Fan3Rpm":1800,"MiddleFanDuty":42}""")]
    public void ThirdFanIsNeverReportedByThisProtocol(string payload)
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("System/FanInfo", payload);

        Assert.False(hardware.MidFanSeen);
    }

    // ---------- 电池健康 ----------

    [Fact]
    public void BatteryHealth_ParsesCycleCountCapacityAndAbnormalFlag()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/BatteryInfo", """
            {"BatteryLifePercent":"96","BatteryLifeRemaining":"-1",
             "BatteryAbnormal":"0","BatteryCapacity":"0 mWh","BatteryCycleCount":"15"}
            """);

        Assert.Equal(96, hardware.BatteryPercent);
        Assert.Equal(15, hardware.BatteryCycleCount);
        Assert.Equal("0 mWh", hardware.BatteryCapacityText);
        Assert.False(hardware.BatteryAbnormal);
    }

    [Fact]
    public void BatteryHealth_FlagsAnAbnormalBattery()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/BatteryInfo", """{"BatteryLifePercent":"50","BatteryAbnormal":"1"}""");
        Assert.True(hardware.BatteryAbnormal);
    }

    [Fact]
    public void BatteryCycleCount_StaysUnknownWhenNotReported()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/BatteryInfo", """{"BatteryLifePercent":"80"}""");
        Assert.Equal(-1, hardware.BatteryCycleCount);
    }

    // ---------- 网络吞吐 ----------

    [Fact]
    public void NetworkThroughput_IsParsedVerbatimWithUnits()
    {
        // 服务端给的是带单位的字符串，原样保留：自己解析再格式化只会引入单位换算错误。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/NetworkInfo",
            """{"NetworkDownload":"232 Kbps","NetworkUpload":"3.9 Mbps"}""");

        Assert.True(hardware.NetworkInfoSeen);
        Assert.Equal("232 Kbps", hardware.NetworkDownload);
        Assert.Equal("3.9 Mbps", hardware.NetworkUpload);
    }

    [Fact]
    public void NetworkThroughput_IsAbsentUntilReported()
    {
        using MechrevoHw hardware = NewHardware();
        Assert.False(hardware.NetworkInfoSeen);
        Assert.Equal("", hardware.NetworkDownload);
    }

    // ---------- 机型 / 固件铭牌 ----------

    [Theory]
    [InlineData("ECVersion")]
    [InlineData("EcVersion")]
    [InlineData("EC_Version")]
    [InlineData("ECFWVersion")]
    public void EcFirmwareVersion_AcceptsEveryKnownFieldSpelling(string field)
    {
        // EC 固件版本是排查固件差异类问题的关键信息，此前只在官方 UI 里可见。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/HardwareInfo", $"{{\"{field}\":\"2.6.0\"}}");

        Assert.True(hardware.HardwareInfoSeen);
        Assert.Equal("2.6.0", hardware.EcFirmwareVersion);
    }

    [Fact]
    public void EcFirmwareVersion_IsNotOverwrittenByAnEmptyReport()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/HardwareInfo", """{"ECVersion":"2.6.0"}""");
        hardware.HandleMessage("System/HardwareInfo", """{"ECVersion":""}""");
        Assert.Equal("2.6.0", hardware.EcFirmwareVersion);
    }

    // ---------- 风扇异常告警 ----------

    [Fact]
    public void FanError_IsDetectedFromAnyErrorField()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":1,"GpuFanError":0}""");

        Assert.True(hardware.FanErrorSeen);
        Assert.True(hardware.FanError);
    }

    [Fact]
    public void FanError_ClearsWhenAllFieldsReportHealthy()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":1}""");
        Assert.True(hardware.FanError);

        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":0,"GpuFanError":0}""");
        Assert.False(hardware.FanError);
    }

    [Fact]
    public void FanError_IgnoresUnrelatedFields()
    {
        // 只看名字里带 Error/Abnormal 的字段，不能因为报文里有别的非零值就报故障。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanRpm":2000,"FanCount":2}""");
        Assert.True(hardware.FanErrorSeen);
        Assert.False(hardware.FanError);
    }

    [Fact]
    public void FanError_DetectsAbnormalNamedFields()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanErrorInfo", """{"FanAbnormal":true}""");
        Assert.True(hardware.FanError);
    }

    // ---------- 新增解析不能污染既有状态 ----------

    [Fact]
    public void NewTopicsDoNotDisturbExistingTelemetry()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("System/FanInfo", """{"CpuFanDuty":30,"CpuFanRpm":2000,"GpuFanRpm":1500}""");
        hardware.HandleMessage("System/NetworkInfo", """{"NetworkDownload":"1 Mbps"}""");
        hardware.HandleMessage("System/HardwareInfo", """{"ECVersion":"2.6.0"}""");
        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":0}""");

        Assert.Equal(30, hardware.CpuFanDuty);
        Assert.Equal(2000, hardware.CpuFanRpm);
        Assert.Equal(1500, hardware.GpuFanRpm);
        Assert.False(hardware.MidFanSeen);
    }
}
