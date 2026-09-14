using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

public class LightRowsLayoutTests
{
    static SettingsForm BuildAuditForm()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            return form;
        }
        catch
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAuditMode;
            throw;
        }
    }

    static void RestoreHarness(bool previousAuditMode, MechrevoLite.Hardware.MechrevoHw previousHardware)
    {
        Program.hw = previousHardware;
        Program.UiAuditMode = previousAuditMode;
    }

    static string[] RowNames() => new[] { "rowKeyboard", "rowLightbar", "rowLogo" };

    static IEnumerable<string> KeyboardEffectNames()
    {
        FieldInfo? field = typeof(RgbForm).GetField("HidEffects", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not Array array) yield break;
        foreach (object item in array)
        {
            string? name = item.GetType().GetField("Item2")?.GetValue(item) as string;
            if (name is not null) yield return name;
        }
    }

    static ComboBox GetPicker(TableLayoutPanel row) =>
        row.Controls.OfType<ComboBox>().Single();

    static string ExpectedLightbarSelectedText()
    {
        string effect = LightingSettingsStore.Load("HidLightbar/Ctrl", LightForm.LightbarEffects[0].Effect).Effect;
        return LightForm.LightbarEffects.FirstOrDefault(e => e.Effect == effect).Name ?? LightForm.LightbarEffects[0].Name;
    }

    static string ExpectedLogoSelectedText()
    {
        string effect = LightingSettingsStore.Load("HidLightbar_Logo/Ctrl", LightForm.LogoEffects[0].Effect).Effect;
        return LightForm.LogoEffects.FirstOrDefault(e => e.Effect == effect).Name ?? LightForm.LogoEffects[0].Name;
    }

    static string? ExtractValue(object? item)
    {
        if (item is null) return null;
        Type type = item.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            PropertyInfo? valueProperty = type.GetProperty("Value");
            object? value = valueProperty?.GetValue(item);
            return value?.ToString();
        }
        return item.ToString();
    }

    [Fact]
    public void LightRow_PowerSwitch_IsVerticallyCenteredInItsRow()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            foreach (string rowName in RowNames())
            {
                var row = form.Controls.Find(rowName, true).OfType<TableLayoutPanel>().Single();
                var sw = row.Controls.OfType<RCheckBox>().Single();
                Assert.Equal(AnchorStyles.Left, sw.Anchor);
                int rowCenter = row.Height / 2;
                int switchCenter = sw.Top + sw.Height / 2;
                Assert.True(Math.Abs(switchCenter - rowCenter) <= 2,
                    $"{rowName} 开关垂直中心 {switchCenter} 偏离行中心 {rowCenter}。");
            }
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousHardware!);
        }
    }

    [Fact]
    public void LightRow_EffectPickers_ExposeZoneEffectCatalogs()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            foreach (string rowName in RowNames())
            {
                var row = form.Controls.Find(rowName, true).OfType<TableLayoutPanel>().Single();
                ComboBox picker = GetPicker(row);
                string?[] expected = rowName switch
                {
                    "rowKeyboard" => KeyboardEffectNames().ToArray(),
                    "rowLightbar" => LightForm.LightbarEffects.Select(e => e.Name).ToArray(),
                    "rowLogo" => LightForm.LogoEffects.Select(e => e.Name).ToArray(),
                    _ => throw new InvalidOperationException($"未知灯行 {rowName}"),
                };
                string?[] actual = picker.Items.Cast<object>().Select(picker.GetItemText).ToArray();
                Assert.Equal(expected, actual);
            }
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousHardware!);
        }
    }

    [Fact]
    public void LightRow_EffectPicker_IsVerticallyCentered()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            foreach (string rowName in RowNames())
            {
                var row = form.Controls.Find(rowName, true).OfType<TableLayoutPanel>().Single();
                ComboBox picker = GetPicker(row);
                int rowCenter = row.Height / 2;
                int pickerCenter = picker.Top + picker.Height / 2;
                Assert.True(Math.Abs(pickerCenter - rowCenter) <= 2,
                    $"{rowName} 效果选择器垂直中心 {pickerCenter} 偏离行中心 {rowCenter}。");
            }
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousHardware!);
        }
    }

    [Fact]
    public void LightRow_EffectPicker_SelectsSavedEffect()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            foreach (string rowName in RowNames())
            {
                var row = form.Controls.Find(rowName, true).OfType<TableLayoutPanel>().Single();
                ComboBox picker = GetPicker(row);
                string expectedText = rowName switch
                {
                    "rowKeyboard" => KeyboardEffectNames().First(),
                    "rowLightbar" => ExpectedLightbarSelectedText(),
                    "rowLogo" => ExpectedLogoSelectedText(),
                    _ => throw new InvalidOperationException($"未知灯行 {rowName}"),
                };
                Assert.Equal(expectedText, picker.GetItemText(picker.SelectedItem));
            }
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousHardware!);
        }
    }
}
