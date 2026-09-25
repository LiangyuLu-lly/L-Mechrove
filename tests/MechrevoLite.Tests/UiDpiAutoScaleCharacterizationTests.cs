using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// Characterization of D() vs RForm AutoScaleMode.Dpi.
///
/// D(n) is ResponsiveLayout.LogicalToDevice, which reads DeviceDpi (via UiDpi.Layout).
/// RForm also sets AutoScaleMode.Dpi / AutoScaleDimensions 96×96. Production scaling
/// is not changed here: these tests pin current behavior, including ctor-before-handle.
/// </summary>
public class UiDpiAutoScaleCharacterizationTests
{
    sealed class ProbeForm : RForm
    {
        public int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);
    }

    static int ExpectedFromDeviceDpi(Control control, int n) =>
        (int)Math.Round(n * Math.Max(UiDpi.Baseline, control.DeviceDpi) / (float)UiDpi.Baseline);

    static int InvokeCustomModeD(CustomModeForm form, int n)
    {
        MethodInfo method = typeof(CustomModeForm).GetMethod(
            "D", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CustomModeForm.D(int) not found.");
        return (int)method.Invoke(form, [n])!;
    }

    static IDisposable UseAuditMode()
    {
        bool previous = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        return new AuditScope(previous, previousHardware);
    }

    sealed class AuditScope(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware;
        }
    }

    [Fact]
    public void AfterHandleCreated_D_UsesDeviceDpi()
    {
        using var form = new ProbeForm();
        _ = form.Handle;
        Assert.True(form.IsHandleCreated);

        Assert.Equal(ExpectedFromDeviceDpi(form, 100), form.D(100));
        Assert.Equal(ExpectedFromDeviceDpi(form, 26), form.D(26));
        Assert.Equal(UiDpi.Layout(form), Math.Max(UiDpi.Baseline, form.DeviceDpi));
    }

    [Fact]
    public void CustomModeForm_AfterHandleCreated_D_UsesDeviceDpi()
    {
        using var audit = UseAuditMode();
        using var form = new CustomModeForm();
        Assert.NotEqual(IntPtr.Zero, form.Handle);
        Assert.True(form.IsHandleCreated);

        Assert.Equal(ExpectedFromDeviceDpi(form, 100), InvokeCustomModeD(form, 100));
        Assert.Equal(ResponsiveLayout.LogicalToDevice(form, 100), InvokeCustomModeD(form, 100));
    }

    [Fact]
    public void RForm_AutoScaleMode_IsDpi_With96Baseline_BeforeHandle()
    {
        using var form = new ProbeForm();
        Assert.False(form.IsHandleCreated);
        Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
        Assert.Equal(new SizeF(UiDpi.Baseline, UiDpi.Baseline), form.AutoScaleDimensions);
    }

    [Fact]
    public void CtorBeforeHandle_D_UsesCurrentDeviceDpi()
    {
        using var form = new ProbeForm();
        Assert.False(form.IsHandleCreated);
        Assert.Equal(ExpectedFromDeviceDpi(form, 100), form.D(100));
    }

    [Fact]
    public void AutoScaleMode_StaysDpi_AfterHandleCreated_DStillUsesDeviceDpi()
    {
        using var form = new ProbeForm();
        _ = form.Handle;
        Assert.True(form.IsHandleCreated);
        Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
        Assert.Equal(ExpectedFromDeviceDpi(form, 50), form.D(50));
    }
}
