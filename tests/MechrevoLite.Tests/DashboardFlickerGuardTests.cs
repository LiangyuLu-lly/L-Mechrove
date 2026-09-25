namespace MechrevoLite.Tests;

/// <summary>
/// Characterization of the dashboard flicker guards: fingerprint before any Visible
/// write, no nested BeginInvoke storm, ArrangeDashboard only when the stack signature
/// changes, telemetry layout skipped when text is unchanged.
/// </summary>
public class DashboardFlickerGuardTests
{
    static string SettingsSource() => GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
    static string SettingsV2() => GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.V2.cs");
    static string Buffered() => GcuInstallerHarness.Read("src", "MechrevoLiteWin", "UI", "BufferedLayoutControls.cs");
    static string Designer() => GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.Designer.cs");

    static string MethodBody(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, startMarker);
        return source[start..end];
    }

    [Fact]
    public void RefreshDeviceCapabilitiesComputesTheFingerprintBeforeAnyVisibleWrite()
    {
        string body = MethodBody(SettingsSource(),
            "public void RefreshDeviceCapabilities()",
            "void ApplyUnsupportedModelNotice");
        int fingerprint = body.IndexOf("if (fingerprint == _lastCapabilityLayout)", StringComparison.Ordinal);
        int visibleWrite = body.IndexOf(".Visible =", StringComparison.Ordinal);
        Assert.True(fingerprint >= 0, "fingerprint early-return missing");
        Assert.True(visibleWrite >= 0, "Visible write missing");
        Assert.True(fingerprint < visibleWrite,
            "RefreshDeviceCapabilities must compute the fingerprint before any Visible write.");
    }

    [Fact]
    public void RefreshDeviceCapabilitiesCoalescesBeginInvoke()
    {
        string body = MethodBody(SettingsSource(),
            "public void RefreshDeviceCapabilities()",
            "void ApplyUnsupportedModelNotice");
        Assert.Contains("_refreshCapsQueued", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvoke(RefreshDeviceCapabilities)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrangeDashboardIsNotForcedWhenTheStackSignatureIsUnchanged()
    {
        string settings = SettingsSource();
        Assert.DoesNotContain("ArrangeDashboard(force: true)", settings, StringComparison.Ordinal);
        string arrange = MethodBody(settings, "private void ArrangeDashboard(", "void UpdateDashboardWindowHeight");
        Assert.Contains("ResumeLayout(false)", arrange, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateTelemetryTextSkipsLayoutWhenTextIsUnchanged()
    {
        string body = MethodBody(SettingsV2(), "internal void UpdateTelemetryText()", "static bool SetTelemetryPiece");
        Assert.Contains("LayoutTelemetryPieces()", body, StringComparison.Ordinal);
        Assert.Contains("changed", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetrySizeChangedLayoutsOnlyWhenMeasuredSizeChanges()
    {
        string body = MethodBody(SettingsV2(), "void BuildTelemetryRow()", "Label MakeTelemetryPiece");
        int sizeChanged = body.IndexOf("SizeChanged", StringComparison.Ordinal);
        int layout = body.IndexOf("LayoutTelemetryPieces()", sizeChanged, StringComparison.Ordinal);
        Assert.True(sizeChanged >= 0 && layout > sizeChanged, "SizeChanged must call LayoutTelemetryPieces");
        string handler = body[sizeChanged..layout];
        Assert.Contains("Width", handler, StringComparison.Ordinal);
        Assert.Contains("Height", handler, StringComparison.Ordinal);
        Assert.Contains("==", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void SetScreenRowControlsDoesNotPairEmptySuspendResume()
    {
        string body = MethodBody(SettingsSource(),
            "void SetScreenRowControls(int row, bool visible, params Control[] controls)",
            "static bool ReflowSingleRow");
        Assert.DoesNotContain("SuspendLayout()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumeLayout(true)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BufferedLayoutControlsUseDoubleBufferStylesAndSwallowEraseBackground()
    {
        string source = Buffered();
        Assert.Contains("ControlStyles.UserPaint", source, StringComparison.Ordinal);
        Assert.Contains("ControlStyles.AllPaintingInWmPaint", source, StringComparison.Ordinal);
        Assert.Contains("ControlStyles.OptimizedDoubleBuffer", source, StringComparison.Ordinal);
        Assert.Contains("0x0014", source, StringComparison.Ordinal);
        Assert.Contains("ResizeRedraw = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignerPerfGpuScreenTablesUseBufferedLayoutControls()
    {
        string designer = Designer();
        Assert.Contains("tablePerf = new BufferedTableLayoutPanel()", designer, StringComparison.Ordinal);
        Assert.Contains("tableGPU = new BufferedTableLayoutPanel()", designer, StringComparison.Ordinal);
        Assert.Contains("tableScreen = new BufferedTableLayoutPanel()", designer, StringComparison.Ordinal);
        Assert.Contains("panelCPUTitle = new BufferedPanel()", designer, StringComparison.Ordinal);
    }
}
