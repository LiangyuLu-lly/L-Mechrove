using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LightbarSingleColorEffectTests
{
    [Theory]
    [InlineData("Single", true)]
    [InlineData("Breathing", true)]
    [InlineData("Impact", true)]
    [InlineData("Wave", false)]
    [InlineData("Raindrop", false)]
    [InlineData("Mix", true)]
    [InlineData("Rainbow", false)]
    public void EffectUsesSingleColor_matches_firmware_swatch_effects(string effect, bool expected) =>
        Assert.Equal(expected, LightingSettingsStore.EffectUsesSingleColor(effect));

    [Fact]
    public void ColorForEffect_sends_swatch_for_impact_not_null()
    {
        Color cyan = Color.FromArgb(0, 255, 255);
        Assert.Equal(cyan.ToArgb(), LightingSettingsStore.ColorForEffect("Impact", cyan)!.Value.ToArgb());
        Assert.Null(LightingSettingsStore.ColorForEffect("Wave", cyan));
    }

    [Fact]
    public void ColorForEffect_Mix_ReturnsSingleColor()
    {
        Color swatch = Color.FromArgb(0, 255, 255);
        Color? actual = LightingSettingsStore.ColorForEffect("Mix", swatch);
        Assert.NotNull(actual);
        Assert.Equal(swatch.ToArgb(), actual.Value.ToArgb());
    }

    [Fact]
    public void ColorForSpec_WithoutASpecFallsBackToColorForEffect()
    {
        Color cyan = Color.FromArgb(0, 255, 255);
        var impact = new LightChannelSettings("Impact", 3, 1, cyan.ToArgb(), PowerOn: true);
        Assert.Equal(cyan.ToArgb(), LightingSettingsStore.ColorForSpec(null, impact)!.Value.ToArgb());
        Assert.Null(LightingSettingsStore.ColorForSpec(null, impact with { Effect = "Wave" }));
    }

    [Fact]
    public void Lightbar_and_restore_paths_pass_color_for_non_single_swatch_effects()
    {
        string lightForm = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "LightForm.cs");
        string settings = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.V2.cs");
        string program = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Program.cs");
        string service = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoService.cs");
        // 界面与恢复路径按效果规格取色（ColorForSpec）；规格未知时它退回 ColorForEffect（见下方用例）。
        Assert.Contains("ColorForSpec", lightForm, StringComparison.Ordinal);
        Assert.Contains("ColorForSpec", settings, StringComparison.Ordinal);
        Assert.Contains("ColorForSpec", program, StringComparison.Ordinal);
        Assert.Contains("ColorForEffect", service, StringComparison.Ordinal);
        Assert.DoesNotContain("_effect == \"Single\" ? _singleColor", lightForm, StringComparison.Ordinal);
        Assert.DoesNotContain("effectId == \"Single\" ? Color.FromArgb", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.Effect == \"Single\" ? Color.FromArgb", program, StringComparison.Ordinal);
        Assert.DoesNotContain("effect == \"Single\" ? Color.FromArgb", service, StringComparison.Ordinal);
    }
}
