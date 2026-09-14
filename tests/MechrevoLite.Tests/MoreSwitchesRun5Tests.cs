using MechrevoLite.Hardware;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// Run5「更多开关」两项新入口的回归测试：
///
/// 1. 深度睡眠（官方 DeepSleepSwitch，Setting/Control DEEPSLEEP_ON/OFF）——真机实测命令下发后
///    Setting/Status 的回读要等重启才更新（FunctionVerifier 归为「需重启」，官方界面同款提示），
///    所以用户的选择持久化到 <c>deepsleep_pending</c>，开关打开时必须显示硬件回读的真实状态
///    （有待生效改动时显示改动值），不能停在硬编码默认。
/// 2. 仅关闭显示器——一次性动作：向 HWND_BROADCAST 广播 WM_SYSCOMMAND/SC_MONITORPOWER(2)，
///    执行后回到 OFF。真实广播在 <c>NativeMethods.MonitorOffSender</c> 接缝后面，测试只记录参数。
/// </summary>
public class MoreSwitchesRun5Tests
{
    const string DeepSleepPendingKey = "deepsleep_pending";
    const int WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;
    const int MONITOR_OFF = 2;

    // ------------------------------------------------------------ 配置键

    /// <summary>
    /// (a) 新配置键的往返 / 默认值。默认必须是「未设置」(-1)：0 与 1 都是合法的待生效值，
    /// 用 0 当默认会把「没有待生效改动」误读成「已请求关闭」。
    /// </summary>
    [Fact]
    public void DeepSleepPendingKey_RoundTripsAndDefaultsToUnset()
    {
        try
        {
            AppConfig.Remove(DeepSleepPendingKey);
            Assert.Equal(-1, AppConfig.Get(DeepSleepPendingKey, -1));

            AppConfig.Set(DeepSleepPendingKey, 0);
            Assert.Equal(0, AppConfig.Get(DeepSleepPendingKey, -1));

            AppConfig.Set(DeepSleepPendingKey, 1);
            Assert.Equal(1, AppConfig.Get(DeepSleepPendingKey, -1));

            AppConfig.Remove(DeepSleepPendingKey);
            Assert.Equal(-1, AppConfig.Get(DeepSleepPendingKey, -1));
        }
        finally
        {
            AppConfig.Remove(DeepSleepPendingKey);
        }
    }

    /// <summary>
    /// 这些测试会真实调用 AppConfig；必须确认全局隔离生效，否则会写进真实用户配置。
    /// （隔离由 TestConfigIsolation 的模块初始化器设置，任何测试触碰 AppConfig 之前生效。）
    /// </summary>
    [Fact]
    public void ConfigStore_IsRedirectedToTempForTheTestRun()
    {
        string? overridePath = Environment.GetEnvironmentVariable("LMECHREVO_CONFIG_FILE");
        Assert.False(string.IsNullOrWhiteSpace(overridePath), "测试必须通过 LMECHREVO_CONFIG_FILE 隔离配置。");
        string full = Path.GetFullPath(overridePath!);
        Assert.StartsWith(Path.GetTempPath(), full, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 关屏接缝

    /// <summary>
    /// (b) 接缝必须收到精确的广播参数：HWND_BROADCAST + WM_SYSCOMMAND + SC_MONITORPOWER(2)。
    /// LParam=2 是「只关显示器」，不是睡眠/休眠/关机。
    /// </summary>
    [Fact]
    public void TurnOffScreen_BroadcastsMonitorPowerToAllTopLevelWindows()
    {
        Action<nint, int, int, int>? previous = NativeMethods.MonitorOffSender;
        var calls = new List<(nint Hwnd, int Message, int WParam, int LParam)>();
        try
        {
            NativeMethods.MonitorOffSender = (hwnd, message, wParam, lParam) => calls.Add((hwnd, message, wParam, lParam));
            NativeMethods.TurnOffScreen();
        }
        finally
        {
            NativeMethods.MonitorOffSender = previous;
        }

        var call = Assert.Single(calls);
        Assert.Equal(NativeMethods.HWND_BROADCAST, call.Hwnd);
        Assert.Equal(WM_SYSCOMMAND, call.Message);
        Assert.Equal(SC_MONITORPOWER, call.WParam);
        Assert.Equal(MONITOR_OFF, call.LParam);
    }

    // ------------------------------------------------------------ 关屏守卫链
    //
    // 「仅关闭显示器」是全局、不可逆、自动化无法恢复的副作用：SC_MONITORPOWER 会灭掉整块屏幕，
    // 而程序化/自动化输入唤不醒它。守卫链（见 Settings.cs 的 monitoroff 分支）：
    //   1) 事件是 Click，不是 CheckedChanged —— 程序化写 Checked（含启动回读）不会触发 Click；
    //   2) _syncingSwitches 期间直接返回（回显同步不触发动作，纵深防御）；
    //   3) 只有 OFF→ON 的翻转才动作（!cb.Checked 直接返回）；
    //   4) GetLastInputInfo 显示刚刚有真实输入（≈500ms 窗口）才放行；
    //   5) 无论是否广播都回弹 OFF、不落配置。
    // 下列测试全程走 NativeMethods.MonitorOffSender / IdleTimeProvider 接缝：只记录参数，
    // 绝不真灭屏，也绝不产生真实输入。

    /// <summary>
    /// 程序化赋值 Checked（等价于启动回读/任意自动化写）绝不能广播。这也是「Checked= 不触发
    /// Click」的机器证据：它只触发 CheckedChanged，而监听在 Click 上，所以一次广播都没有。
    /// </summary>
    [Fact]
    public void ProgrammaticCheckedAssignment_DoesNotBroadcastMonitorOff()
    {
        WithMonitorOffSeams(calls =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            Assert.False(box.Checked, "仅关闭显示器不是持久开关，初始必须是 OFF。");
            box.Checked = true;    // 程序化写入：绝不能触发关屏
            box.Checked = false;
            box.Checked = true;

            Assert.Empty(calls);
        });
    }

    /// <summary>
    /// 启动/定时回显路径（<see cref="SettingsForm"/> 的 UpdateQuickSwitches：内部置
    /// _syncingSwitches=true 并把硬件回读值写进 Checked）必须对关屏开关完全惰性。
    /// 最坏情形是「每次启动都灭一次屏」——这正是本次事故的形态，必须锁死。
    /// </summary>
    [Fact]
    public void SwitchSyncAndReadBack_NeverBroadcastMonitorOff()
    {
        using var hardware = new MechrevoHw(null, MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
        // 故意让回读里出现 monitoroff=true（生产不会写这个键，但守卫必须对任何 Checked= 写入惰性）。
        hardware.QuickSwitches["monitoroff"] = true;

        WithMonitorOffSeams(calls =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);
            Assert.False(box.Checked);

            typeof(SettingsForm)
                .GetMethod("UpdateQuickSwitches", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null);

            Assert.Empty(calls);
        }, hardware: hardware);
    }

    /// <summary>
    /// 真实点击 + 新鲜输入（GetLastInputInfo 显示刚刚有输入）→ 精确广播一次并回弹 OFF。
    /// </summary>
    [Fact]
    public void GenuineUserClick_BroadcastsExactlyOnceAndRevertsToOff()
    {
        WithMonitorOffSeams(calls =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);
            Assert.False(box.Checked);

            RaiseClick(box);

            var call = Assert.Single(calls);
            Assert.Equal(NativeMethods.HWND_BROADCAST, call.Hwnd);
            Assert.Equal(WM_SYSCOMMAND, call.Message);
            Assert.Equal(SC_MONITORPOWER, call.WParam);
            Assert.Equal(MONITOR_OFF, call.LParam);
            Assert.False(box.Checked, "一次性动作执行后必须回到 OFF（不落配置、不回弹）。");
        }, idle: static () => TimeSpan.Zero);
    }

    /// <summary>
    /// 陈旧输入（自动化/UIA 点击的典型形态：最近一次真实输入在两秒前）与输入缺失都不能广播；
    /// 末尾的新鲜输入对照证明空结果来自新鲜度判定，而不是点击根本没投递。
    /// </summary>
    [Fact]
    public void StaleOrMissingRealInput_DoesNotBroadcast()
    {
        WithMonitorOffSeams(calls =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            NativeMethods.IdleTimeProvider = static () => TimeSpan.FromMilliseconds(2000);
            RaiseClick(box);
            Assert.Empty(calls);
            Assert.False(box.Checked, "被拒绝的一次性动作也必须回到 OFF，不能停在半开。");

            NativeMethods.IdleTimeProvider = static () => TimeSpan.MaxValue;   // 读不到输入
            RaiseClick(box);
            Assert.Empty(calls);

            NativeMethods.IdleTimeProvider = static () => TimeSpan.Zero;       // 反向对照：新鲜输入放行
            RaiseClick(box);
            Assert.Single(calls);
        });
    }

    /// <summary>
    /// (b) 开关语义：真实点击拨到 ON 触发一次动作后自动回到 OFF（它是动作，不是持久状态），
    /// 且第二次真实点击仍能再执行（一次性动作可重复，不是被禁用）。
    /// </summary>
    [Fact]
    public void MonitorOffSwitch_ExecutesTheBroadcastThenRevertsToOff()
    {
        WithMonitorOffSeams(calls =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            Assert.False(box.Checked, "仅关闭显示器不是持久开关，初始必须是 OFF。");
            RaiseClick(box);
            Assert.Single(calls);
            Assert.False(box.Checked, "执行后必须回到 OFF（一次性动作）。");

            RaiseClick(box);
            Assert.Equal(2, calls.Count);
            Assert.False(box.Checked);
        }, idle: static () => TimeSpan.Zero);
    }

    // ------------------------------------------------------------ 关屏测试脚手架

    /// <summary>
    /// 注入关屏接缝并托管全局状态（审计模式、hw、两个静态接缝），结束时全部还原——
    /// 套件已禁用并行，但静态状态仍必须还原以免污染后续测试。广播只进内存记录器，绝不真灭屏。
    /// </summary>
    static void WithMonitorOffSeams(
        Action<List<(nint Hwnd, int Message, int WParam, int LParam)>> body,
        Func<TimeSpan>? idle = null,
        MechrevoHw? hardware = null)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Action<nint, int, int, int>? previousSender = NativeMethods.MonitorOffSender;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        var calls = new List<(nint Hwnd, int Message, int WParam, int LParam)>();
        try
        {
            Program.UiAuditMode = true;
            Program.hw = hardware!;
            NativeMethods.MonitorOffSender = (hwnd, message, wParam, lParam) => calls.Add((hwnd, message, wParam, lParam));
            NativeMethods.IdleTimeProvider = idle;
            body(calls);
        }
        finally
        {
            NativeMethods.IdleTimeProvider = previousIdle;
            NativeMethods.MonitorOffSender = previousSender;
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    static CheckBox MonitorOffBox(SettingsForm form) =>
        form.Controls.Find("quick_monitoroff", true).OfType<CheckBox>().Single();

    /// <summary>
    /// 投递一次真实 Click（与 PerformClick 同一条 CheckBox.OnClick → Click 路径：先按 AutoCheck
    /// 翻转 Checked 并触发 CheckedChanged，再触发 Click）。用反射而非 PerformClick，是为了不依赖
    /// 窗体可见性——PerformClick 的 CanSelect 要求整条父链 Visible，而本仓库的 SettingsForm
    /// 测试历来只 CreateControl，不在测试里 Show 主窗体。
    /// </summary>
    static void RaiseClick(CheckBox box) =>
        typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(box, new object[] { EventArgs.Empty });

    // ------------------------------------------------------------ 深度睡眠初值

    /// <summary>
    /// 打开时必须显示硬件回读的真实状态（本机今天报 DEEPSLEEP_ON 的场景），
    /// 不能停在 CheckBox 的默认 false。
    /// </summary>
    [Fact]
    public void DeepSleepSwitch_ShowsTheReportedHardwareStateOnOpen()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        try
        {
            AppConfig.Remove(DeepSleepPendingKey);
            Program.UiAuditMode = true;
            using var hardware = new MechrevoHw(null, MechrevoDeviceCapabilities.FromValues(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
            hardware.HandleMessage("Setting/Status", """{"DeepSleepSwitch":"DEEPSLEEP_ON","DeepSleepTime":1800}""");
            Assert.True(hardware.SupportsQuickSwitch("deepsleep"), "前置条件：服务端报过 DeepSleepSwitch。");
            Program.hw = hardware;

            using var form = new SettingsForm();
            form.CreateControl();
            var box = form.Controls.Find("quick_deepsleep", true).OfType<CheckBox>().Single();

            Assert.True(box.Checked, "深度睡眠开关必须显示硬件回读的 ON，而不是默认未勾选。");
        }
        finally
        {
            AppConfig.Remove(DeepSleepPendingKey);
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// 有待重启生效的改动时（硬件回读还是旧值），开关显示用户最后一次选择——
    /// 这是 <c>deepsleep_pending</c> 存在的意义：重启前后都别把用户刚关掉的开关弹回来。
    /// </summary>
    [Fact]
    public void DeepSleepSwitch_ShowsThePendingChoiceWhileTheHardwareStillReportsTheOldValue()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        try
        {
            Program.UiAuditMode = true;
            using var hardware = new MechrevoHw(null, MechrevoDeviceCapabilities.FromValues(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
            hardware.HandleMessage("Setting/Status", """{"DeepSleepSwitch":"DEEPSLEEP_ON","DeepSleepTime":1800}""");
            Program.hw = hardware;

            AppConfig.Set(DeepSleepPendingKey, 0);   // 用户已请求关闭，等重启生效
            using var form = new SettingsForm();
            form.CreateControl();
            var box = form.Controls.Find("quick_deepsleep", true).OfType<CheckBox>().Single();

            Assert.False(box.Checked, "待生效的请求关闭必须压过硬件旧值（ON）。");
        }
        finally
        {
            AppConfig.Remove(DeepSleepPendingKey);
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAudit;
        }
    }
}
