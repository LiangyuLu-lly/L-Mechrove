using MechrevoLite.Hardware;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Tests;

/// <summary>
/// GCU 报文解析层的语义回归。这一层过去有四套互不一致的默认值语义，导致「未知」被伪装成
/// 一个看起来合理的真实档位，以及单个字段的类型差异会静默毁掉整帧状态。
/// </summary>
public class PayloadParsingTests
{
    // ---- 布尔解析：只接受可枚举的已知记法，其余一律未知 ----

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("ON", true)]
    [InlineData("on", true)]
    [InlineData("Enable", true)]
    [InlineData("ENABLED", true)]
    [InlineData("SUPPORT", true)]
    [InlineData("Supported", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("OFF", false)]
    [InlineData("Disabled", false)]
    public void FlexibleBool_AcceptsEveryKnownFirmwareSpelling(string text, bool expected) =>
        Assert.Equal(expected, MechrevoHw.ParseFlexibleBool(text));

    /// <summary>
    /// 关键回归：子串匹配会让 NOT_SUPPORT 命中 SUPPORT 判成真。这里必须是精确匹配。
    /// </summary>
    [Theory]
    [InlineData("NOT_SUPPORT")]
    [InlineData("NOT SUPPORT")]
    [InlineData("NotSupported")]
    [InlineData("UNSUPPORTED")]
    [InlineData("NONSUPPORT")]
    public void FlexibleBool_NegativeSupportSpellingsNeverReadAsSupported(string text) =>
        Assert.False(MechrevoHw.ParseFlexibleBool(text));

    /// <summary>
    /// 同理：含 "ON" 的普通词不能被当成 true，否则枚举型字符串里恰好含 "ON" 的取值会被误判。
    /// </summary>
    [Theory]
    [InlineData("DisconnectMonitor")]
    [InlineData("MONITOR")]
    [InlineData("OPERATING_GAMING_MODE")]
    [InlineData("IGPU_ONLY_CONNECT_RB_AUTO")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FlexibleBool_UnknownTextStaysUnknown(string? text) =>
        Assert.Null(MechrevoHw.ParseFlexibleBool(text));

    // ---- 整数解析：无法解析 == 未知（-1），不再退化成 0 ----

    [Theory]
    [InlineData("62", 62)]
    [InlineData(" 62 ", 62)]
    [InlineData("-25", -25)]
    [InlineData("62.0", 62)]
    [InlineData("61.6", 62)]
    public void OptionalInt_ParsesIntegersAndFirmwareDecimals(string text, int expected) =>
        Assert.Equal(expected, MechrevoHw.ParseOptionalInt(text));

    [Theory]
    [InlineData("OPERATING_GAMING_MODE")]
    [InlineData("HEALTHYMODE")]
    [InlineData("True")]
    [InlineData("")]
    [InlineData(null)]
    public void OptionalInt_UnparsableTextIsUnknown(string? text) =>
        Assert.Null(MechrevoHw.ParseOptionalInt(text));

    [Fact]
    public void Int_MapsBothMissingAndUnparsableToUnknown()
    {
        var payload = JObject.Parse("{\"a\":7,\"b\":\"OPERATING_GAMING_MODE\",\"c\":null}");

        Assert.Equal(7, MechrevoHw.Int(payload, "a"));
        // 旧实现这里返回 0，于是 `>= 0` 的有效性守卫全部被绕过。
        Assert.Equal(-1, MechrevoHw.Int(payload, "b"));
        Assert.Equal(-1, MechrevoHw.Int(payload, "c"));
        Assert.Equal(-1, MechrevoHw.Int(payload, "missing"));
    }

    // ---- 整帧健壮性：单个字段的类型差异不再毁掉整个主题 ----

    /// <summary>
    /// IsAC 过去用 Value&lt;bool&gt;()，是 Fan/Status 自增版本号后的第一个赋值。
    /// 该协议同一帧里大量使用字符串型布尔，一旦 IsAC 也是 "1" 就抛 FormatException，
    /// 导致模式、功耗墙、TCC、TGP、超频回读全部停留在旧值且无任何日志。
    /// </summary>
    [Fact]
    public void FanStatus_StringBooleanIsAcDoesNotDiscardTheRestOfTheFrame()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("Fan/Status", """
            {
              "IsAC": "1",
              "OperatingMode": 1,
              "CPU_PL1": 45,
              "CPU_PL2": 65,
              "TjMax": 100,
              "GPU_ConfigurableTGPTarget": 115
            }
            """);

        Assert.True(hardware.IsAC);
        Assert.Equal(1, hardware.OperatingMode);
        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
        Assert.Equal(115, hardware.GpuTgp);
    }

    /// <summary>
    /// DC_HZ 同理：过去它抛异常会连带毁掉 currentHZList 和 currentHZ，刷新率整块功能静默失效。
    /// </summary>
    [Fact]
    public void GpuDeviceStatus_StringBooleanDcHzDoesNotDiscardTheRefreshRateList()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("GPUDevice/Status", """
            {
              "currentHZList": [60, 165, 165],
              "currentHZ": 165,
              "DC_HZ": "1"
            }
            """);

        Assert.Equal(new[] { 165, 60 }, hardware.HzList);
        Assert.Equal(165, hardware.CurrentHz);
        Assert.True(hardware.DcHzSeen);
        Assert.True(hardware.DcHz);
    }

    /// <summary>
    /// 无法识别的 DC_HZ 不能把 DcHzSeen 置位——否则 UI 会暴露一个永远确认不了的开关。
    /// </summary>
    [Fact]
    public void GpuDeviceStatus_UnrecognisedDcHzDoesNotClaimTheCapability()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("GPUDevice/Status", "{\"currentHZ\":165,\"DC_HZ\":\"UNKNOWN_STATE\"}");

        Assert.False(hardware.DcHzSeen);
        Assert.Equal(165, hardware.CurrentHz);
    }

    /// <summary>
    /// 「外接屏断独显」「电池切独显」两个快捷开关已整条移除。GPUDevice/Status 里
    /// 曾驱动它们的 DisconnectMonitor / DC_Once 字段从此不再置位能力、也不再写
    /// QuickSwitches——否则界面会把已撤的开关重新长出来。
    /// </summary>
    [Fact]
    public void GpuDeviceStatus_SwitchFieldsDoNotReviveRemovedQuickSwitches()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("GPUDevice/Status", """
            {
              "currentHZ": 165,
              "DisconnectMonitor": true,
              "DC_Once": "ON"
            }
            """);

        Assert.False(hardware.SupportsQuickSwitch("exmonitor"));
        Assert.False(hardware.SupportsQuickSwitch("exbattery"));
        Assert.False(hardware.QuickSwitches.ContainsKey("exmonitor"));
        Assert.False(hardware.QuickSwitches.ContainsKey("exbattery"));
        // 同帧的刷新率解析不受字段移除影响。
        Assert.Equal(165, hardware.CurrentHz);
    }

    /// <summary>
    /// FanBoostEnable 过去走 Int()&gt;0：固件发 JSON 布尔时 int 解析失败得到 0，
    /// 于是能力被判成「支持」而状态永远显示「关闭」。
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void FanStatus_FanBoostAcceptsBothJsonBooleanAndNumericSpelling(string raw, bool expected)
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("Fan/Status", $"{{\"FanBoostEnable\":{raw}}}");

        Assert.True(hardware.SupportsFanBoost);
        Assert.Equal(expected, hardware.FanBoost);
    }

    [Fact]
    public void FanStatus_UnrecognisedFanBoostValueDoesNotClaimTheCapability()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("Fan/Status", "{\"FanBoostEnable\":\"WHATEVER\"}");

        Assert.False(hardware.SupportsFanBoost);
        Assert.False(hardware.FanBoost);
    }

    /// <summary>
    /// 字符串枚举形式的 OperatingMode 过去被解析成 0 = 办公档，UI 会高亮一个错误的性能档。
    /// </summary>
    [Fact]
    public void FanStatus_EnumStringOperatingModeIsRejectedInsteadOfBecomingOfficeMode()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");
        Assert.Equal(1, hardware.OperatingMode);

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":\"OPERATING_OFFICE_MODE\"}");

        Assert.Equal(1, hardware.OperatingMode);
    }

    /// <summary>
    /// 字符串状态形式的电池保护档过去被解析成 0 = 满充，用户看到的是错误的档位。
    /// </summary>
    [Fact]
    public void BatteryProtection_EnumStringStatusIsRejectedInsteadOfBecomingFullCharge()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());
        hardware.HandleMessage("System/BatteryProtection", "{\"HealthProtectionStatus\":2}");
        Assert.Equal(2, hardware.BatteryProtection);

        hardware.HandleMessage("System/BatteryProtection", "{\"HealthProtectionStatus\":\"HEALTHYMODE\"}");

        Assert.Equal(2, hardware.BatteryProtection);
    }

    /// <summary>
    /// 曲线点做范围钳制而不是 byte 截断——旧代码里 Duty=300 会变成 44，画出一条错误的曲线。
    /// upT 保留 255 作为「有效点结束」哨兵，所以上界是 255。
    /// </summary>
    [Fact]
    public void FanTable_OutOfRangeCurvePointsAreClampedNotTruncated()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("Fan/Table", """
            {
              "Name": "M1T1",
              "CPU": [
                { "UpT": 40, "Duty": 30 },
                { "UpT": 300, "Duty": 300 },
                { "UpT": -5, "Duty": -5 }
              ]
            }
            """);

        Assert.Equal(40, hardware.CpuCurveUpT[0]);
        Assert.Equal(30, hardware.CpuCurveDuty[0]);
        Assert.Equal(255, hardware.CpuCurveUpT[1]);
        Assert.Equal(100, hardware.CpuCurveDuty[1]);
        Assert.Equal(0, hardware.CpuCurveUpT[2]);
        Assert.Equal(0, hardware.CpuCurveDuty[2]);
    }

    /// <summary>
    /// 非法 JSON 只能被记录，不能让 MQTT 接收线程抛出去。
    /// </summary>
    [Fact]
    public void MalformedPayloadIsContainedInsteadOfPropagating()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

        hardware.HandleMessage("Fan/Status", "not json at all");
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        Assert.Equal(1, hardware.OperatingMode);
    }
}
