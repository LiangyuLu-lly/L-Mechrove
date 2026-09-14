using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 显示色彩模式 / 显示特性 / NVIDIA 全局首选显卡的只读解析。
///
/// 这一族是「协议里有、官方 5.56 界面已经不用」的遗留能力：
/// DISPLAY_STANDARD_MODE、DISPLAY_*_MODE_VALUE_SAVE、NV_CTRL_PANEL_* 在 CCUWinUI 里
/// 只出现在动作枚举的声明处，没有任何发送点；那批颜色字段也没有任何读取点。
/// 所以这里只锁解析，不做下发——理由见 docs/hardware/README.md。
/// </summary>
public class DisplayColorStatusTests
{
    static MechrevoHw NewHardware() => new();

    [Fact]
    public void DisplayModeAndFeatureAndNvPreferenceAreParsed()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {
              "DisplayMode": "DISPLAY_STANDARD_MODE",
              "DisplayFeatureStatus": "DISPLAY_FEATURE_STATUS_ON",
              "DGpu": "NV_CTRL_PANEL_AUTOSELECT"
            }
            """);

        Assert.Equal("DISPLAY_STANDARD_MODE", hardware.DisplayColorMode);
        Assert.True(hardware.DisplayFeatureOn);
        Assert.Equal("NV_CTRL_PANEL_AUTOSELECT", hardware.NvControlPanelPreference);
    }

    /// <summary>状态值是字符串常量，关闭态靠含 OFF 判定（与其他开关同一套约定）。</summary>
    [Theory]
    [InlineData("DISPLAY_FEATURE_STATUS_ON", true)]
    [InlineData("DISPLAY_FEATURE_STATUS_OFF", false)]
    public void DisplayFeatureOffIsDetectedByTheOffSuffix(string status, bool expected)
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", $"{{\"DisplayFeatureStatus\":\"{status}\"}}");

        Assert.Equal(expected, hardware.DisplayFeatureOn);
    }

    /// <summary>
    /// 十七个色彩参数全部要能读到。
    /// 每种模式的参数集不一样：护眼只有亮度/蓝光/色温，影音只有亮度/色温，
    /// 游戏与自定义才有完整六项——这是官方的分法，不是遗漏。
    /// </summary>
    [Fact]
    public void AllSeventeenColourParametersAreParsed()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {
              "GamingBrightness": 80, "GamingRed": 128, "GamingGreen": 130, "GamingBlue": 126,
              "GamingColorTemp": 6500, "GamingContrast": 55,
              "VideoBrightness": 70, "VideoColorTemp": 5500,
              "ReadBrightness": 60, "ReadBlue": 90, "ReadColorTemp": 3200,
              "CutomizedBrightness": 75, "CutomizedRed": 120, "CutomizedGreen": 128,
              "CutomizedBlue": 140, "CutomizedColorTemp": 4200, "CutomizedContrast": 50
            }
            """);

        Assert.Equal(MechrevoHw.DisplayColorParameterFields.Length, hardware.DisplayColorParameters.Count);
        Assert.Equal(80, hardware.DisplayColorParameters["GamingBrightness"]);
        Assert.Equal(6500, hardware.DisplayColorParameters["GamingColorTemp"]);
        Assert.Equal(3200, hardware.DisplayColorParameters["ReadColorTemp"]);
        Assert.Equal(4200, hardware.DisplayColorParameters["CutomizedColorTemp"]);
    }

    /// <summary>
    /// 官方状态字段是 Cutomized（少一个 s），命令名却是 Customized_*。
    /// 照命令名去读状态会一个都读不到，所以这个拼写必须按官方原样保留。
    /// </summary>
    [Fact]
    public void TheOfficialMisspellingOfCustomizedIsPreservedForStatusFields()
    {
        Assert.Contains("CutomizedBrightness", MechrevoHw.DisplayColorParameterFields);
        Assert.DoesNotContain("CustomizedBrightness", MechrevoHw.DisplayColorParameterFields);

        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", "{\"CutomizedContrast\":42}");

        Assert.Equal(42, hardware.DisplayColorParameters["CutomizedContrast"]);
    }

    /// <summary>没上报的参数不能凭默认值凑出来——那会显示一个设备没说过的数。</summary>
    [Fact]
    public void UnreportedParametersAreAbsentRatherThanDefaulted()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", "{\"GamingBrightness\":80}");

        Assert.Single(hardware.DisplayColorParameters);
        Assert.False(hardware.DisplayColorParameters.ContainsKey("GamingContrast"));
        Assert.Equal("", hardware.DisplayColorMode);
        Assert.Null(hardware.DisplayFeatureOn);
        Assert.Equal("", hardware.NvControlPanelPreference);
    }

    /// <summary>不带这些字段的后续状态帧不能把已知值清掉。</summary>
    [Fact]
    public void LaterFramesWithoutTheseFieldsKeepTheKnownValues()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {"DisplayMode":"DISPLAY_GAMING_MODE","DGpu":"NV_CTRL_PANEL_HIGHPERFORMANCE","GamingBrightness":80}
            """);
        hardware.HandleMessage("Setting/Status", "{\"WinKey\":\"WINKEY_STATUS_UNLOCK\"}");

        Assert.Equal("DISPLAY_GAMING_MODE", hardware.DisplayColorMode);
        Assert.Equal("NV_CTRL_PANEL_HIGHPERFORMANCE", hardware.NvControlPanelPreference);
        Assert.Equal(80, hardware.DisplayColorParameters["GamingBrightness"]);
    }

    /// <summary>日志去重键要在册，否则每帧状态都会把这一整行重新打一遍。</summary>
    [Fact]
    public void DisplayColorHasAStatusLogKey() =>
        Assert.Contains("display-color", MechrevoHw.StatusLogKeys);
}
