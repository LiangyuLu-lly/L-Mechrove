using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Tray right-click menu is capability-gated like the dashboard. Dead G-Helper/ASUS
/// leftovers and duplicate action names must not appear; working mode/GPU/keyboard
/// entries stay when the machine can actually do them.
/// </summary>
public class TrayContextMenuCapabilityTests
{
    static string ZhUi(string key) =>
        Properties.Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("zh-CN")) ?? "";

    static ContextMenuStrip BuildTrayMenu(SettingsForm form)
    {
        form.SetContextMenu();
        FieldInfo field = typeof(SettingsForm).GetField("contextMenuStrip", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<ContextMenuStrip>(field.GetValue(form));
    }

    static string[] MenuTexts(ContextMenuStrip menu)
    {
        var texts = new List<string>();
        void Walk(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                texts.Add(item.Text ?? "");
                if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
                    Walk(menuItem.DropDownItems);
            }
        }
        Walk(menu.Items);
        return texts.ToArray();
    }

    static IDisposable SwapHardware(MechrevoHw? hardware, bool audit)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = audit;
        Program.hw = hardware!;
        return new Restore(previousHardware, previousAudit);
    }

    sealed class Restore(MechrevoHw? hardware, bool audit) : IDisposable
    {
        public void Dispose()
        {
            Program.hw = hardware!;
            Program.UiAuditMode = audit;
        }
    }

    [Fact]
    public void TheTrayMenuDoesNotOfferDeadAsusOrBatteryThreeModeLeftovers()
    {
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { TurboMode = true }), audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form));

        Assert.DoesNotContain(texts, text => text.Contains("XGM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("性能 (满充)", texts);
        Assert.DoesNotContain("平衡 (80%)", texts);
        Assert.DoesNotContain("健康 (60%)", texts);
        Assert.DoesNotContain(texts, text => text.Contains("FnLock", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Fn-Lock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("FHD", texts);
        Assert.DoesNotContain("UHD", texts);
        Assert.DoesNotContain(Properties.Strings.Multizone, texts);
        Assert.DoesNotContain(Properties.Strings.OneZone, texts);
        Assert.DoesNotContain(Properties.Strings.Optimized, texts);
    }

    [Fact]
    public void TheTrayMenuHasNoDuplicateActionNames()
    {
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { TurboMode = true, Keyboard = true, DgpuDirect = true }), audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form))
            .Where(text => text.Length > 0 && text != "-")
            .ToArray();

        string[] duplicates = texts.GroupBy(text => text, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, "duplicate tray actions: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void TheTrayMenuUsesTheSameModeNamesAsTheDashboard()
    {
        // 旧托盘文案「静音 (办公) / 平衡 (游戏) / 狂暴 (增强)」和主窗 resx 不是同一套。
        // 现在三档必须就是 Strings.Silent / Balanced / Turbo，缺一档仍然失败。
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { TurboMode = true }), audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form));

        Assert.Contains(Properties.Strings.Silent, texts);
        Assert.Contains(Properties.Strings.Balanced, texts);
        Assert.Contains(Properties.Strings.Turbo, texts);
    }

    [Fact]
    public void TheTrayOmitsGpuItemsWhenTheMachineCannotOfferAGpuSwitch()
    {
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { DgpuDirect = false, IgpuOnly = false }), audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form));

        Assert.DoesNotContain(Properties.Strings.GPUMode, texts);
        Assert.DoesNotContain(Properties.Strings.EcoMode, texts);
        Assert.DoesNotContain(Properties.Strings.StandardMode, texts);
        Assert.DoesNotContain(Properties.Strings.UltimateMode, texts);
    }

    [Fact]
    public void TheTrayOmitsGpuItemsWhenHardwareIsNull()
    {
        using var _ = SwapHardware(null, audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form));

        Assert.DoesNotContain(Properties.Strings.GPUMode, texts);
        Assert.DoesNotContain(Properties.Strings.EcoMode, texts);
        Assert.DoesNotContain(Properties.Strings.StandardMode, texts);
        Assert.DoesNotContain(Properties.Strings.UltimateMode, texts);
    }

    [Fact]
    public void TheTrayOffersGpuItemsWhenCanOfferGpuModeSwitchIsTrue()
    {
        var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities { DgpuDirect = true });
        using var _ = SwapHardware(hardware, audit: false);
        using var form = new SettingsForm();
        form.CreateControl();
        string[] texts = MenuTexts(BuildTrayMenu(form));

        Assert.DoesNotContain(Properties.Strings.Optimized, texts);
        if (!hardware.CanOfferGpuModeSwitch) return;
        Assert.Contains(Properties.Strings.GPUMode, texts);
        Assert.Contains(Properties.Strings.StandardMode, texts);
        Assert.Contains(Properties.Strings.UltimateMode, texts);
        Assert.DoesNotContain(Properties.Strings.EcoMode, texts);
    }

    [Fact]
    public void SetContextMenuUsesTheSameCanOfferPredicatesAsTheDashboard()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        int start = source.IndexOf("public void SetContextMenu()", StringComparison.Ordinal);
        int end = source.IndexOf("public void InitContextMenuTheme()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = source[start..end];

        // 托盘与主界面读同一个布局值（MechrevoHw.GpuRowLayout 汇总了 CanOfferGpuModeSwitch /
        // CanOfferIgpuOnly / CanOfferNvPreferredGpu），没有硬件时整行隐藏。
        Assert.Contains("GpuRowLayout ?? GpuRowLayout.Hidden", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CanOfferGpuModeSwitch ?? true", source, StringComparison.Ordinal);
        Assert.Contains("GpuRowLayouts.HasIgpuSegment", body, StringComparison.Ordinal);
        string hardware = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoHw.cs");
        Assert.Contains("public GpuRowLayout GpuRowLayout => GpuRowLayouts.Resolve(", hardware, StringComparison.Ordinal);
        Assert.Contains("Properties.Strings.TrayActionFailed", body, StringComparison.Ordinal);
        // 托盘切模式与主界面同一入口（编排层），失败提示也在那一处。
        Assert.Contains("ActivatePerfModeAsync(", body, StringComparison.Ordinal);
        Assert.Contains("Properties.Strings.PerfModeSwitchFailed",
            GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Mode", "ModeControl.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("SwitchCustomProfileWithResend", body, StringComparison.Ordinal);
        Assert.Contains("Properties.Strings.QuickSwitchFailed", source, StringComparison.Ordinal);
        Assert.Contains("Properties.Strings.StartupTaskFailed", source, StringComparison.Ordinal);
        Assert.Equal("托盘操作失败。", ZhUi("TrayActionFailed"));
        Assert.Equal("性能模式切换失败。", ZhUi("PerfModeSwitchFailed"));
        Assert.Equal("自定义档已切换，电源计划未确认。", ZhUi("CustomProfilePlanUnconfirmed"));
        Assert.Equal("快捷开关设置失败。", ZhUi("QuickSwitchFailed"));
        Assert.Equal("开机自启动设置失败。", ZhUi("StartupTaskFailed"));
        Assert.DoesNotContain("buttonStopGPU.Visible = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("panelScreen.Visible = ScreenPanelVisibility.PanelVisible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("性能 (满充)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToogleFHD", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToogleMiniled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToogleHDRControl", body, StringComparison.Ordinal);
        Assert.DoesNotContain("XGM", body, StringComparison.Ordinal);
    }
}
