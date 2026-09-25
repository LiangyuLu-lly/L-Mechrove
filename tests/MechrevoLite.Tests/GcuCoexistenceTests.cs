using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 外来 GCU 共存：启动时只提示用户自行卸官方控制台，绝不静默删除。
/// </summary>
public class GcuCoexistenceTests
{
    [Theory]
    [InlineData(GcuCoexistenceKind.None, false)]
    [InlineData(GcuCoexistenceKind.ForeignService, true)]
    [InlineData(GcuCoexistenceKind.PortOwner, true)]
    [InlineData(GcuCoexistenceKind.Both, true)]
    public void RequiresConsoleRemovalPrompt_MatchesKind(GcuCoexistenceKind kind, bool expected) =>
        Assert.Equal(expected, GcuCoexistence.RequiresConsoleRemovalPrompt(kind));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void WarnAtStartup_PromptsOnlyWhenLeftoverExists(
        bool leftoverConsoleOrService, bool leftoverPortOwner, bool expectPrompt)
    {
        string? shown = null;
        bool prompted = GcuCoexistence.WarnAtStartup(
            prompt => shown = prompt,
            () => leftoverConsoleOrService,
            () => leftoverPortOwner);

        Assert.Equal(expectPrompt, prompted);
        if (expectPrompt)
        {
            Assert.False(string.IsNullOrWhiteSpace(shown));
            Assert.Contains("请手动卸载", shown);
            Assert.Contains("官方控制台", shown);
            Assert.DoesNotContain("已删除", shown);
            Assert.DoesNotContain("已为你卸载", shown);
        }
        else
            Assert.Null(shown);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"C:\Program Files\L-Mechrevo\GCU\GCUService.exe", false)]
    [InlineData(@"C:\Program Files\OEM\ControlCenter\GCUService.exe", true)]
    [InlineData(@"""C:\Program Files\OEM\GCUBridge.exe""", true)]
    public void IsLeftoverVendorService_IgnoresOurImage(string? imagePath, bool expected) =>
        Assert.Equal(expected, GcuCoexistence.IsLeftoverVendorService(imagePath));

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, @"C:\Program Files\OEM\GCUService.exe", false)]
    [InlineData(true, null, true)]
    [InlineData(true, @"C:\Program Files\OEM\GCUService.exe", true)]
    [InlineData(true, @"C:\Program Files\L-Mechrevo\GCU\GCUService.exe", false)]
    public void IsLeftoverPortOwner_OnlyWhenForeignOwnsThePort(
        bool portInUse, string? ownerImagePath, bool expected) =>
        Assert.Equal(expected, GcuCoexistence.IsLeftoverPortOwner(portInUse, ownerImagePath));

    [Fact]
    public void ProgramStartupCallsGcuCoexistenceWithoutSilentUninstall()
    {
        string program = File.ReadAllText(
            Path.Combine(GcuInstallerHarness.RepoRoot, "src", "MechrevoLiteWin", "Program.cs"));
        Assert.Contains("GcuCoexistence.WarnAtStartup", program, StringComparison.Ordinal);
        int call = program.IndexOf("GcuCoexistence.WarnAtStartup", StringComparison.Ordinal);
        int settingsToggle = program.IndexOf("SettingsToggle", call, StringComparison.Ordinal);
        Assert.True(settingsToggle < 0 || settingsToggle > call + 400,
            "startup coexistence wiring must not go through SettingsToggle");
    }
}
