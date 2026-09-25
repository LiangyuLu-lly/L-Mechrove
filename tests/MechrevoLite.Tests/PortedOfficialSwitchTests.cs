using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 本轮补齐的官方开关：触摸板切换键、单色键盘背光、Uni/Omni、电源指示灯、
/// 游戏白名单、CPU 高级性能（超频菜单总闸）、GPU Whisper 模式。
///
/// 每一项都覆盖两条路径：服务端报过该字段（可用）与从未报过（这台机器没有）。
/// 本项目要覆盖全系机型，所以「不支持」这条路径和「支持」一样重要——
/// 误报会让用户点到本机不存在的入口。
/// </summary>
public class PortedOfficialSwitchTests
{
    static MechrevoDeviceCapabilities Profile(params (string Key, object Value)[] values)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object value) in values) map[key] = value;
        return MechrevoDeviceCapabilities.FromValues(map);
    }

    static MechrevoHw NewHardware(params (string Key, object Value)[] capabilities) =>
        new(null, Profile(capabilities));

    // ---------- 未上报即视为该机型没有这项硬件 ----------

    [Theory]
    [InlineData("touchpadtoggle")]
    [InlineData("singlecolorkb")]
    [InlineData("uni")]
    [InlineData("omni")]
    [InlineData("powerlight")]
    [InlineData("gamewhitelist")]
    [InlineData("cpuadvperf")]
    [InlineData("whisper")]
    public void SwitchIsUnsupportedUntilTheServiceReportsIt(string key)
    {
        using MechrevoHw hardware = NewHardware();
        Assert.False(hardware.SupportsQuickSwitch(key));
    }

    // ---------- Setting/Status 上报后可用 ----------

    [Fact]
    public void TouchpadToggle_BecomesAvailableAndParsesState()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"TouchpadToggle":"TOUCHPAD_TOGGLE_ON"}""");

        Assert.True(hardware.TouchpadToggleSeen);
        Assert.True(hardware.SupportsQuickSwitch("touchpadtoggle"));
        Assert.True(hardware.QuickSwitches["touchpadtoggle"]);

        hardware.HandleMessage("Setting/Status", """{"TouchpadToggle":"TOUCHPAD_TOGGLE_OFF"}""");
        Assert.False(hardware.QuickSwitches["touchpadtoggle"]);
    }

    [Fact]
    public void TouchpadToggle_IsDistinctFromThePlainTouchpadSwitch()
    {
        // 触摸板开关走 Settings/DeviceSwitchItemStatus 的 TochpadEnable，
        // 切换键走 Setting/Status 的 TouchpadToggle。报了一个不能让另一个也可用。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"TouchpadToggle":"TOUCHPAD_TOGGLE_ON"}""");

        Assert.True(hardware.SupportsQuickSwitch("touchpadtoggle"));
        Assert.False(hardware.SupportsQuickSwitch("touchpad"));
        Assert.False(hardware.TouchpadSeen);
        Assert.False(hardware.QuickSwitches.ContainsKey("touchpad"));
    }

    [Fact]
    public void SingleColorKeyboardBacklight_BecomesAvailableAndParsesState()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON"}""");

        Assert.True(hardware.SupportsQuickSwitch("singlecolorkb"));
        Assert.True(hardware.QuickSwitches["singlecolorkb"]);

        hardware.HandleMessage("Setting/Status", """{"SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_OFF"}""");
        Assert.False(hardware.QuickSwitches["singlecolorkb"]);
    }

    [Fact]
    public void UniOmni_BothBecomeAvailableWhenEitherIsReported()
    {
        // 官方把这两个做成一组互斥单选，只报一个也说明这组开关存在。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"UniSwitch":"Uni_OFF"}""");

        Assert.True(hardware.SupportsQuickSwitch("uni"));
        Assert.True(hardware.SupportsQuickSwitch("omni"));
        Assert.False(hardware.QuickSwitches["uni"]);
    }

    [Fact]
    public void UniOmni_ParsesBothStatesIndependently()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"UniSwitch":"Uni_ON","OmniSwitch":"Omni_OFF"}""");

        Assert.True(hardware.QuickSwitches["uni"]);
        Assert.False(hardware.QuickSwitches["omni"]);
    }

    [Fact]
    public void UniOmni_ArePairedAsMutuallyExclusive()
    {
        Assert.Equal("omni", MechrevoService.MutuallyExclusivePartner("uni"));
        Assert.Equal("uni", MechrevoService.MutuallyExclusivePartner("omni"));
        Assert.Null(MechrevoService.MutuallyExclusivePartner("wifi"));
        Assert.Null(MechrevoService.MutuallyExclusivePartner("powerlight"));
    }

    [Fact]
    public void PowerLight_ExposesSwitchAndBrightness()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"PowerLightSwitch":1,"PowerLightBrightness":80}""");

        Assert.True(hardware.SupportsQuickSwitch("powerlight"));
        Assert.True(hardware.QuickSwitches["powerlight"]);
        Assert.Equal(80, hardware.PowerLightBrightness);
        Assert.True(hardware.SupportsPowerLightBrightness);
    }

    [Fact]
    public void PowerLightBrightness_IsClampedToTheOfficialRange()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"PowerLightSwitch":1,"PowerLightBrightness":250}""");
        Assert.Equal(100, hardware.PowerLightBrightness);
    }

    [Fact]
    public void PowerLightBrightness_IsUnavailableWithoutTheSwitchField()
    {
        // 只报亮度不报开关时不能认为可控：官方载荷把两者放在同一条命令里，
        // 缺开关态会导致调亮度顺带把灯关掉。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"PowerLightBrightness":60}""");
        Assert.False(hardware.SupportsPowerLightBrightness);
    }

    // ---------- Fan/Status 上报的项 ----------

    [Fact]
    public void GameWhitelist_BecomesAvailableAndParsesState()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", """{"GameWhitelistSwitch":1}""");

        Assert.True(hardware.SupportsQuickSwitch("gamewhitelist"));
        Assert.True(hardware.QuickSwitches["gamewhitelist"]);

        hardware.HandleMessage("Fan/Status", """{"GameWhitelistSwitch":0}""");
        Assert.False(hardware.QuickSwitches["gamewhitelist"]);
    }

    [Fact]
    public void CpuAdvancedPerformance_RequiresBothTheCapabilityBitAndTheRuntimeField()
    {
        // 静态能力位为假说明这台机器的 BIOS 没开这一档，光有运行时字段不能暴露入口。
        using MechrevoHw withoutCapability = NewHardware(("CPUPerformanceAndOverClockMenuSupport", 0));
        withoutCapability.HandleMessage("Fan/Status", """{"CPUPerformanceAndOverClockMenuSwitch":1}""");
        Assert.False(withoutCapability.SupportsQuickSwitch("cpuadvperf"));

        using MechrevoHw withCapability = NewHardware(("CPUPerformanceAndOverClockMenuSupport", 1));
        Assert.False(withCapability.SupportsQuickSwitch("cpuadvperf"));   // 还没有运行时字段
        withCapability.HandleMessage("Fan/Status", """{"CPUPerformanceAndOverClockMenuSwitch":1}""");
        Assert.True(withCapability.SupportsQuickSwitch("cpuadvperf"));
        Assert.True(withCapability.QuickSwitches["cpuadvperf"]);
    }

    [Fact]
    public void WhisperMode_RequiresAnExplicitSupportFlag()
    {
        // 关键区别：官方对不支持的机型也会带上 GPU_WhisperMode* 字段，
        // 所以「字段存在」不能当成支持证据，必须看 GPU_WhisperModeSupport。
        using MechrevoHw switchOnly = NewHardware();
        switchOnly.HandleMessage("Fan/Status", """{"GPU_WhisperModeSwitch":0}""");
        Assert.True(switchOnly.WhisperModeSeen);
        Assert.False(switchOnly.SupportsWhisperMode);

        using MechrevoHw supported = NewHardware();
        supported.HandleMessage("Fan/Status", """{"GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":1}""");
        Assert.True(supported.SupportsWhisperMode);
        Assert.True(supported.WhisperMode);
    }

    [Fact]
    public void WhisperMode_ExplicitFalseSupportStaysUnsupported()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", """{"GPU_WhisperModeSupport":false,"GPU_WhisperModeSwitch":1}""");
        Assert.False(hardware.SupportsWhisperMode);
    }

    [Fact]
    public void WhisperMode_SwitchAndLevelAreDifferentFields()
    {
        // 防退化：GPU_WhisperModeSwitch 是开关、GPU_WhisperModeSetting 是静音档位
        // （对应三档 MinFps）。曾经把 Setting 当开关读，于是「关掉静音」会被回读成开启。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", """
            {"GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":"0","GPU_WhisperModeSetting":"2"}
            """);

        Assert.False(hardware.WhisperMode);
        Assert.Equal(2, hardware.WhisperModeLevel);
    }

    [Fact]
    public void WhisperMode_IsNeverExposedAsAWritableQuickSwitch()
    {
        // 防退化：官方 5.56 对 GpuWhisperModeSwitch / GpuWhisperModeSetting
        // 只有属性声明、零下发点，命令形状无从取证；按枚举名猜出来的实现真机验证
        // 下发后回读毫无变化。除非拿到真实的下发点，这一族必须保持只读。
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", """
            {"GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":1,"GPU_WhisperModeSetting":1}
            """);

        Assert.True(hardware.SupportsWhisperMode);          // 状态读得到
        Assert.False(hardware.SupportsQuickSwitch("whisper"));
        Assert.False(hardware.QuickSwitches.ContainsKey("whisper"));
    }

    // ---------- 不能因为新增字段而误判既有开关 ----------

    [Fact]
    public void NewFieldsDoNotLeakIntoUnrelatedSwitches()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """
            {"TouchpadToggle":"TOUCHPAD_TOGGLE_ON","SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON",
             "UniSwitch":"Uni_OFF","PowerLightSwitch":1,"PowerLightBrightness":50}
            """);

        foreach (string unrelated in new[] { "wifi", "bt", "webcam", "numpad", "copilot" })
            Assert.False(hardware.SupportsQuickSwitch(unrelated), $"{unrelated} 不应因为其他字段而变成可用。");
    }

    [Fact]
    public void UnknownSwitchKeyIsNeverSupported()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", """{"TouchpadToggle":"TOUCHPAD_TOGGLE_ON"}""");
        Assert.False(hardware.SupportsQuickSwitch("no-such-switch"));
        Assert.False(hardware.SupportsQuickSwitch(""));
    }
}

/// <summary>
/// 界面的开关回显是按 CheckBox.Tag 统一从 <c>QuickSwitches</c> 字典读的
/// （Settings.UpdateQuickSwitches）。任何新增开关如果只留独立属性、不进这个字典，
/// 勾选状态就永远不会刷新——这一组测试锁住「协议层解析」和「界面回显」之间的这个契约。
/// </summary>
public class QuickSwitchReadbackContractTests
{
    static MechrevoHw NewHardware() =>
        new(null, MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CPUPerformanceAndOverClockMenuSupport"] = 1,
            }));

    [Fact]
    public void EverySupportedSwitchAlsoPublishesItsStateToTheReadbackDictionary()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {"TouchpadToggle":"TOUCHPAD_TOGGLE_ON","SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON",
             "UniSwitch":"Uni_ON","OmniSwitch":"Omni_OFF",
             "PowerLightSwitch":1,"PowerLightBrightness":70}
            """);
        hardware.HandleMessage("Fan/Status", """
            {"GameWhitelistSwitch":1,"CPUPerformanceAndOverClockMenuSwitch":1,
             "GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":1}
            """);

        // whisper 不在这张表里：它是只读族，不该出现在可写开关字典里。
        string[] added =
        {
            "touchpadtoggle", "singlecolorkb", "uni", "omni",
            "powerlight", "gamewhitelist", "cpuadvperf",
        };

        foreach (string key in added)
        {
            Assert.True(hardware.SupportsQuickSwitch(key), $"{key} 应当已可用。");
            Assert.True(hardware.QuickSwitches.ContainsKey(key),
                $"{key} 的状态没有写进 QuickSwitches，界面回显会一直不刷新。");
        }
    }

    [Fact]
    public void WhisperModeTracksTheSwitchFieldWithoutJoiningTheWritableDictionary()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", """{"GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":1}""");
        Assert.True(hardware.WhisperMode);

        hardware.HandleMessage("Fan/Status", """{"GPU_WhisperModeSupport":true,"GPU_WhisperModeSwitch":0}""");
        Assert.False(hardware.WhisperMode);

        // 只读族不进 QuickSwitches：进了字典界面就会长出一个点了没反应的勾选框。
        Assert.False(hardware.QuickSwitches.ContainsKey("whisper"));
    }
}
