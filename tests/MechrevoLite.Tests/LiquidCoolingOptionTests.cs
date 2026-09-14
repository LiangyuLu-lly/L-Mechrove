using System.Text.RegularExpressions;
using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LiquidCoolingOptionTests
{
    static SettingsForm BuildAuditForm()
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            var form = new SettingsForm();
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.PerformLayout();
            return form;
        }
        catch
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
            Program.UiAuditMode = previousAuditMode;
            throw;
        }
    }

    static void RestoreHarness(bool previousAuditMode, bool previousReportedCapabilities, MechrevoLite.Hardware.MechrevoHw previousHardware)
    {
        Program.hw = previousHardware;
        Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
        Program.UiAuditMode = previousAuditMode;
    }

    static (IReadOnlyList<string> Texts, IReadOnlyList<int> Values) ReadComboItems(ComboBox combo)
    {
        var texts = new List<string>();
        var values = new List<int>();
        foreach (object item in combo.Items)
        {
            if (item is KeyValuePair<string, int> pair)
            {
                texts.Add(pair.Key);
                values.Add(pair.Value);
            }
            else
            {
                texts.Add(item.ToString() ?? "");
                values.Add(0);
            }
        }
        return (texts, values);
    }

    [Fact]
    public void PumpOptions_ArePercentOnlyWithoutPlaceholder()
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            ComboBox pump = form.Controls.Find("comboLcPump", true).OfType<ComboBox>().Single();
            (IReadOnlyList<string> texts, IReadOnlyList<int> values) = ReadComboItems(pump);

            Assert.All(texts, t => Assert.DoesNotContain("档位", t));
            Assert.All(texts, t => Assert.DoesNotContain("低", t));
            Assert.All(texts, t => Assert.DoesNotContain("中", t));
            // 2026-09-13 用户要求：最高档显示「最大」而非百分比，其余档位保持百分比文本。
            int maxIndex = values.ToList().IndexOf(WaterCoolerBle.TopPumpProfile);
            Assert.True(maxIndex >= 0, "泵速档位表缺少最高档");
            Assert.Equal("最大", texts[maxIndex]);
            Assert.Single(texts, t => t == "最大");
            foreach (string text in texts.Where(t => t != "自动" && t != "最大"))
            {
                Assert.Matches("^\\d+%$", text);
            }
            Assert.DoesNotContain(WaterCoolerBle.ProfileUnset, values);
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousReportedCapabilities, previousHardware!);
        }
    }

    [Fact]
    public void FanOptions_ArePercentOnlyWithoutPlaceholder()
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            ComboBox fan = form.Controls.Find("comboLcFan", true).OfType<ComboBox>().Single();
            (IReadOnlyList<string> texts, IReadOnlyList<int> values) = ReadComboItems(fan);

            Assert.All(texts, t => Assert.DoesNotContain("档位", t));
            Assert.All(texts, t => Assert.DoesNotContain("低", t));
            Assert.All(texts, t => Assert.DoesNotContain("中", t));
            // 2026-09-13 用户要求：最高档显示「最大」而非百分比，其余档位保持百分比文本。
            int maxIndex = values.ToList().IndexOf(WaterCoolerBle.TopFanProfile);
            Assert.True(maxIndex >= 0, "风扇档位表缺少最高档");
            Assert.Equal("最大", texts[maxIndex]);
            Assert.Single(texts, t => t == "最大");
            foreach (string text in texts.Where(t => t != "自动" && t != "最大"))
            {
                Assert.Matches("^\\d+%$", text);
            }
            Assert.DoesNotContain(WaterCoolerBle.ProfileUnset, values);
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousReportedCapabilities, previousHardware!);
        }
    }

    [Fact]
    public void PumpMaxGear_DisplaysMaximumLabel()
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            ComboBox pump = form.Controls.Find("comboLcPump", true).OfType<ComboBox>().Single();
            (IReadOnlyList<string> texts, IReadOnlyList<int> values) = ReadComboItems(pump);

            int maxIndex = values.ToList().IndexOf(WaterCoolerBle.TopPumpProfile);
            Assert.True(maxIndex >= 0, "泵速档位表缺少最高档");
            Assert.Equal("最大", texts[maxIndex]);
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousReportedCapabilities, previousHardware!);
        }
    }

    [Fact]
    public void FanMaxGear_DisplaysMaximumLabel()
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            using var form = BuildAuditForm();
            ComboBox fan = form.Controls.Find("comboLcFan", true).OfType<ComboBox>().Single();
            (IReadOnlyList<string> texts, IReadOnlyList<int> values) = ReadComboItems(fan);

            int maxIndex = values.ToList().IndexOf(WaterCoolerBle.TopFanProfile);
            Assert.True(maxIndex >= 0, "风扇档位表缺少最高档");
            Assert.Equal("最大", texts[maxIndex]);
        }
        finally
        {
            RestoreHarness(previousAuditMode, previousReportedCapabilities, previousHardware!);
        }
    }
}
