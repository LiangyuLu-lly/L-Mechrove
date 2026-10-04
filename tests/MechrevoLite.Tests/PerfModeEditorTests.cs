using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 统一性能模式编辑器（G-Helper 式）：所有模式同一个入口，顶部下拉选模式；
/// 主界面模式按钮不再有右键「二次自定义」；托盘列出全部模式并给出唯一的编辑入口。
/// </summary>
public class PerfModeEditorTests
{
    static IDisposable UseAuditMode()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        return new Restore(previousAudit, previousHardware);
    }

    sealed class Restore(bool audit, MechrevoHw? hardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = audit;
            Program.hw = hardware;
        }
    }

    static void CleanPerfConfig()
    {
        foreach (string key in AppConfig.Snapshot().Keys
                     .Where(k => k.StartsWith("perf_", StringComparison.Ordinal)).ToArray())
            AppConfig.Remove(key);
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control child in Descendants(control))
                yield return child;
        }
    }

    static T Named<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, true).OfType<T>().Single();

    [Theory]
    [InlineData("_pl1", "_pl1Val", 0, 300, 150, false)]
    [InlineData("_pl4", "_pl4Val", 0, 400, 290, false)]
    [InlineData("_coreOc", "_coreOcVal", -500, 250, -300, true)]
    public void ExpandingAndShrinkingLiveRangesKeepsLinkedControlsAndConfigurationConsistent(
        string sliderField, string numericField, int minimum, int maximum, int value, bool allowNegative)
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            using var _ = UseAuditMode();
            using var form = new CustomModeForm();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(CustomModeForm).GetField("_syncing", flags)!.SetValue(form, true);
            var slider = (RSlider)typeof(CustomModeForm).GetField(sliderField, flags)!.GetValue(form)!;
            var numeric = (RNumericUpDown)typeof(CustomModeForm).GetField(numericField, flags)!.GetValue(form)!;
            string before = System.Text.Json.JsonSerializer.Serialize(AppConfig.Snapshot());

            CustomModeForm.ApplyRange(slider, numeric, minimum, maximum, value, allowNegative);
            Assert.Equal(value, slider.Value);
            Assert.Equal(value, numeric.Value);
            Assert.Equal(minimum, slider.Minimum);
            Assert.Equal(maximum, numeric.Maximum);

            CustomModeForm.ApplyRange(slider, numeric, 10, 30, value, allowNegative);
            Assert.Equal(Math.Clamp(value, 10, 30), slider.Value);
            Assert.Equal(slider.Value, numeric.Value);
            Assert.Equal(10, numeric.Minimum);
            Assert.Equal(30, slider.Maximum);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(AppConfig.Snapshot()));
        });
    }

    [Fact]
    public void TheDropDownListsEveryBuiltInModeAndTheCustomMode()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerfConfig();
            using var _ = UseAuditMode();
            using var form = new CustomModeForm();
            form.CreateControl();

            RComboBox combo = Named<RComboBox>(form, "modeCombo");
            string[] items = combo.Items.Cast<object>().Select(o => o.ToString()!).ToArray();

            Assert.Equal(
                new[]
                {
                    Properties.Strings.Silent, Properties.Strings.Balanced, Properties.Strings.SilentTurbo,
                    Properties.Strings.Turbo, string.Format(Properties.Strings.CustomProfileN, 1),
                },
                items);
        });
    }

    [Fact]
    public void BuiltInModesCannotBeRenamedOrDeleted_AndTheLastCustomModeCannotBeDeleted()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerfConfig();
            using var _ = UseAuditMode();
            using var form = new CustomModeForm();
            form.CreateControl();

            form.BindMode(PerfModeDefinition.BuiltInId(PerfModeKind.Balanced));
            Assert.Equal("balanced", form.EditingModeId);
            Assert.False(Named<RButton>(form, "buttonModeRename").Enabled);
            Assert.False(Named<RButton>(form, "buttonModeDelete").Enabled);
            Assert.True(Named<RButton>(form, "buttonModeNew").Enabled);
            // 未改过的内置模式：如实说明改哪些项会让它转到自定义档承载。
            Assert.Equal(Properties.Strings.PerfModeHintVendor, Named<Label>(form, "labelModeRouteHint").Text);
            Assert.Contains(Properties.Strings.Balanced, form.Text);

            form.BindMode(PerfModeDefinition.CustomId(1));
            Assert.True(Named<RButton>(form, "buttonModeRename").Enabled);
            Assert.False(Named<RButton>(form, "buttonModeDelete").Enabled);
        });
    }

    [Fact]
    public void ACustomizedBuiltInModeIsMarkedInTheDropDownAndExplainsItsSlot()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerfConfig();
            PerfModeStore.SaveSettings("balanced", new PerfModeSettings { Pl1 = 60 });
            using var _ = UseAuditMode();
            using var form = new CustomModeForm();
            form.CreateControl();

            string[] items = Named<RComboBox>(form, "modeCombo").Items.Cast<object>().Select(o => o.ToString()!).ToArray();
            Assert.Contains(string.Format(Properties.Strings.PerfModeCustomized, Properties.Strings.Balanced), items);

            form.BindMode("balanced");
            Assert.Equal(string.Format(Properties.Strings.PerfModeHintEmulated, "?"),
                Named<Label>(form, "labelModeRouteHint").Text);
        });
    }

    [Fact]
    public void AppSideDropDownsStartAtUnchangedAndFollowTheBoundMode()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerfConfig();
            PerfModeStore.SaveSettings("turbo", new PerfModeSettings { WindowsPowerMode = 2, CpuBoost = 3, FanBoost = true });
            using var _ = UseAuditMode();
            using var form = new CustomModeForm();
            form.CreateControl();

            form.BindMode("silent");
            Assert.Equal(0, Named<RComboBox>(form, "powerModeCombo").SelectedIndex);
            Assert.Equal(0, Named<RComboBox>(form, "boostCombo").SelectedIndex);
            Assert.Equal(0, Named<RComboBox>(form, "planCombo").SelectedIndex);
            Assert.Equal(0, Named<RComboBox>(form, "fanBoostCombo").SelectedIndex);

            form.BindMode("turbo");
            Assert.Equal(3, Named<RComboBox>(form, "powerModeCombo").SelectedIndex);   // 最佳性能
            Assert.Equal(4, Named<RComboBox>(form, "boostCombo").SelectedIndex);       // 索引 3 + 「不改变」
            Assert.Equal(1, Named<RComboBox>(form, "fanBoostCombo").SelectedIndex);    // 开启
        });
    }

    [Fact]
    public void EditorFieldKeysMapOntoTheModeSettings()
    {
        PerfModeSettings s = CustomModeForm.ApplyFields(PerfModeSettings.Default, new Dictionary<string, string>
        {
            ["PL1"] = "55",
            ["PL2"] = "70",
            ["PL4"] = "120",
            ["CpuTccOffsetSwitch"] = "1",
            ["CpuTccOffset"] = "92",
            ["GpuConfigurableTGPTarget"] = "110",
            ["GpuDynamicBoostSwitch"] = "0",
            ["GpuDynamicBoost"] = "15",
            ["FanSwitchSpeedEnabled"] = "1",
            ["FanSwitchSpeed"] = "600",
            ["OverClockingSwitch"] = "1",
            ["GpuCoreClockOffsetOC"] = "100",
            ["GpuMemoryClockOffsetOC"] = "400",
            ["Unknown"] = "5",
            ["PL3"] = "not-a-number",
        });

        Assert.Equal(55, s.Pl1);
        Assert.Equal(70, s.Pl2);
        Assert.Equal(120, s.Pl4);
        Assert.True(s.TccOn);
        Assert.Equal(92, s.TccTarget);
        Assert.Equal(110, s.GpuTgp);
        Assert.False(s.GpuDynamicBoostOn);
        Assert.Equal(15, s.GpuDynamicBoost);
        Assert.True(s.FanSwitchSpeedOn);
        Assert.Equal(600, s.FanSwitchSpeedMs);
        Assert.True(s.GpuOverclockOn);
        Assert.Equal(100, s.GpuCoreOffset);
        Assert.Equal(400, s.GpuMemoryOffset);
        Assert.Null(s.WindowsPowerMode);
    }

    [Fact]
    public void MissingSavedPowerPlanIsShownExplicitlyWithoutChangingTheConfiguration()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            using var _ = UseAuditMode();
            string absent = Guid.NewGuid().ToString();
            PerfModeStore.SaveSettings("balanced", new PerfModeSettings { PowerPlanGuid = absent });
            using var form = new CustomModeForm();
            form.BindMode("balanced");
            var selected = Assert.IsType<KeyValuePair<string, string>>(Named<RComboBox>(form, "planCombo").SelectedItem);
            Assert.Equal(absent, selected.Value);
            Assert.Equal(Properties.Strings.PowerPlanUnavailable, selected.Key);
            Assert.Equal(absent, PerfModeStore.LoadSettings("balanced").PowerPlanGuid);
        });
    }

    [Fact]
    public void ModeButtonsHaveNoSecondaryRightClickMenu()
    {
        using var _ = UseAuditMode();
        using var form = new SettingsForm();
        form.CreateControl();

        string[] modeTexts =
        {
            Properties.Strings.Silent, Properties.Strings.Balanced, Properties.Strings.SilentTurbo,
            Properties.Strings.Turbo, Properties.Strings.ModeCustom,
        };
        foreach (string text in modeTexts)
        {
            RButton button = Descendants(form).OfType<RButton>().Single(candidate => candidate.Text == text);
            Assert.Null(button.ContextMenuStrip);
        }
    }

    [Fact]
    public void TrayListsEveryModeOnceAndOffersExactlyOneEditorEntry()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerfConfig();
            using var _ = UseAuditMode();
            using var form = new SettingsForm();
            form.CreateControl();
            form.SetContextMenu();

            FieldInfo field = typeof(SettingsForm).GetField(
                "contextMenuStrip", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var menu = Assert.IsAssignableFrom<ContextMenuStrip>(field.GetValue(form));
            ToolStripMenuItem[] items = menu.Items.OfType<ToolStripMenuItem>().ToArray();

            Assert.DoesNotContain(items, item => item.Name == "menuSecondaryCustomize");
            Assert.Single(items, item => item.Name == "menuPerfModeEditor");
            foreach (string name in new[]
                     {
                         Properties.Strings.Silent, Properties.Strings.Balanced, Properties.Strings.SilentTurbo,
                         Properties.Strings.Turbo, string.Format(Properties.Strings.CustomProfileN, 1),
                     })
                Assert.Single(items, item => item.Text == name);

            string[] duplicates = items.Select(i => i.Text ?? "")
                .Where(text => text.Length > 0)
                .GroupBy(text => text, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            Assert.True(duplicates.Length == 0, "duplicate tray actions: " + string.Join(", ", duplicates));
        });
    }

    [Fact]
    public void TheEditorEntryOpensTheUnifiedEditor()
    {
        using var _ = UseAuditMode();
        using var form = new SettingsForm();
        form.CreateControl();

        form.ShowPerfModeEditor("turbo");

        CustomModeForm editor = form.OwnedForms.OfType<CustomModeForm>().Single();
        Assert.Equal("turbo", editor.EditingModeId);
        Assert.NotEmpty(editor.Controls.Find("paramTable", true));
        Assert.NotEmpty(editor.Controls.Find("modeCombo", true));
    }

    [Fact]
    public void OnlyTheServiceAppliesTheOfficialTurboAutoOverclock()
    {
        // 静音狂暴从不下发官方 +105/+500；编辑器也不自己补发——自动超频只在服务的切换路径上。
        string form = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "CustomModeForm.cs");
        Assert.DoesNotContain("ApplyTurboGpuOverclockDefaults", form);
        Assert.Contains("ApplyTurboGpuOverclockDefaults",
            GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoService.cs"));
        Assert.False(MechrevoService.ShouldApplyTurboGpuOverclockDefaultsOnSubMode(silent: true));
        Assert.True(MechrevoService.ShouldApplyTurboGpuOverclockDefaultsOnSubMode(silent: false));
    }

    [Fact]
    public void NoGHelperExtraModesAreRegistered()
    {
        int callers = Directory.EnumerateFiles(
                GcuInstallerHarness.Path("src", "MechrevoLiteWin"), "*.cs", SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains("Modes.Add(", StringComparison.Ordinal));
        Assert.Equal(0, callers);
    }
}
