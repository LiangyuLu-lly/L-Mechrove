using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// The first-run guide's primary button said "go to the system page" and called
/// <c>SelectDashboardPage(2)</c>, which has an empty body. The dashboard is one page
/// (<see cref="SettingsForm.DashboardPageCount"/>), so that navigation cannot work.
/// </summary>
public class FirstRunGuideNavigationTests
{
    [Fact]
    public void TheGuideDoesNotPromiseAMissingSystemPage()
    {
        using var guide = new FirstRunGuideForm();
        guide.CreateControl();

        var texts = AllTexts(guide).ToArray();
        Assert.DoesNotContain(texts, text => text.Contains("系统页", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => text.Contains("“系统”页", StringComparison.Ordinal));

        Button primary = AllButtons(guide).Single(button => button.DialogResult == DialogResult.OK);
        Assert.False(string.IsNullOrWhiteSpace(primary.Text));
        Assert.Equal(DialogResult.OK, primary.DialogResult);
    }

    [Fact]
    public void ShowFirstRunGuide_DoesNotCallSelectDashboardPage()
    {
        string source = ChargeLimitGatingTests.SourceFile("Settings.cs");
        Assert.DoesNotContain("SelectDashboardPage(", source, StringComparison.Ordinal);
        Assert.Equal(1, new SettingsForm().DashboardPageCount);
    }

    static IEnumerable<string> AllTexts(Control root)
    {
        if (!string.IsNullOrEmpty(root.Text)) yield return root.Text;
        foreach (Control child in root.Controls)
        {
            foreach (string text in AllTexts(child)) yield return text;
        }
    }

    static IEnumerable<Button> AllButtons(Control root)
    {
        if (root is Button button) yield return button;
        foreach (Control child in root.Controls)
        {
            foreach (Button nested in AllButtons(child)) yield return nested;
        }
    }
}
