using MechrevoLite.Display;
using MechrevoLite.Hardware;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// Run5「更多开关」两项新入口的回归测试：
///
/// 1. 深度睡眠（官方 DeepSleepSwitch，Setting/Control DEEPSLEEP_ON/OFF）——真机实测命令下发后
///    Setting/Status 的回读要等重启才更新（FunctionVerifier 归为「需重启」，官方界面同款提示），
///    所以用户的选择持久化到 <c>deepsleep_pending</c>，开关打开时必须显示硬件回读的真实状态
///    （有待生效改动时显示改动值），不能停在硬编码默认。
/// 2. 息屏（不睡眠）——一次性动作：把面板亮度压到最低做黑屏，同时用
///    <c>SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED)</c> 顶住
///    Modern Standby，执行后回到 OFF。真实亮度读写藏在 <see cref="ScreenBrightness.ReadOverride"/> /
///    <see cref="ScreenBrightness.WriteOverride"/> 接缝后面，测试只记录，绝不真改屏。
///
/// 真机事故（2026-09-14）：本机只报 S0 低功耗待机（无 S3），旧实现向 HWND_BROADCAST 广播
/// WM_SYSCOMMAND/SC_MONITORPOWER(2) 被 Windows 当成进入 Connected Standby，点一次开关就睡了。
/// 该类含静态扫描测试，锁死阻塞 SendMessage 广播路径不再存在。
/// </summary>
public class MoreSwitchesRun5Tests
{
    const string DeepSleepPendingKey = "deepsleep_pending";

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

    // ------------------------------------------------------------ 息屏守卫链
    //
    // 「息屏（不睡眠）」是全局、用户可见的副作用（屏幕变黑）。守卫链（见 Settings.cs 的 monitoroff 分支）：
    //   1) 事件是 Click，不是 CheckedChanged —— 程序化写 Checked（含启动回读）不会触发 Click；
    //   2) _syncingSwitches 期间直接返回（回显同步不触发动作，纵深防御）；
    //   3) 只有 OFF→ON 的翻转才动作（!cb.Checked 直接返回）；
    //   4) GetLastInputInfo 显示刚刚有真实输入（≈500ms 窗口）才放行；
    //   5) 无论是否息屏都回弹 OFF、不落配置。
    // 下列测试全程走 ScreenBrightness / ScreenBlankController / NativeMethods 接缝：只记录，绝不真改屏。

    /// <summary>
    /// 程序化赋值 Checked（等价于启动回读/任意自动化写）绝不能息屏。这也是「Checked= 不触发
    /// Click」的机器证据：它只触发 CheckedChanged，而监听在 Click 上，所以一次亮度写入都没有。
    /// </summary>
    [Fact]
    public void ProgrammaticCheckedAssignment_DoesNotDim()
    {
        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            Assert.False(box.Checked, "息屏不是持久开关，初始必须是 OFF。");
            box.Checked = true;    // 程序化写入：绝不能触发息屏
            box.Checked = false;
            box.Checked = true;

            Assert.Empty(writes);
            Assert.Empty(exec);
            Assert.False(ScreenBlankController.IsDimmed);
        });
    }

    /// <summary>
    /// 启动/定时回显路径（<see cref="SettingsForm"/> 的 UpdateQuickSwitches：内部置
    /// _syncingSwitches=true 并把硬件回读值写进 Checked）必须对息屏开关完全惰性。
    /// </summary>
    [Fact]
    public void SwitchSyncAndReadBack_NeverDims()
    {
        using var hardware = new MechrevoHw(null, MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
        // 故意让回读里出现 monitoroff=true（生产不会写这个键，但守卫必须对任何 Checked= 写入惰性）。
        hardware.QuickSwitches["monitoroff"] = true;

        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);
            Assert.False(box.Checked);

            typeof(SettingsForm)
                .GetMethod("UpdateQuickSwitches", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null);

            Assert.Empty(writes);
            Assert.Empty(exec);
            Assert.False(ScreenBlankController.IsDimmed);
        }, hardware: hardware);
    }

    /// <summary>
    /// 真实点击 + 新鲜输入 → 精确把面板压到 0，并设置执行状态标志、回弹 OFF；
    /// 首次真实输入（时钟越过最小黑屏时长、空闲窗口为 0）→ 恢复原亮度并清除标志。
    /// </summary>
    [Fact]
    public void GenuineClick_DimsAndSetsExecutionState_ThenRestoresOnInput()
    {
        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);
            Assert.False(box.Checked);

            RaiseClick(box);

            Assert.Equal(new[] { 0 }, writes);   // 只写入一次 0（黑屏）
            Assert.False(box.Checked, "一次性动作执行后必须回到 OFF。");
            Assert.True(ScreenBlankController.IsDimmed);
            uint state = Assert.Single(exec);
            Assert.Equal(ScreenBlankController.BlankExecutionState, state);
            Assert.True((state & ScreenBlankController.ES_SYSTEM_REQUIRED) != 0,
                "息屏期间必须置 ES_SYSTEM_REQUIRED 顶住 Modern Standby。");

            // 首次真实输入：空闲窗口为 0（idle 接缝），时钟越过最小黑屏时长。
            ScreenBlankController.Poll(DateTime.UtcNow.AddSeconds(2));

            Assert.Equal(new[] { 0, 50 }, writes);   // 恢复原亮度 50
            Assert.Equal(ScreenBlankController.ES_CONTINUOUS, exec[^1]);
            Assert.False(ScreenBlankController.IsDimmed);
        }, idle: static () => TimeSpan.Zero);
    }

    /// <summary>
    /// 陈旧输入（自动化/UIA 点击的典型形态：最近一次真实输入在两秒前）与输入缺失都不能息屏；
    /// 末尾的新鲜输入对照证明空结果来自新鲜度判定，而不是点击根本没投递。
    /// </summary>
    [Fact]
    public void StaleOrMissingRealInput_DoesNotDim()
    {
        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            NativeMethods.IdleTimeProvider = static () => TimeSpan.FromMilliseconds(2000);
            RaiseClick(box);
            Assert.Empty(writes);
            Assert.False(box.Checked, "被拒绝的一次性动作也必须回到 OFF，不能停在半开。");

            NativeMethods.IdleTimeProvider = static () => TimeSpan.MaxValue;   // 读不到输入
            RaiseClick(box);
            Assert.Empty(writes);

            NativeMethods.IdleTimeProvider = static () => TimeSpan.Zero;       // 反向对照：新鲜输入放行
            RaiseClick(box);
            Assert.Equal(new[] { 0 }, writes);
            ScreenBlankController.RestoreImmediate();
        });
    }

    /// <summary>
    /// watchdog：黑屏时长达到上限（<see cref="ScreenBlankController.MaxBlankDuration"/>）即强制恢复，
    /// 即使期间完全没有输入；未到上限且无输入则保持黑屏。
    /// </summary>
    [Fact]
    public void Watchdog_RestoresAfterMaxBlankDuration()
    {
        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            RaiseClick(box);   // 点击时 idle=0（新鲜输入）放行
            Assert.Equal(new[] { 0 }, writes);
            Assert.True(ScreenBlankController.IsDimmed);

            // 黑屏后没有任何输入（空闲窗口很大）：只有 watchdog 能恢复。
            NativeMethods.IdleTimeProvider = static () => TimeSpan.FromMinutes(10);
            ScreenBlankController.Poll(DateTime.UtcNow.AddMinutes(10));
            Assert.Equal(new[] { 0 }, writes);   // 未到上限：不恢复
            Assert.True(ScreenBlankController.IsDimmed);

            ScreenBlankController.Poll(DateTime.UtcNow.Add(
                ScreenBlankController.MaxBlankDuration + TimeSpan.FromSeconds(1)));
            Assert.Equal(new[] { 0, 50 }, writes);
            Assert.Equal(ScreenBlankController.ES_CONTINUOUS, exec[^1]);
            Assert.False(ScreenBlankController.IsDimmed);
        }, idle: static () => TimeSpan.Zero);
    }

    /// <summary>亮度读失败时绝不猜、绝不息屏（否则可能把屏幕压黑却记错原值，无法恢复）。</summary>
    [Fact]
    public void BrightnessReadFailure_NeverDims()
    {
        WithScreenBlankSeams((writes, exec) =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = MonitorOffBox(form);

            RaiseClick(box);

            Assert.Empty(writes);
            Assert.Empty(exec);
            Assert.False(ScreenBlankController.IsDimmed);
        }, idle: static () => TimeSpan.Zero, brightness: null);
    }

    // ------------------------------------------------------------ 阻塞调用静态锁

    /// <summary>
    /// 旧的阻塞广播入口/接缝必须彻底消失：它曾在 UI 线程上同步等待所有顶层窗口，
    /// 在本机把系统带进 Connected Standby，恢复后界面卡死。
    /// </summary>
    [Fact]
    public void NativeMethods_NoLongerExposesBlockingMonitorOffBroadcast()
    {
        Assert.Null(typeof(NativeMethods).GetMethod("TurnOffScreen",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(typeof(NativeMethods).GetProperty("MonitorOffSender",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        string source = File.ReadAllText(MainProjectFile("NativeMethods.cs"));
        Assert.DoesNotContain("TurnOffScreen", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bSendMessage\(", source);
    }

    // ------------------------------------------------------------ 息屏测试脚手架

    /// <summary>
    /// 注入亮度读/写、执行状态、空闲时间接缝并托管全局状态（审计模式、hw、自动轮询），结束时全部还原——
    /// 套件已禁用并行，但静态状态仍必须还原以免污染后续测试。亮度写入只进内存记录器，绝不真改屏。
    /// </summary>
    static void WithScreenBlankSeams(
        Action<List<int>, List<uint>> body,
        Func<TimeSpan>? idle = null,
        int? brightness = 50,
        MechrevoHw? hardware = null)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Func<int?>? previousRead = ScreenBrightness.ReadOverride;
        Action<int>? previousWrite = ScreenBrightness.WriteOverride;
        Action<uint>? previousExecutionState = ScreenBlankController.ExecutionStateOverride;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        bool previousAutoPoll = ScreenBlankController.AutoPollEnabled;
        var writes = new List<int>();
        var executionStates = new List<uint>();
        try
        {
            Program.UiAuditMode = true;
            Program.hw = hardware!;
            ScreenBrightness.ReadOverride = () => brightness;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = executionStates.Add;
            NativeMethods.IdleTimeProvider = idle;
            ScreenBlankController.AutoPollEnabled = false;   // 测试直接调 Poll(now)，不依赖消息泵定时器
            body(writes, executionStates);
        }
        finally
        {
            // 接缝仍在时先还原，避免把接缝状态/黑屏状态泄漏给后续测试（写入进记录器，不碰真屏）。
            ScreenBlankController.RestoreImmediate();
            ScreenBlankController.AutoPollEnabled = previousAutoPoll;
            NativeMethods.IdleTimeProvider = previousIdle;
            ScreenBlankController.ExecutionStateOverride = previousExecutionState;
            ScreenBrightness.WriteOverride = previousWrite;
            ScreenBrightness.ReadOverride = previousRead;
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

    /// <summary>沿目录向上找仓库根（含 MechrevoLite.slnx 的那一层）。</summary>
    static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!;
    }

    static string MainProjectFile(string relativePath) =>
        Path.Combine(RepositoryRoot().FullName, "src", "MechrevoLiteWin", relativePath);

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
