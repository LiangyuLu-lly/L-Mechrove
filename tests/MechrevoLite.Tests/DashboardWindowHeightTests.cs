namespace MechrevoLite.Tests;

public class DashboardWindowHeightTests
{
    [Fact]
    public void CompactDashboardLogicalClientSize_HeightIs529()
    {
        // DESIGN.md v2 §5：紧凑仪表盘逻辑客户区高度应收敛到 529（当前 660 是旧双列断点遗留）。
        Assert.Equal(529, SettingsForm.CompactDashboardLogicalClientSize.Height);
    }
}
