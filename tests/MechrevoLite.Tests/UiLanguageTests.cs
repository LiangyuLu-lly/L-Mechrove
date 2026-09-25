using System.Windows.Forms;

namespace MechrevoLite.Tests;

public class UiLanguageTests
{
    [Theory]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("zh", "en-US", "zh-CN")]
    [InlineData("", "zh-CN", "zh-CN")]
    [InlineData(null, "en-US", "en")]
    [InlineData("de", "de-DE", "en")]
    public void Normalize_MapsStoredOrOsCulture(string? stored, string os, string expected)
    {
        Assert.Equal(expected, UiLanguage.Normalize(stored, os));
    }

    [Fact]
    public void IndexAndCode_AreInversesForTheTwoModes()
    {
        Assert.Equal(0, UiLanguage.IndexOf(UiLanguage.Chinese));
        Assert.Equal(1, UiLanguage.IndexOf(UiLanguage.English));
        Assert.Equal(UiLanguage.Chinese, UiLanguage.CodeFromIndex(0));
        Assert.Equal(UiLanguage.English, UiLanguage.CodeFromIndex(1));
    }
}

public class SettingsDialogLanguageTests
{
    [Fact]
    public void LanguageCombo_IsPresentUnderTheInterfaceGroup()
    {
        using var dialog = new SettingsDialog(new Panel(), null, displayGroupAvailable: false);
        ComboBox combo = dialog.Controls.Find("comboLanguage", true).OfType<ComboBox>().Single();
        Assert.Equal(2, combo.Items.Count);
        Assert.Equal("中文", combo.Items[0]);
        Assert.Equal("English", combo.Items[1]);
        Assert.NotEmpty(dialog.Controls.Find("labelLanguage", true));
    }

    [Fact]
    public void LanguageCombo_WritesAppConfigWithoutRestarting()
    {
        AppConfig.Set(UiLanguage.ConfigKey, UiLanguage.Chinese);
        string? captured = null;
        SettingsDialog.LanguageChangedOverride = code => captured = code;
        try
        {
            using var dialog = new SettingsDialog(new Panel(), null, displayGroupAvailable: false);
            ComboBox combo = dialog.Controls.Find("comboLanguage", true).OfType<ComboBox>().Single();
            Assert.Equal(0, combo.SelectedIndex);

            combo.SelectedIndex = 1;

            Assert.Equal(UiLanguage.English, captured);
            Assert.Equal(UiLanguage.English, AppConfig.GetString(UiLanguage.ConfigKey));
        }
        finally
        {
            SettingsDialog.LanguageChangedOverride = null;
            AppConfig.Remove(UiLanguage.ConfigKey);
        }
    }
}
