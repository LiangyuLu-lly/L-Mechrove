using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;
using Xunit;
using static MechrevoLite.UI.GcuConnectionStatus;

namespace MechrevoLite.Tests;

/// <summary>
/// GCU 连接状态指示条（run7）：状态解析、状态→（色/文/tooltip）映射、非仅色差、
/// 以及指示条随硬件快照变化而更新（UpdateGcuStatus 即 timer/ConnectionReady 共用的刷新路径）。
/// </summary>
public class GcuConnectionStatusTests
{
    // ---- Resolve：真实硬件快照 → 状态 ----

    [Theory]
    [InlineData(false, false, false, false, (int)GcuConnectionState.Unknown)]      // hw 未初始化
    [InlineData(true, true, false, true, (int)GcuConnectionState.Connected)]       // 已连接
    [InlineData(true, false, true, false, (int)GcuConnectionState.Connecting)]     // 首连重试中（服务开机晚于登录）
    [InlineData(true, false, true, true, (int)GcuConnectionState.Disconnected)]    // 曾连上后断线（服务被停）
    [InlineData(true, false, false, false, (int)GcuConnectionState.Disconnected)]  // 从未连上且不在重试
    [InlineData(true, false, false, true, (int)GcuConnectionState.Disconnected)]   // 曾连上、重连循环已停
    public void Resolve_MapsRealHardwareSnapshots(
        bool hardwarePresent, bool connected, bool reconnecting, bool everConnected,
        int expected)
    {
        Assert.Equal((GcuConnectionState)expected,
            Resolve(hardwarePresent, connected, reconnecting, everConnected));
    }

    [Fact]
    public void Resolve_ServiceStoppedAfterConnected_GoesDisconnectedNotConnecting()
    {
        // 服务停止的实机序列：IsConnected 翻 false、重连循环起飞、ConnectionGeneration>0。
        // 必须报「未连接」而不是「连接中」——用户需要知道服务此刻不可用。
        Assert.Equal(GcuConnectionState.Connected, Resolve(true, true, false, true));
        Assert.Equal(GcuConnectionState.Disconnected, Resolve(true, false, true, true));
    }

    // ---- Describe：状态 →（前景色，文本，tooltip）----

    [Fact]
    public void Describe_EveryStateHasDistinctTextAndTooltip()
    {
        var states = new[] { GcuConnectionState.Unknown, GcuConnectionState.Connected,
            GcuConnectionState.Connecting, GcuConnectionState.Disconnected };
        var texts = states.Select(s => Describe(s).Text).ToHashSet();
        var tooltips = states.Select(s => Describe(s).Tooltip).ToHashSet();

        // 非仅色差：每个状态的文本标签互不相同、tooltip 互不相同且非空。
        Assert.Equal(states.Length, texts.Count);
        Assert.Equal(states.Length, tooltips.Count);
        foreach (var state in states)
        {
            var (fore, text, tooltip) = Describe(state);
            Assert.True(fore.A > 0);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.False(string.IsNullOrWhiteSpace(tooltip));
            Assert.Contains("GCU", text);
        }
    }

    [Fact]
    public void Describe_RealStatesTooltipMentionsBrokerEndpoint()
    {
        foreach (var state in new[] { GcuConnectionState.Connected, GcuConnectionState.Connecting,
            GcuConnectionState.Disconnected })
        {
            Assert.Contains("13688", Describe(state).Tooltip);
        }
    }

    [Fact]
    public void Describe_MapsStatesToDistinctSemanticColors()
    {
        Color connected = Describe(GcuConnectionState.Connected).Fore;
        Color connecting = Describe(GcuConnectionState.Connecting).Fore;
        Color disconnected = Describe(GcuConnectionState.Disconnected).Fore;
        Color unknown = Describe(GcuConnectionState.Unknown).Fore;

        Assert.Equal(UiVisualStyle.Ok, connected);
        Assert.Equal(UiVisualStyle.Warn, connecting);
        Assert.Equal(UiVisualStyle.Danger, disconnected);
        Assert.Equal(UiVisualStyle.Muted, unknown);
        Assert.Equal(4, new[] { connected, connecting, disconnected, unknown }
            .Select(c => c.ToArgb()).ToHashSet().Count);
    }

    // ---- 指示条本体：随快照更新、非仅色差、位于页面栈首行 ----

    static SettingsForm BuildForm()
    {
        var form = new SettingsForm();
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.PerformLayout();
        return form;
    }

    [Fact]
    public void Indicator_RendersTextLabelForEveryState_NotColourOnly()
    {
        MechrevoHw? previous = Program.hw;
        try
        {
            using var form = BuildForm();
            var label = form.Controls.Find("labelGcuStatus", true).OfType<Label>().Single();

            var rendered = new HashSet<string>();
            // 可用真实 MechrevoHw 构造的三个快照：hw=null（未知）、注入后端（已连接）、
            // 无后端且未重连（未连接）。「连接中」态由 Resolve/Describe 单测覆盖
            //（IsReconnecting 只在真实重连循环里翻转，无法在单测内驱动）。
            foreach (MechrevoHw? hw in new MechrevoHw?[]
                     {
                         null,
                         new MechrevoHw((_, _) => Task.CompletedTask),
                         new MechrevoHw(default(Func<string, object, Task>)),
                     })
            {
                Program.hw = hw;
                form.UpdateGcuStatus();
                rendered.Add(label.Text);
                Assert.False(string.IsNullOrWhiteSpace(label.Text));
                Assert.Contains("GCU", label.Text);
                Assert.False(string.IsNullOrWhiteSpace(form.GcuStatusTooltip));
            }

            // 三个快照渲染出互不相同的文本（状态由文本承载，不只靠颜色）。
            Assert.Equal(3, rendered.Count);
        }
        finally
        {
            Program.hw = previous;
        }
    }

    [Fact]
    public void Indicator_SitsAtTopOfDashboardStack_AndIsInLayout()
    {
        using var form = BuildForm();
        var panel = form.Controls.Find("panelGcuStatus", true).OfType<BufferedPanel>().Single();
        var label = form.Controls.Find("labelGcuStatus", true).OfType<Label>().Single();
        var perf = form.Controls.Find("panelPerformance", true).First();

        Assert.Equal(panel, label.Parent);
        // 页面栈首行：在性能卡之上（滚动顶部恒可见；可见性由审计 + 真机截图证明）。
        var stack = panel.Parent!;
        Assert.Equal(panel, stack.Controls[0]);
        Assert.True(panel.Top <= perf.Top);
        Assert.True(panel.Height > 0);
        Assert.True(label.Height > 0);
    }
}
