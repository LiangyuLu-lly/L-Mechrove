using MechrevoLite.Hardware;
using System.Reflection;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯带（主灯带与 Logo 子灯带）的能力判定与状态隔离。
///
/// 历史背景：铰链灯带与同步灯带两个功能已于 2026-09-11 按用户决定整体移除
/// （订阅、解析、能力位、快捷开关、灯效入口、恢复通道、验证项全链路）。
/// 本类保留仍然有效的两通道判据测试，并按 beta12 模式用反射断言已移除的
/// 符号确实不存在——否则任何一处以旧形状复活都会重新长出假入口。
/// </summary>
public class LightbarChannelTests
{
    // 显式空白画像：无参构造读的是本机注册表（开发机 LogoLight=true），
    // 会把「运行时证据是唯一判据」的断言全部打歪。能力位全零才能隔离测试。
    static MechrevoHw NewHardware() => new(null, new MechrevoDeviceCapabilities());

    /// <summary>
    /// 两条通道的电源状态必须互相独立。共用一个键会让开一条灯带把另一条的
    /// 界面勾选一起改掉。
    /// </summary>
    [Fact]
    public void BothLightbarChannelsTrackPowerIndependently()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("HidLightbar/Status", "{\"powerStatus\":\"On\"}");
        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"powerStatus\":\"Off\"}");

        Assert.True(hardware.QuickSwitches["lightbar"]);
        Assert.False(hardware.QuickSwitches["logolight"]);
    }

    /// <summary>状态主题到过即视为这条子灯带存在。</summary>
    [Fact]
    public void LogoLightSupportIsEstablishedByItsOwnStatusTopic()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"powerStatus\":\"On\",\"type\":\"MEZone_Lighbar4\"}");

        Assert.True(hardware.LogoLightStatusSeen);
        Assert.True(hardware.SupportsLogoLight);
        Assert.True(hardware.SupportsQuickSwitch("logolight"));
    }

    /// <summary>
    /// 主灯带明确报了不支持某能力时，不能凭"有灯条"就把入口点亮。
    /// </summary>
    [Fact]
    public void LogoStaysUnsupportedWhenTheMainLightbarSaysSo()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("HidLightbar/Status", """
            {"type":"MEZone_Lighbar4","powerStatus":"On","LogoSupport":false}
            """);

        Assert.True(hardware.SupportsLightbar);
        Assert.False(hardware.SupportsLogoLight);
        Assert.False(hardware.SupportsQuickSwitch("logolight"));
    }

    /// <summary>
    /// 子灯带的能力位只在主灯带状态里带。子灯带自己的状态里没有这些字段，
    /// 解析它们时不能把已知的支持位覆盖成 null。
    /// </summary>
    [Fact]
    public void SubLightbarStatusDoesNotClearCapabilityFlagsFromTheMainStatus()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("HidLightbar/Status", "{\"LogoSupport\":true,\"BaseSupport\":true}");
        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"powerStatus\":\"On\"}");
        hardware.HandleMessage("HidLightbar/Status", "{\"powerStatus\":\"On\"}");

        Assert.True(hardware.LightbarLogoSupport);
        Assert.True(hardware.LightbarBaseSupport);
    }

    /// <summary>
    /// 没有任何灯带证据时入口全部关闭——机型不该凭空多出灯效入口。
    /// </summary>
    [Fact]
    public void NoLightbarEvidenceMeansNoLightbarSupport()
    {
        using MechrevoHw hardware = NewHardware();

        Assert.False(hardware.SupportsLightbar);
        Assert.False(hardware.SupportsLogoLight);
        Assert.Null(hardware.LightbarLogoSupport);
        Assert.Null(hardware.LightbarBaseSupport);
        Assert.Null(hardware.LightbarNewLogoSupport);
        Assert.Null(hardware.LightbarMbLogoSupport);
    }

    /// <summary>
    /// 日志去重键要覆盖剩余两条通道，否则会绕过 WriteLineIfChanged 刷屏。
    /// </summary>
    [Fact]
    public void StatusLogKeysCoverRemainingLightbarChannels()
    {
        Assert.Contains("lb-status-HidLightbar/Status", MechrevoHw.StatusLogKeys);
        Assert.Contains("lb-status-HidLightbar_Logo/Status", MechrevoHw.StatusLogKeys);
    }
}

/// <summary>
/// 真机运行发现的缺陷回归。
///
/// 在开发机（BIOS_PROJECT_ID=IDY）上实跑时，同步灯带的主题每次都发，但载荷全空：
///   LB HidLightbar_Sync/Status:  type= power= light=
/// 服务端对没有这条灯带的机器照样推一个空状态，所以「主题到过」不构成硬件存在的证据。
/// 铰链/同步通道本身已按用户决定移除，但这条「空载荷不构成存在证据」的纪律
/// 对仍然存在的两条通道同样有效，所以用 Logo 通道的空载荷锁住它。
/// </summary>
public class EmptyLightbarStatusRegressionTests
{
    static MechrevoHw NewHardware() => new(null, new MechrevoDeviceCapabilities());

    /// <summary>空载荷形态（历史实测载荷形状）：一个可识别字段都没有。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"\",\"powerStatus\":\"\",\"brightNess\":\"\"}")]
    [InlineData("{\"type\":null,\"powerStatus\":null}")]
    [InlineData("{\"type\":\"   \"}")]
    public void AnEmptyStatusPayloadIsNotEvidenceThatTheLightbarExists(string payload)
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status", payload);

        Assert.False(hardware.LogoLightStatusSeen);
        Assert.False(hardware.SupportsLogoLight);
        Assert.False(hardware.SupportsQuickSwitch("logolight"));
    }

    /// <summary>同一条判据不能把真实存在的子灯带也误判掉。</summary>
    [Fact]
    public void ARealSubLightbarStatusIsStillRecognised()
    {
        using var hardware = NewHardware();

        // 开发机实测的子灯带载荷。
        hardware.HandleMessage("HidLightbar_Logo/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"brightNess\":3}");

        Assert.True(hardware.LogoLightStatusSeen);
        Assert.True(hardware.SupportsLogoLight);
        Assert.True(hardware.SupportsQuickSwitch("logolight"));
    }

    /// <summary>
    /// 空载荷之后又来了真载荷（例如灯带热插或固件延迟上报），要能转成支持。
    /// </summary>
    [Fact]
    public void AnEmptyStatusFollowedByARealOneEstablishesSupport()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status", "{}");
        Assert.False(hardware.SupportsLogoLight);

        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"Off\"}");
        Assert.True(hardware.SupportsLogoLight);
    }

    /// <summary>
    /// 真载荷之后又来一个空载荷不能把已确认的支持撤销——
    /// 灯带不会因为一帧空状态就消失。
    /// </summary>
    [Fact]
    public void ALaterEmptyStatusDoesNotRevokeEstablishedSupport()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\"}");
        hardware.HandleMessage("HidLightbar_Logo/Status", "{}");

        Assert.True(hardware.SupportsLogoLight);
    }

    /// <summary>
    /// 能力位**存在**不构成证据，因为 GCU 用同一个状态 DTO 发全部灯带主题——
    /// 开发机实测那条不存在的灯带载荷里也带着设备级能力位。
    /// 但能力位取值为 true 是另一回事，那条通路仍然有效。
    /// </summary>
    [Fact]
    public void CapabilityFlagsBeingPresentIsNotEvidenceButBeingTrueStillIs()
    {
        using var hardware = NewHardware();

        // 只有能力位、没有 type / powerStatus：不算这条灯带的状态到过。
        hardware.HandleMessage("HidLightbar/Status", "{\"MBlogoSupport\":true}");
        Assert.False(hardware.LightbarStatusSeen);

        // 但 LogoSupport 取值为 true 依然能确立 Logo 灯带的支持。
        hardware.HandleMessage("HidLightbar/Status", "{\"LogoSupport\":true}");
        Assert.True(hardware.SupportsLogoLight);
    }

    /// <summary>
    /// 历史实测载荷：空的 type / powerStatus / brightNess，
    /// 加上从保存设置回显出来的 effect / light，再加上设备级能力位。
    /// 这三类加起来都不能证明这条灯带存在。
    /// </summary>
    [Fact]
    public void TheRealEmptyPayloadFromTheDevMachineIsNotTreatedAsHardwareEvidence()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status", """
            {
              "type": "", "powerStatus": "", "brightNess": "",
              "effect": "Rainbow", "light": 3,
              "LogoSupport": false, "BaseSupport": false,
              "NewlogoSupport": false, "MBlogoSupport": true
            }
            """);

        Assert.False(hardware.LogoLightStatusSeen);
        Assert.False(hardware.SupportsLogoLight);
        Assert.False(hardware.SupportsQuickSwitch("logolight"));
    }
}

/// <summary>
/// 铰链灯带与同步灯带功能移除的守卫（beta12 模式：反射断言符号不存在）。
///
/// 这两个功能按用户决定全链路移除。任何一处以旧形状复活（哪怕编译通过）都会
/// 在界面上长出背后没有完整链路的入口，所以从四个方向分别锁死：
/// 能力位属性、订阅与日志键、解析行为、主题映射。
/// </summary>
public class HingeSyncRemovalGuardTests
{
    [Theory]
    [InlineData("SupportsHingeLight")]
    [InlineData("SupportsSyncLight")]
    [InlineData("HingeLightStatusSeen")]
    [InlineData("SyncLightStatusSeen")]
    [InlineData("LightbarHingeSupport")]
    public void RemovedCapabilitySymbolsDoNotExist(string propertyName)
    {
        var property = typeof(MechrevoHw).GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is null, $"MechrevoHw.{propertyName} 已随铰链/同步灯带功能移除，不应再存在。");
    }

    [Fact]
    public void HingeAndSyncTopicsAreNoLongerSubscribedOrLogged()
    {
        Assert.DoesNotContain(MechrevoHw.SubscribedTopicFilters,
            f => f.Contains("Hinge", StringComparison.OrdinalIgnoreCase) || f.Contains("Sync", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(MechrevoHw.StatusLogKeys,
            k => k.Contains("Hinge", StringComparison.OrdinalIgnoreCase) || k.Contains("Sync", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HingeAndSyncTopicsNoLongerParseIntoSupport()
    {
        using MechrevoHw hardware = new(null, new MechrevoDeviceCapabilities());

        // 真实固件仍可能推这两条主题；收到时必须无害（不解析、不建立任何能力）。
        hardware.HandleMessage("HidLightbar_Hinge/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"brightNess\":3}");
        hardware.HandleMessage("HidLightbar_Sync/Status",
            "{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"On\",\"brightNess\":3}");

        Assert.False(hardware.SupportsQuickSwitch("hingelight"));
        Assert.False(hardware.SupportsQuickSwitch("synclight"));
        Assert.False(hardware.QuickSwitches.ContainsKey("hingelight"));
        Assert.False(hardware.QuickSwitches.ContainsKey("synclight"));
    }

    [Fact]
    public void HingeAndSyncTopicsMapToNothing()
    {
        Assert.Null(MechrevoService.LightTopicToQuickSwitchKey("HidLightbar_Hinge/Ctrl"));
        Assert.Null(MechrevoService.LightTopicToQuickSwitchKey("HidLightbar_Sync/Ctrl"));
    }
}
