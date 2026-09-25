using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// Every performance mode has a secondary editor. Firmware detail is real only for the
/// mode that is actually running; a built-in edit must not be redirected onto a custom slot.
/// </summary>
public class ModeSecondaryEditorTests
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

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control child in Descendants(control))
                yield return child;
        }
    }

    [Fact]
    public void ModeSecondaryEditor_NamesOfficeGamingTurboSilentTurboAndCustom()
    {
        ModeEditorTarget[] targets =
        [
            ModeEditorTarget.Office,
            ModeEditorTarget.Gaming,
            ModeEditorTarget.Turbo,
            ModeEditorTarget.SilentTurbo,
            ModeEditorTarget.Custom,
        ];

        string[] titles = targets.Select(ModeSecondaryEditor.Title).ToArray();
        Assert.Equal(titles.Length, titles.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains(Properties.Strings.Silent, ModeSecondaryEditor.Title(ModeEditorTarget.Office));
        Assert.Contains(Properties.Strings.Balanced, ModeSecondaryEditor.Title(ModeEditorTarget.Gaming));
        Assert.Contains(Properties.Strings.Turbo, ModeSecondaryEditor.Title(ModeEditorTarget.Turbo));
        Assert.Contains(Properties.Strings.SilentTurbo, ModeSecondaryEditor.Title(ModeEditorTarget.SilentTurbo));
        Assert.Contains("自定义", ModeSecondaryEditor.Title(ModeEditorTarget.Custom));
        Assert.DoesNotContain(Properties.Strings.Silent, ModeSecondaryEditor.Title(ModeEditorTarget.Custom));

        foreach (ModeEditorTarget builtIn in targets.Where(target => target.VisualMode != MechrevoService.ModeCustom))
        {
            string hint = ModeSecondaryEditor.Hint(builtIn);
            Assert.Contains(ModeSecondaryEditor.DisplayName(builtIn), hint);
            Assert.Contains("SET_OPERATING_MODE_DETAIL", hint);
            Assert.Contains("不会改写自定义档", hint);
        }
    }

    [Fact]
    public void ModeSecondaryEditor_RefusesFirmwareDetailWhenTheActiveModeDiffers()
    {
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Office, MechrevoService.ModeGaming));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Gaming, MechrevoService.ModeTurbo));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Turbo, MechrevoService.ModeOffice));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.SilentTurbo, MechrevoService.ModeCustom));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Custom, MechrevoService.ModeOffice));

        string refused = ModeSecondaryEditor.RefusedWriteText(ModeEditorTarget.Office);
        Assert.Contains(Properties.Strings.Silent, refused);
        Assert.Contains("没有写入自定义档", refused);
        Assert.DoesNotContain("已保存", refused);
    }

    [Fact]
    public void ModeSecondaryEditor_AllowsFirmwareDetailOnlyForTheActiveMode()
    {
        Assert.Equal(
            FirmwareDetailWrite.ApplyToActiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Office, MechrevoService.ModeOffice));
        Assert.Equal(
            FirmwareDetailWrite.ApplyToActiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Gaming, MechrevoService.ModeGaming));
        Assert.Equal(
            FirmwareDetailWrite.ApplyToActiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Turbo, MechrevoService.ModeTurbo));
        Assert.Equal(
            FirmwareDetailWrite.ApplyToActiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.Custom, MechrevoService.ModeCustom));
    }

    [Fact]
    public void ModeSecondaryEditor_DoesNotRedirectABuiltInEditToTheCustomSlot()
    {
        ModeEditorTarget[] builtIn =
        [
            ModeEditorTarget.Office,
            ModeEditorTarget.Gaming,
            ModeEditorTarget.Turbo,
            ModeEditorTarget.SilentTurbo,
        ];
        int[] activeModes =
        [
            MechrevoService.ModeOffice,
            MechrevoService.ModeGaming,
            MechrevoService.ModeTurbo,
            MechrevoService.ModeCustom,
        ];

        foreach (ModeEditorTarget editing in builtIn)
        {
            foreach (int active in activeModes)
            {
                Assert.False(
                    ModeSecondaryEditor.WritesCustomSlot(editing, active),
                    $"{ModeSecondaryEditor.DisplayName(editing)} while active={active} must not write the custom slot.");
            }
        }

        Assert.True(ModeSecondaryEditor.WritesCustomSlot(ModeEditorTarget.Custom, MechrevoService.ModeCustom));
        Assert.False(ModeSecondaryEditor.WritesCustomSlot(ModeEditorTarget.Custom, MechrevoService.ModeOffice));
    }

    [Fact]
    public void ModeSecondaryEditor_RejectsGHelperExtraSlotsTheFirmwareCannotAccept()
    {
        Assert.False(ModeSecondaryEditor.IsFirmwareVisualMode(4));
        Assert.False(ModeSecondaryEditor.IsFirmwareVisualMode(19));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(new ModeEditorTarget(4, false), 4));
        Assert.Equal(
            FirmwareDetailWrite.RefuseInactiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(new ModeEditorTarget(19, false), MechrevoService.ModeGaming));

        int callers = Directory.EnumerateFiles(
                GcuInstallerHarness.Path("src", "MechrevoLiteWin"),
                "*.cs",
                SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains("Modes.Add(", StringComparison.Ordinal));
        Assert.Equal(0, callers);
    }

    [Fact]
    public void ModeSecondaryEditor_SilentTurboSharesTheTurboSlotAndDoesNotAutoApplyGpuOverclock()
    {
        Assert.Equal(MechrevoService.ModeTurbo, ModeEditorTarget.SilentTurbo.VisualMode);
        Assert.Equal(
            FirmwareDetailWrite.ApplyToActiveMode,
            ModeSecondaryEditor.DecideFirmwareWrite(ModeEditorTarget.SilentTurbo, MechrevoService.ModeTurbo));
        Assert.Contains("共用", ModeSecondaryEditor.Hint(ModeEditorTarget.SilentTurbo));
        Assert.Contains("自动 GPU 超频", ModeSecondaryEditor.Hint(ModeEditorTarget.SilentTurbo));

        string editor = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Mode", "ModeSecondaryEditor.cs");
        string form = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "CustomModeForm.cs");
        Assert.DoesNotContain("ApplyTurboGpuOverclockDefaults", editor);
        Assert.DoesNotContain("ApplyTurboGpuOverclockDefaults", form);
        Assert.Contains(
            "ApplyTurboGpuOverclockDefaults",
            GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoService.cs"));
    }

    [Fact]
    public void CustomModeForm_BindEditingTarget_NamesTheModeAndHidesCustomSlots()
    {
        using var _ = UseAuditMode();
        using var form = new CustomModeForm();
        form.CreateControl();
        form.Show();

        form.BindEditingTarget(ModeEditorTarget.Office);

        Assert.Equal(ModeEditorTarget.Office, form.EditingTarget);
        Assert.Contains(Properties.Strings.Silent, form.Text);
        Assert.Contains("二次自定义", form.Text);
        Assert.False(form.Controls.Find("profileRow", true).Single().Visible);
        Assert.False(form.Controls.Find("planCombo", true).Single().Parent!.Visible);
        Assert.False(form.Controls.Find("boostCombo", true).Single().Parent!.Visible);
        Label hint = form.Controls.Find("labelModeEditorHint", true).OfType<Label>().Single();
        Assert.True(hint.Visible);
        Assert.Contains("不会改写自定义档", hint.Text);

        form.BindEditingTarget(ModeEditorTarget.Custom);
        Assert.Equal("自定义性能模式", form.Text);
        Assert.True(form.Controls.Find("profileRow", true).Single().Visible);
        Assert.True(form.Controls.Find("planCombo", true).Single().Parent!.Visible);
    }

    [Fact]
    public async Task CustomModeForm_CommitDetail_RefusesABuiltInWriteOntoAnotherMode()
    {
        using var _ = UseAuditMode();
        using var form = new CustomModeForm();
        form.BindEditingTarget(ModeEditorTarget.Office);
        bool called = false;

        DetailCommit result = await form.CommitDetailAsync(
            new Dictionary<string, string> { ["PL1"] = "35" },
            MechrevoService.ModeCustom,
            _ =>
            {
                called = true;
                return Task.FromResult(true);
            });

        Assert.Equal(DetailCommit.RefusedWrongMode, result);
        Assert.False(called);
        Label status = form.Controls.Find("labelModeEditorStatus", true).OfType<Label>().Single();
        Assert.Contains("没有写入自定义档", status.Text);
    }

    [Fact]
    public async Task CustomModeForm_CommitDetail_WritesTheActiveBuiltInModeWithoutACustomSlotRedirect()
    {
        using var _ = UseAuditMode();
        using var form = new CustomModeForm();
        form.BindEditingTarget(ModeEditorTarget.Gaming);
        Dictionary<string, string>? seen = null;

        DetailCommit result = await form.CommitDetailAsync(
            new Dictionary<string, string> { ["PL1"] = "45" },
            MechrevoService.ModeGaming,
            fields =>
            {
                seen = fields;
                return Task.FromResult(true);
            });

        Assert.Equal(DetailCommit.Applied, result);
        Assert.NotNull(seen);
        Assert.Equal("45", seen["PL1"]);
        Assert.False(seen.ContainsKey("ProfileIndex"));
        Assert.False(seen.ContainsKey("Action"));
    }

    [Fact]
    public void SettingsForm_EveryModeButtonSecondaryMenuOpensThatModesEditor()
    {
        using var _ = UseAuditMode();
        using var form = new SettingsForm();
        form.CreateControl();

        (string Text, ModeEditorTarget Target)[] expected =
        [
            (Properties.Strings.Silent, ModeEditorTarget.Office),
            (Properties.Strings.Balanced, ModeEditorTarget.Gaming),
            (Properties.Strings.SilentTurbo, ModeEditorTarget.SilentTurbo),
            (Properties.Strings.Turbo, ModeEditorTarget.Turbo),
            ("自定义", ModeEditorTarget.Custom),
        ];

        foreach ((string text, ModeEditorTarget target) in expected)
        {
            RButton button = Descendants(form).OfType<RButton>().Single(candidate => candidate.Text == text);
            ToolStripMenuItem item = button.ContextMenuStrip!.Items
                .OfType<ToolStripMenuItem>()
                .Single(candidate => candidate.Name == "menuSecondaryCustomize");
            Assert.Equal(ModeSecondaryEditor.EditorMenuText, item.Text);
            Assert.Equal(target, Assert.IsType<ModeEditorTarget>(item.Tag));

            item.PerformClick();
            CustomModeForm editor = form.OwnedForms.OfType<CustomModeForm>().Single();
            Assert.Equal(target, editor.EditingTarget);
            Assert.Contains(ModeSecondaryEditor.DisplayName(target), editor.Text);
        }
    }

    [Fact]
    public void SettingsForm_TraySecondaryMenuListsEveryModeWithoutDuplicatingSwitchNames()
    {
        using var _ = UseAuditMode();
        using var form = new SettingsForm();
        form.CreateControl();
        form.SetContextMenu();

        FieldInfo field = typeof(SettingsForm).GetField(
            "contextMenuStrip",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var menu = Assert.IsAssignableFrom<ContextMenuStrip>(field.GetValue(form));
        ToolStripMenuItem secondary = menu.Items.OfType<ToolStripMenuItem>()
            .Single(item => item.Name == "menuSecondaryCustomize");

        ModeEditorTarget[] targets = secondary.DropDownItems.OfType<ToolStripMenuItem>()
            .Select(item => Assert.IsType<ModeEditorTarget>(item.Tag))
            .ToArray();
        Assert.Contains(ModeEditorTarget.Office, targets);
        Assert.Contains(ModeEditorTarget.Gaming, targets);
        Assert.Contains(ModeEditorTarget.SilentTurbo, targets);
        Assert.Contains(ModeEditorTarget.Turbo, targets);
        Assert.Contains(ModeEditorTarget.Custom, targets);

        string[] texts = MenuTexts(menu);
        string[] duplicates = texts
            .Where(text => text.Length > 0 && text != "-")
            .GroupBy(text => text, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, "duplicate tray actions: " + string.Join(", ", duplicates));
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
}
