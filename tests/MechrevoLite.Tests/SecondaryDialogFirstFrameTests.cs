using MechrevoLite.Hardware;
using MechrevoLite.UI;
using MechrevoLite.Update;
using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 二级对话框首帧几何回归（run5 UI hardening, item 4）。
///
/// 现象：内容派生尺寸/贴边定位在窗口可见之后才跑，首帧先画在默认位置（Manual + 未定位 = 屏幕
/// 左上角）再跳到主窗旁边。契约：Show 之前尺寸与位置都已最终；Shown（首个可见帧）之后不得再
/// 触发 LocationChanged / SizeChanged。
/// </summary>
public class SecondaryDialogFirstFrameTests
{
    static IDisposable UseAuditMode()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        return new Scoped(previousAudit, previousHardware);
    }

    sealed class Scoped(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware!;
        }
    }

    static Form NewOwner()
    {
        var owner = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(640, 320),
            Size = new Size(480, 600),
        };
        owner.Show();
        Application.DoEvents();
        return owner;
    }

    /// <summary>订阅 Shown/LocationChanged/SizeChanged：首个可见帧之后再动几何就判失败。</summary>
    static void AssertFirstFrameIsFinal(Form dialog, Action show)
    {
        Rectangle firstPaint = Rectangle.Empty;
        bool shown = false;
        bool changedAfterShown = false;
        string? change = null;
        dialog.Shown += (_, _) => { shown = true; firstPaint = dialog.Bounds; };
        dialog.LocationChanged += (_, _) =>
        {
            if (!shown) return;
            changedAfterShown = true;
            change ??= $"LocationChanged → {dialog.Location}";
        };
        dialog.SizeChanged += (_, _) =>
        {
            if (!shown) return;
            changedAfterShown = true;
            change ??= $"SizeChanged → {dialog.Size}";
        };

        show();
        Application.DoEvents();

        Assert.True(shown, "对话框必须已显示。");
        Assert.False(changedAfterShown, "首帧之后不得再改 Location/Size：" + change);
        Assert.Equal(firstPaint, dialog.Bounds);
    }

    static void AssertAdjacentTo(Form dialog, Form owner)
    {
        Assert.True(dialog.Left < owner.Left || dialog.Left >= owner.Right,
            $"对话框必须贴在主窗旁边（dialog={dialog.Location}, owner={owner.Location}）。");
        Assert.NotEqual(Point.Empty, dialog.Location);
    }

    [Fact]
    public void LightForm_IsPositionedBeforeShow_AndNeverMovesAfterFirstPaint()
    {
        using var _ = UseAuditMode();
        using var owner = NewOwner();
        using var dialog = new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects);

        AssertFirstFrameIsFinal(dialog, () => ResponsiveLayout.ShowAdjacentTo(dialog, owner));
        AssertAdjacentTo(dialog, owner);
    }

    [Fact]
    public void RgbForm_IsSizedAndPositionedBeforeShow()
    {
        using var _ = UseAuditMode();
        using var owner = NewOwner();
        using var dialog = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());

        AssertFirstFrameIsFinal(dialog, () => ResponsiveLayout.ShowAdjacentTo(dialog, owner));
        AssertAdjacentTo(dialog, owner);
    }

    [Fact]
    public void SettingsDialog_IsSizedAndPositionedBeforeShow()
    {
        using var _ = UseAuditMode();
        using var owner = NewOwner();
        var overdrive = new RCheckBox { Text = "响应加速" };
        using var dialog = new SettingsDialog(new Panel(), new Panel(), overdrive, displayGroupAvailable: true);

        AssertFirstFrameIsFinal(dialog, () => dialog.ShowAdjacentTo(owner));
        AssertAdjacentTo(dialog, owner);
    }

    [Fact]
    public void UpdateForm_ShrinksToEmptyStateBeforeFirstPaint()
    {
        using var _ = UseAuditMode();
        using var form = new UpdateForm(new UpdateInfo(
            CurrentVersion: "0.0.0-test", LatestVersion: "9.9.9-test", Channel: "test", ChannelFallback: false,
            UpdateAvailable: false, ReleaseDate: null, Notes: null, FileName: null, Size: null,
            Sha256: null, DownloadUrl: null, DownloadPage: null));

        AssertFirstFrameIsFinal(form, form.Show);
    }
}
