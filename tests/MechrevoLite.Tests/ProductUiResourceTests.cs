using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows.Forms;
using MechrevoLite.Mode;
using MechrevoLite.UI;
using MechrevoLite.Update;

namespace MechrevoLite.Tests;

/// <summary>
/// English UI culture must select Properties.Strings, not the Chinese literals the
/// language picker cannot override. zh-CN values stay the previous product copy.
/// </summary>
public class ProductUiResourceTests
{
    static IDisposable UseAuditMode()
    {
        bool previous = Program.UiAuditMode;
        Program.UiAuditMode = true;
        return new Scoped(() => Program.UiAuditMode = previous);
    }

    static IDisposable UseUiCulture(string name)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        return new Scoped(() => CultureInfo.CurrentUICulture = previous);
    }

    sealed class Scoped(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    static string En(string key)
    {
        string? value = Properties.Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("en"));
        Assert.False(string.IsNullOrEmpty(value), $"English resource '{key}' is missing.");
        return value;
    }

    static string Zh(string key)
    {
        string? value = Properties.Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("zh-CN"));
        Assert.False(string.IsNullOrEmpty(value), $"zh-CN resource '{key}' is missing.");
        return value;
    }

    static T Field<T>(object instance, string name)
    {
        object? value = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(instance);
        return Assert.IsAssignableFrom<T>(value);
    }

    static Button RequireButton(Control root, string name)
    {
        Button? found = root.Controls.Find(name, true).OfType<Button>().FirstOrDefault();
        Assert.NotNull(found);
        return found!;
    }

    [Fact]
    public void EnglishAndChineseResources_HaveTheSameProductKeys()
    {
        var manager = new ResourceManager("MechrevoLite.Properties.Strings", typeof(SettingsForm).Assembly);
        using ResourceSet english = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!;
        using ResourceSet chinese = manager.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, true)!;
        var englishKeys = english.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        var chineseKeys = chinese.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        string[] added =
        [
            "ModeCustom", "GpuRouteIgpu", "GpuRouteStandard", "GpuRouteDirect",
            "GpuRouteIgpuTip", "GpuRouteStandardTip", "GpuRouteAutoTip", "GpuRouteDirectTip",
            "FooterSettings", "FooterDiagnostics", "FooterOverlay", "ExportDiagnostics",
            "GcuConnected", "GcuConnecting", "GcuDisconnected", "GcuUnknown",
            "FanCurve", "DonateTitle", "CheckForUpdates", "CustomModeTitle",
        ];
        foreach (string key in added)
        {
            Assert.Contains(key, englishKeys);
            Assert.Contains(key, chineseKeys);
        }
    }

    [Fact]
    public void EnglishCulture_ModeGpuFooterAndTray_MatchStrings()
    {
        using var culture = UseUiCulture("en");
        using var audit = UseAuditMode();
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.hw = null;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();

            Assert.Equal(En("ModeCustom"), Field<Button>(form, "buttonCustomMode").Text);
            Assert.Equal(Properties.Strings.ModeCustom, Field<Button>(form, "buttonCustomMode").Text);
            Assert.Equal(En("GpuRouteIgpu"), RequireButton(form, "buttonEco").Text);
            Assert.Equal(Properties.Strings.GpuRouteIgpu, RequireButton(form, "buttonEco").Text);
            Assert.Equal(En("GpuRouteStandard"), RequireButton(form, "buttonStandard").Text);
            Assert.Equal(En("GpuRouteDirect"), RequireButton(form, "buttonUltimate").Text);
            Assert.Equal(En("FooterSettings"), FindButtonText(form, En("FooterSettings")));
            Assert.Equal(En("FooterDiagnostics"), FindButtonText(form, En("FooterDiagnostics")));
            Assert.Equal(Properties.Strings.Quit, RequireButton(form, "buttonQuit").Text);
            Assert.Equal(Properties.Strings.Donate, RequireButton(form, "buttonDonate").Text);
            Assert.Equal(Properties.Strings.Updates, RequireButton(form, "buttonUpdates").Text);

            form.SetContextMenu();
            string[] tray = TrayTexts(form);
            Assert.Contains(Properties.Strings.Silent, tray);
            Assert.Contains(Properties.Strings.SilentTurbo, tray);
            Assert.Contains(En("TrayFanBoost"), tray);
            Assert.Contains(En("TrayOpenMain"), tray);
            Assert.Contains(En("ExportDiagnostics"), tray);
            Assert.DoesNotContain(tray, text => text.Contains("风扇增强", StringComparison.Ordinal)
                || text.Contains("打开主界面", StringComparison.Ordinal));
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    [Fact]
    public void EnglishCulture_SecondaryFormsAndGcu_MatchStrings()
    {
        using var culture = UseUiCulture("en");
        using var audit = UseAuditMode();
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.hw = null;
        try
        {
            var connected = GcuConnectionStatus.Describe(GcuConnectionState.Connected);
            Assert.Equal(En("GcuConnected"), connected.Text);
            Assert.Equal(Properties.Strings.GcuConnected, connected.Text);
            Assert.Equal(En("GcuConnecting"), GcuConnectionStatus.Describe(GcuConnectionState.Connecting).Text);
            Assert.Equal(En("GcuDisconnected"), GcuConnectionStatus.Describe(GcuConnectionState.Disconnected).Text);
            Assert.Equal(En("GcuUnknown"), GcuConnectionStatus.Describe(GcuConnectionState.Unknown).Text);
            Assert.DoesNotContain("已连接", connected.Text);

            using var custom = new CustomModeForm();
            Assert.Equal(En("CustomModeTitle"), custom.Text);
            Assert.Equal(Properties.Strings.CustomModeTitle, custom.Text);
            Assert.Contains(custom.Controls.Cast<Control>().SelectMany(All), c => c.Text == En("CpuPl1"));
            Assert.Equal(En("CustomModeHint"), ModeSecondaryEditor.Hint(ModeEditorTarget.Custom));

            using var fan = new FanCurveForm();
            Assert.Equal(En("FanCurve"), fan.Text);
            Assert.Equal(Properties.Strings.FanCurve, fan.Text);

            using var donate = new DonateForm();
            Assert.Equal(En("DonateTitle"), donate.Text);
            Assert.Equal(Properties.Strings.DonateTitle, donate.Text);

            using var update = new UpdateForm(new UpdateInfo(
                CurrentVersion: "0.0.0-test", LatestVersion: "9.9.9-test", Channel: "test", ChannelFallback: false,
                UpdateAvailable: false, ReleaseDate: null, Notes: null, FileName: null, Size: null,
                Sha256: null, DownloadUrl: null, DownloadPage: null));
            Assert.Equal(En("CheckForUpdates"), update.Text);
            Assert.Equal(Properties.Strings.CheckForUpdates, update.Text);
            Assert.Equal(En("UpdateNotesEmpty"), Field<Label>(update, "_notesEmpty").Text);
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    [Fact]
    public void ZhCnResources_KeepThePreviousProductCopy()
    {
        Assert.Equal("自定义", Zh("ModeCustom"));
        Assert.Equal("集显", Zh("GpuRouteIgpu"));
        Assert.Equal("直连", Zh("GpuRouteDirect"));
        Assert.Equal("设置", Zh("FooterSettings"));
        Assert.Equal("诊断", Zh("FooterDiagnostics"));
        Assert.Equal("风扇曲线", Zh("FanCurve"));
        Assert.Equal("赞助支持", Zh("DonateTitle"));
        Assert.Equal("检查更新", Zh("CheckForUpdates"));
        Assert.Equal("● GCU 已连接", Zh("GcuConnected"));
        Assert.Equal("暂无更新说明", Zh("UpdateNotesEmpty"));
        Assert.Equal("自定义性能模式", Zh("CustomModeTitle"));
        Assert.Equal("CPU 功耗墙 PL1 (W)", Zh("CpuPl1"));
    }

    static string FindButtonText(Control root, string text)
    {
        Button? button = All(root).OfType<Button>().FirstOrDefault(b => b.Text == text);
        Assert.NotNull(button);
        return button.Text;
    }

    static string[] TrayTexts(SettingsForm form)
    {
        object? menuValue = typeof(SettingsForm)
            .GetField("contextMenuStrip", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form);
        var menu = Assert.IsAssignableFrom<ContextMenuStrip>(menuValue);
        return menu.Items.Cast<ToolStripItem>()
            .SelectMany(ItemTexts)
            .Where(text => text.Length > 0)
            .ToArray();
    }

    static IEnumerable<string> ItemTexts(ToolStripItem item)
    {
        if (item.Text is { Length: > 0 } text) yield return text;
        if (item is ToolStripDropDownItem drop)
        {
            foreach (ToolStripItem child in drop.DropDownItems)
            {
                foreach (string nested in ItemTexts(child)) yield return nested;
            }
        }
    }

    static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in All(child)) yield return nested;
        }
    }
}
