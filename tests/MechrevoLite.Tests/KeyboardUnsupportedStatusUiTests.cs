using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// T4 键盘控制器状态行（设计 §5）：标签 <c>labelKeyboardControllerStatus</c> 位于键盘块内；
/// 仅确定性「不支持」时可见并给出准确文案，Supported/Unknown 隐藏（现有用户布局不变）；
/// 审计模式因审计可见（布局演练）且**绝不**从 UI 启动探测。键盘行本身始终保留。
/// </summary>
public class KeyboardUnsupportedStatusUiTests
{
    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>确定性 HID：只用于把判定驱动到 Supported，不触真实设备。</summary>
    sealed class FakeKeyboardHid : HidDeviceWin
    {
        public override bool Open() => true;
        public override bool SetFeature(byte[] report) => true;
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }
    }

    /// <summary>Program 静态态 + 临时配置目录的保存/恢复脚手架。</summary>
    sealed class Scope : IDisposable
    {
        readonly string _directory;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly bool _previousAudit;
        readonly bool _previousReported;
        readonly string? _previousLightDir;

        public readonly KeyboardRgb Keyboard;
        public readonly MechrevoHw Hardware;

        public Scope(bool auditMode, bool useReportedCapabilities = false)
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            Hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities { Keyboard = true });
            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"));

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousAudit = Program.UiAuditMode;
            _previousReported = Program.UiAuditUseReportedCapabilities;

            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
            Program.UiAuditMode = auditMode;
            Program.UiAuditUseReportedCapabilities = useReportedCapabilities;
        }

        public void Dispose()
        {
            Program.rgb = _previousRgb!;
            Program.service = _previousService!;
            Program.hw = _previousHardware!;
            Program.UiAuditMode = _previousAudit;
            Program.UiAuditUseReportedCapabilities = _previousReported;
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousLightDir);
            Keyboard.Dispose();
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    /// <summary>把判定驱动成确定性结论（不触真实设备；Unsupported = 无候选）。</summary>
    static async Task SetVerdictAsync(KeyboardRgb keyboard, FeatureAvailability verdict)
    {
        keyboard.ResolveDeviceProbe = verdict == FeatureAvailability.Supported
            ? () => new FakeKeyboardHid()
            : () => null;
        await keyboard.EnsureHidReadyAsync();
        Assert.Equal(verdict, keyboard.ControllerAvailability);
    }

    /// <summary>Show 一次让 Visible 语义生效（祖先不可见时子控件 Visible 恒为 false），再读数。</summary>
    static Label ShowAndFindStatusLabel(SettingsForm form)
    {
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();
        return form.Controls.Find("labelKeyboardControllerStatus", true).OfType<Label>().Single();
    }

    [Fact]
    public async Task Unsupported_ShowsTheStatusLineWithTheExactTextAndKeepsTheKeyboardRow()
    {
        using var scope = new Scope(auditMode: false);
        await SetVerdictAsync(scope.Keyboard, FeatureAvailability.Unsupported);
        using var form = new SettingsForm();

        Label status = ShowAndFindStatusLabel(form);
        var keyboardRow = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();

        Assert.Equal(SettingsForm.KeyboardControllerUnsupportedText, status.Text);
        Assert.Equal("本机控制器不支持软件灯效控制，已改用官方通道（仅电源与亮度）", status.Text);
        Assert.True(status.Visible, "确定性「不支持」时状态行必须可见。");
        Assert.True(keyboardRow.Visible, "不支持时机型仍保留键盘行可见（定案 4）。");

        // 状态行紧跟键盘行：同一内容体内，行号紧邻。
        var body = Assert.IsAssignableFrom<TableLayoutPanel>(keyboardRow.Parent);
        Assert.Equal(body.GetCellPosition(keyboardRow).Row + 1, body.GetCellPosition(status).Row);
    }

    [Fact]
    public async Task Supported_HidesTheStatusLine()
    {
        using var scope = new Scope(auditMode: false);
        await SetVerdictAsync(scope.Keyboard, FeatureAvailability.Supported);
        using var form = new SettingsForm();

        Label status = ShowAndFindStatusLabel(form);

        Assert.False(status.Visible, "Supported 机型不得出现状态行（现有用户布局不变）。");
    }

    [Fact]
    public void Unknown_HidesTheStatusLine()
    {
        using var scope = new Scope(auditMode: false);
        using var form = new SettingsForm();

        Label status = ShowAndFindStatusLabel(form);

        Assert.False(status.Visible, "判定未定（Unknown）按现状处理：不得出现状态行。");
    }

    [Fact]
    public void AuditMode_ShowsTheStatusLineAndNeverStartsTheProbe()
    {
        using var scope = new Scope(auditMode: true);   // audit = UiAuditMode 且未采用上报能力（Settings.cs:2793）
        using var form = new SettingsForm();

        Label status = ShowAndFindStatusLabel(form);

        Assert.True(status.Visible, "审计模式下状态行因审计可见（布局演练，不碰硬件）。");
        Assert.Equal(0, scope.Keyboard.ControllerProbeCount);   // 绝不从 UI 线程启动探测
        Assert.Equal(FeatureAvailability.Unknown, scope.Keyboard.ControllerAvailability);
    }
}
