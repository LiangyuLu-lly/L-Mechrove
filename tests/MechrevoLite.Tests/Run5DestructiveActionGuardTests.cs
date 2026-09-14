using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 破坏性副作用守卫链回归（run5 UI hardening, item 1）。
///
/// 危险类别：<c>CheckedChanged</c> 里做不可逆的系统状态写入，且能被程序化驱动
/// （自动化、回读同步、误赋值）。已在「息屏」上验证过的守卫链推广到：
/// <list type="bullet">
/// <item>Windows 重启（4 处 shutdown /r）——<see cref="SystemRestart"/> 统一入口；</item>
/// <item>「开机自启动」快捷开关（建/删计划任务）与 footer 勾选框；</item>
/// <item>「深度睡眠」快捷开关（EC 休眠策略）。</item>
/// </list>
/// 守卫链：只挂 Click（程序化写 Checked 不触发）、要求 GetLastInputInfo 显示刚刚有真实输入
/// （≤500ms）、回读同步路径惰性、失败/被拒绝回滚。所有真实副作用都藏在接缝后：
/// <c>SystemRestart.ProcessStartOverride</c>、<c>Startup.Read/WriteScheduledState</c>、
/// <c>MechrevoHw.publishOverride</c>——测试只记录，绝不真重启、绝不碰真实计划任务。
/// </summary>
public class Run5DestructiveActionGuardTests
{
    // ============================================================ 重启统一入口

    /// <summary>程序化调用（自动化/无真实输入）绝不放行：这才是「程序化不可达」的实质门禁。</summary>
    [Fact]
    public async Task Restart_WithoutFreshInput_StartsNothing()
    {
        var started = new List<(string File, string Args)>();
        using var seams = new RestartSeams(TimeSpan.FromSeconds(5), started);

        Assert.False(SystemRestart.RequestRestart("unit-test", SystemRestart.RebootNowArguments));
        await Task.Delay(30);
        Assert.Empty(started);
    }

    /// <summary>真实点击后的新鲜输入放行；shutdown 必须在调用线程之外的线程发起（不占 UI 线程）。</summary>
    [Fact]
    public async Task Restart_WithFreshInput_StartsShutdownOffTheCallingThread()
    {
        var started = new List<(string File, string Args)>();
        int? startedOnThread = null;
        var signal = new ManualResetEventSlim(false);
        using var seams = new RestartSeams(TimeSpan.Zero, started, thread => { startedOnThread = thread; signal.Set(); });

        Assert.True(SystemRestart.RequestRestart("unit-test", SystemRestart.RebootNowArguments));
        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "新鲜输入放行后必须实际发起重启。");

        Assert.Equal(("shutdown", SystemRestart.RebootNowArguments), Assert.Single(started));
        Assert.NotNull(startedOnThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, startedOnThread);
        await Task.CompletedTask;
    }

    /// <summary>两种重启参数都按调用方给定原样透传（FHD / 旧 GPU 路径用 /t 1，GCU 兜底用 /t 5）。</summary>
    [Fact]
    public async Task Restart_PassesTheRequestedArgumentsThrough()
    {
        var started = new List<(string File, string Args)>();
        using var seams = new RestartSeams(TimeSpan.Zero, started);

        Assert.True(SystemRestart.RequestRestart("five-seconds", SystemRestart.RebootAfterFiveSecondsArguments));
        for (int i = 0; i < 100 && started.Count == 0; i++) await Task.Delay(10);

        Assert.Equal(("shutdown", SystemRestart.RebootAfterFiveSecondsArguments), Assert.Single(started));
    }

    // ============================================================ 开机自启动（快捷开关 quick_startup）

    /// <summary>程序化赋值 Checked（启动回读/自动化）绝不动计划任务。</summary>
    [Fact]
    public void QuickStartup_ProgrammaticCheckAssignment_NeverWritesTheTask()
    {
        bool scheduled = false;
        WithAutostartSeam(read: () => scheduled, write: value => { scheduled = value; return true; }, idle: TimeSpan.Zero, body: writes =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = StartupBox(form);

            box.Checked = true;
            box.Checked = false;
            box.Checked = true;

            Assert.Empty(writes);
            Assert.False(scheduled);
        });
    }

    /// <summary>
    /// 回读同步（UpdateQuickSwitches 内部置 _syncingSwitches 写 Checked）同样惰性。
    /// 用深度睡眠做样本：它的回读分支不受快捷组展开状态影响，能确定地走到 Checked 同步。
    /// </summary>
    [Fact]
    public void QuickDeepSleep_SyncAndReadBack_NeverSendsTheCommand()
    {
        WithDeepSleepHarness(idle: TimeSpan.Zero, body: (publishes, box, hardware) =>
        {
            Assert.True(box.Checked, "前置：硬件报 DEEPSLEEP_ON。");

            hardware.QuickSwitches["deepsleep"] = false;   // 回读报 OFF
            InvokeUpdateQuickSwitches((SettingsForm)box.FindForm()!);

            Assert.Empty(publishes);
            Assert.Equal(-1, AppConfig.Get("deepsleep_pending", -1));
            Assert.False(box.Checked, "回读必须把 Checked 同步成硬件状态。");
        });
    }

    /// <summary>真实点击 + 新鲜输入 → 经接缝建/删计划任务（用户可见行为不变）。</summary>
    [Fact]
    public void QuickStartup_GenuineClick_AppliesThroughTheSeam()
    {
        bool scheduled = false;
        WithAutostartSeam(read: () => scheduled, write: value => { scheduled = value; return true; }, idle: TimeSpan.Zero, body: writes =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = StartupBox(form);
            Assert.False(box.Checked);

            RaiseClick(box);   // ON
            Assert.True(scheduled);
            Assert.True(box.Checked);
            Assert.Equal(new[] { true }, writes);

            RaiseClick(box);   // OFF
            Assert.False(scheduled);
            Assert.False(box.Checked);
            Assert.Equal(new[] { true, false }, writes);
        });
    }

    /// <summary>陈旧输入（自动化点击的典型形态）→ 拒绝并回滚到系统真实状态，绝不写任务。</summary>
    [Fact]
    public void QuickStartup_StaleInput_RollsBackAndNeverWrites()
    {
        bool scheduled = false;
        WithAutostartSeam(read: () => scheduled, write: value => { scheduled = value; return true; }, idle: TimeSpan.FromSeconds(5), body: writes =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = StartupBox(form);
            Assert.False(box.Checked);

            RaiseClick(box);

            Assert.Empty(writes);
            Assert.False(scheduled);
            Assert.False(box.Checked, "被拒绝的点击必须回滚到系统真实状态。");
        });
    }

    // ============================================================ 深度睡眠（快捷开关 quick_deepsleep）

    /// <summary>程序化赋值 Checked 绝不发 DEEPSLEEP 命令，也不写 pending。</summary>
    [Fact]
    public void QuickDeepSleep_ProgrammaticCheckAssignment_NeverSendsTheCommand()
    {
        WithDeepSleepHarness(idle: TimeSpan.Zero, body: (publishes, box, _) =>
        {
            box.Checked = false;
            box.Checked = true;
            box.Checked = false;

            Assert.Empty(publishes);
            Assert.Equal(-1, AppConfig.Get("deepsleep_pending", -1));
        });
    }

    /// <summary>真实点击 + 新鲜输入 → 下发 DEEPSLEEP_ON 并记录待重启生效值。</summary>
    [Fact]
    public void QuickDeepSleep_GenuineClick_SendsTheCommand()
    {
        WithDeepSleepHarness(idle: TimeSpan.Zero, body: (publishes, box, _) =>
        {
            RaiseClick(box);   // 从 ON → OFF（初始硬件报 DEEPSLEEP_ON）

            Assert.Contains(publishes, entry => entry.Topic == "Setting/Control" &&
                entry.Payload is IDictionary<string, object> dict &&
                dict.TryGetValue("Action", out object? action) && Equals(action, "DEEPSLEEP_OFF"));
            Assert.Equal(0, AppConfig.Get("deepsleep_pending", -1));
        });
    }

    /// <summary>陈旧输入 → 拒绝且不回弹到用户拨到的位置。</summary>
    [Fact]
    public void QuickDeepSleep_StaleInput_RollsBackAndNeverSendsTheCommand()
    {
        WithDeepSleepHarness(idle: TimeSpan.FromSeconds(5), body: (publishes, box, _) =>
        {
            Assert.True(box.Checked, "前置：硬件报 DEEPSLEEP_ON。");

            RaiseClick(box);   // 尝试关掉

            Assert.Empty(publishes);
            Assert.Equal(-1, AppConfig.Get("deepsleep_pending", -1));
            Assert.True(box.Checked, "被拒绝的点击必须回滚到硬件回读值（仍为 ON）。");
        });
    }

    // ============================================================ 脚手架

    sealed class RestartSeams : IDisposable
    {
        readonly Func<TimeSpan>? _previousIdle;
        readonly Action<string, string>? _previousStart;

        public RestartSeams(TimeSpan idle, List<(string File, string Args)> started, Action<int>? recordThread = null)
        {
            _previousIdle = NativeMethods.IdleTimeProvider;
            _previousStart = SystemRestart.ProcessStartOverride;
            NativeMethods.IdleTimeProvider = () => idle;
            SystemRestart.ProcessStartOverride = (file, args) =>
            {
                started.Add((file, args));
                recordThread?.Invoke(Environment.CurrentManagedThreadId);
            };
        }

        public void Dispose()
        {
            SystemRestart.ProcessStartOverride = _previousStart;
            NativeMethods.IdleTimeProvider = _previousIdle;
        }
    }

    static void WithAutostartSeam(Func<bool?> read, Func<bool, bool> write, TimeSpan idle, Action<List<bool>> body)
    {
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        var writes = new List<bool>();
        try
        {
            Startup.ReadScheduledState = read;
            Startup.WriteScheduledState = value => { writes.Add(value); return write(value); };
            NativeMethods.IdleTimeProvider = () => idle;
            body(writes);
        }
        finally
        {
            NativeMethods.IdleTimeProvider = previousIdle;
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    static void WithDeepSleepHarness(TimeSpan idle, Action<List<(string Topic, object Payload)>, CheckBox, MechrevoHw> body)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        MechrevoService? previousService = Program.service;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        string? previousPending = AppConfig.GetString("deepsleep_pending");
        var publishes = new List<(string Topic, object Payload)>();
        MechrevoHw? hardware = null;
        try
        {
            Program.UiAuditMode = true;
            AppConfig.Remove("deepsleep_pending");
            hardware = new MechrevoHw((topic, payload) =>
            {
                publishes.Add((topic, payload));
                // 回显确认：让 SwitchDeepSleep 的 ConfirmSettingAsync 立刻成功，测试不必等超时。
                if (topic == "Setting/Control" && payload is IDictionary<string, object> dict &&
                    dict.TryGetValue("Action", out object? action))
                {
                    if (Equals(action, "DEEPSLEEP_ON"))
                        hardware!.HandleMessage("Setting/Status", """{"DeepSleepSwitch":"DEEPSLEEP_ON","DeepSleepTime":1800}""");
                    else if (Equals(action, "DEEPSLEEP_OFF"))
                        hardware!.HandleMessage("Setting/Status", """{"DeepSleepSwitch":"DEEPSLEEP_OFF"}""");
                }
                return Task.CompletedTask;
            }, MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
            {
                ["DeepSleepSwitch"] = "DEEPSLEEP_ON",
            }));
            hardware.HandleMessage("Setting/Status", """{"DeepSleepSwitch":"DEEPSLEEP_ON","DeepSleepTime":1800}""");
            Assert.True(hardware.SupportsQuickSwitch("deepsleep"), "前置：服务端报过 DeepSleepSwitch。");
            Program.hw = hardware;
            Program.service = new MechrevoService(hardware);
            NativeMethods.IdleTimeProvider = () => idle;

            using var form = new SettingsForm();
            form.CreateControl();
            var box = form.Controls.Find("quick_deepsleep", true).OfType<CheckBox>().Single();
            body(publishes, box, hardware);
        }
        finally
        {
            NativeMethods.IdleTimeProvider = previousIdle;
            Program.service = previousService!;
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            if (previousPending is null) AppConfig.Remove("deepsleep_pending");
            else AppConfig.Set("deepsleep_pending", int.Parse(previousPending));
        }
    }

    static CheckBox StartupBox(SettingsForm form) =>
        form.Controls.Find("quick_startup", true).OfType<CheckBox>().Single();

    static void InvokeUpdateQuickSwitches(SettingsForm form) =>
        typeof(SettingsForm)
            .GetMethod("UpdateQuickSwitches", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null);

    /// <summary>
    /// 投递一次真实 Click（与 PerformClick 同一条 CheckBox.OnClick → Click 路径：先按 AutoCheck
    /// 翻转 Checked 并触发 CheckedChanged，再触发 Click）。用反射而非 PerformClick，是为了不依赖
    /// 窗体可见性（PerformClick 的 CanSelect 要求整条父链 Visible）。
    /// </summary>
    static void RaiseClick(CheckBox box) =>
        typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(box, new object[] { EventArgs.Empty });
}
