namespace MechrevoLite.Tests;

/// <summary>
/// Tray "打开主界面" must show-or-focus. SettingsToggle(false, true) hides when
/// already Visible (checkForFocus=false → HideAll). An "open" action must not hide.
/// </summary>
public class TrayOpenMainWindowTests
{
    [Fact]
    public void OpenMainWindow_WhenVisible_ShowsOrFocuses_DoesNotHide()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        int marker = source.IndexOf("AddAction(Properties.Strings.TrayOpenMain", StringComparison.Ordinal);
        Assert.True(marker >= 0, "tray action TrayOpenMain is missing from Settings.cs");
        Assert.Equal("打开主界面", Properties.Strings.ResourceManager.GetString(
            "TrayOpenMain", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")));

        int lineEnd = source.IndexOf('\n', marker);
        string handler = lineEnd > marker ? source[marker..lineEnd] : source[marker..];

        Assert.DoesNotContain("SettingsToggle(false, true)", handler, StringComparison.Ordinal);
        Assert.True(
            handler.Contains("ShowAll", StringComparison.Ordinal)
            || handler.Contains("Activate", StringComparison.Ordinal)
            || handler.Contains("BringToFront", StringComparison.Ordinal)
            || handler.Contains("SettingsToggle(true", StringComparison.Ordinal),
            "打开主界面 must show-or-focus, not hide. Handler: " + handler.Trim());
    }
}
