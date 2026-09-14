using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

public class HeaderAlignmentTests
{
    static SettingsForm BuildForm()
    {
        var form = new SettingsForm();
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.PerformLayout();
        return form;
    }

    static Control GetHeaderIcon(Control header)
    {
        // BuildHeadRow 把图标放在第 0 列；电池/显卡手写头也遵循「图标在第 0 列」的约定。
        return header.Controls.OfType<PictureBox>().First();
    }

    [Fact]
    public void AllZoneHeaders_UseCanonicalIconColumnAndMargin()
    {
        using var form = BuildForm();

        var headers = new[]
        {
            form.Controls.Find("labelPerf", true).OfType<Label>().Single().Parent!,
            form.Controls.Find("labelGPU", true).OfType<Label>().Single().Parent!,
            form.Controls.Find("labelBatteryTitle", true).OfType<Label>().Single().Parent!,
            form.Controls.Find("labelScreenRow", true).OfType<Label>().Single().Parent!,
        };

        foreach (Control header in headers)
        {
            var table = Assert.IsType<BufferedTableLayoutPanel>(header);
            Assert.Equal(SizeType.Absolute, table.ColumnStyles[0].SizeType);
            Assert.Equal(ResponsiveLayout.LogicalToDevice(form, 26), table.ColumnStyles[0].Width);

            Control icon = GetHeaderIcon(header);
            Assert.Equal(DockStyle.Fill, icon.Dock);
            Assert.Equal(Padding.Empty, icon.Margin);
        }
    }

    [Fact]
    public void AllZoneTitles_UseTheSameTitleFont()
    {
        using var form = BuildForm();

        Label screenTitle = form.Controls.Find("labelScreenRow", true).OfType<Label>().Single();
        Label perfTitle = form.Controls.Find("labelPerf", true).OfType<Label>().Single();

        Assert.Equal(perfTitle.Font, screenTitle.Font);
    }

    [Fact]
    public void ScreenHeader_UsesCanonicalCardPadding()
    {
        using var form = BuildForm();

        Panel brightness = form.Controls.Find("panelBrightness", true).OfType<Panel>().Single();
        Panel performance = form.Controls.Find("panelPerformance", true).OfType<Panel>().Single();

        Assert.Equal(performance.Padding.Left, brightness.Padding.Left);
    }
}
