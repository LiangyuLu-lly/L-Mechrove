using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 自定义档切换的即时反馈 / 单次落盘 / 单次应用。
///
/// 用户报告：切换太慢，偶尔「未确认」之后又变「已确认」。根因链路：选中态只在硬件确认后
/// 才移动 → 点击后确认窗口（250ms+900ms）内界面毫无反应 → 用户再点一次 → 同一次切换
/// 被下发两遍，两条流程各自做确认后工作（真机日志里 ApplyProfile/SetBoost 成对出现，
/// SetActivePlan 一次早返回不记日志即为佐证）。
/// 这里把三条行为钉死：点击当帧选中态就移动；每次成功切换恰好一次 <see cref="AppConfig.Flush"/>；
/// 恰好一次 <see cref="WinPowerPlan.ApplyProfile"/>（被后一次点击取代的旧流程不得再做确认后工作）。
/// </summary>
public class CustomProfileSwitchFeedbackTests
{
    const string FanControl = "Fan/Control";
    const string FanStatus = "Fan/Status";
    const string BalancePlan = "381b4222-f694-41f0-9685-ff5bb260df2e";

    static string CommandOf(object payload) =>
        payload is IDictionary<string, object> values && values.TryGetValue("Action", out object? action)
            ? action?.ToString() ?? ""
            : "";

    static void EchoProfile(MechrevoHw hardware, int profileIndex) =>
        hardware.HandleMessage(FanStatus,
            $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{profileIndex},\"CPU_PL1\":55,\"CPU_PL2\":80}}");

    /// <summary>四个档的电源设置全部预置，避免测试里出现 GetOrCreateProfileSettings 的首次落盘。</summary>
    static void SeedProfileStorage()
    {
        for (int i = 0; i < WinPowerPlan.CustomProfileCount; i++)
        {
            AppConfig.Set(WinPowerPlan.GetProfilePlanKey(i), BalancePlan);
            AppConfig.Set(WinPowerPlan.GetProfileBoostKey(i), 1);
        }
        AppConfig.Set("custom_last_profile", 0);
    }

    static Label StatusOf(CustomModeForm form) =>
        (Label)typeof(CustomModeForm)
            .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

    static RButton[] ButtonsOf(CustomModeForm form) =>
        (RButton[])typeof(CustomModeForm)
            .GetField("_profileBtns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

    /// <summary>把 Program 的静态接缝指向测试硬件与真实服务，并在 Dispose 时还原。</summary>
    sealed class ProgramScope : IDisposable
    {
        readonly bool _previousAuditMode;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;

        ProgramScope(bool previousAuditMode, MechrevoHw? previousHardware, MechrevoService? previousService)
        {
            _previousAuditMode = previousAuditMode;
            _previousHardware = previousHardware;
            _previousService = previousService;
        }

        public static ProgramScope Enter(MechrevoHw hardware)
        {
            var scope = new ProgramScope(Program.UiAuditMode, Program.hw, Program.service);
            Program.UiAuditMode = true;
            Program.hw = hardware;
            Program.service = new MechrevoService(hardware);
            return scope;
        }

        public void Dispose()
        {
            Program.UiAuditMode = _previousAuditMode;
            Program.hw = _previousHardware!;
            Program.service = _previousService!;
        }
    }

    [Fact]
    public async Task Click_MovesSelectionImmediately_ThenAppliesPowerOnceAndFlushesOnce()
    {
        var echoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hardwareProfile = 0;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != FanControl) return Task.CompletedTask;
            string command = CommandOf(payload);
            if (command == "OPERATING_CUSTOM_MODE")
            {
                int requested = Convert.ToInt32(((IDictionary<string, object>)payload)["ProfileIndex"]);
                _ = Task.Run(async () =>
                {
                    await echoGate.Task;   // 回读被扣住：界面反馈不允许依赖硬件确认。
                    hardwareProfile = requested;
                    EchoProfile(hardware!, requested);
                });
            }
            else if (command == "GETSTATUS")
            {
                EchoProfile(hardware!, hardwareProfile);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            SeedProfileStorage();
            EchoProfile(hardware, 0);
            using var _ = ProgramScope.Enter(hardware);
            using var form = new CustomModeForm();
            Label status = StatusOf(form);
            RButton[] buttons = ButtonsOf(form);
            int applies = 0;
            WinPowerPlan.ApplyProfileOverride = _ => { applies++; return true; };
            int flushesBefore = AppConfig.FlushCount;
            try
            {
                Task<bool> switching = form.ActivateProfileAsync(1);

                // 硬件尚未确认（回读被 gate 扣住）时，选中态必须已经在目标档上。
                Assert.True(buttons[1].Activated, "点击当帧选中态必须移动，不能等硬件确认。");
                Assert.False(buttons[0].Activated);
                Assert.Equal(CustomModeForm.SwitchPendingText, status.Text);

                echoGate.SetResult();
                Assert.True(await switching.WaitAsync(TimeSpan.FromSeconds(10)));

                Assert.True(buttons[1].Activated);
                Assert.Equal("自定义 2 已激活", status.Text);
                Assert.Equal(1, AppConfig.FlushCount - flushesBefore);
                Assert.Equal(1, applies);
            }
            finally { WinPowerPlan.ApplyProfileOverride = null; }
        }
    }

    [Fact]
    public async Task GenuineFailure_RevertsTheSelection_AndOnlyThenShowsTheFailureText()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            // 硬件始终报旧档：宽限期耗尽后这才是真失败。
            if (topic == FanControl && CommandOf(payload) == "GETSTATUS")
                EchoProfile(hardware!, 0);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            SeedProfileStorage();
            EchoProfile(hardware, 0);
            using var _ = ProgramScope.Enter(hardware);
            using var form = new CustomModeForm();
            Label status = StatusOf(form);
            RButton[] buttons = ButtonsOf(form);
            int applies = 0;
            WinPowerPlan.ApplyProfileOverride = _ => { applies++; return true; };
            int flushesBefore = AppConfig.FlushCount;
            try
            {
                Task<bool> switching = form.ActivateProfileAsync(1);
                Assert.True(buttons[1].Activated);
                Assert.Equal(CustomModeForm.SwitchPendingText, status.Text);

                Assert.False(await switching.WaitAsync(TimeSpan.FromSeconds(12)));

                Assert.True(buttons[0].Activated, "真失败后选中态必须回退到切换前的档位。");
                Assert.False(buttons[1].Activated);
                Assert.Equal(CustomModeForm.SwitchUnconfirmedText, status.Text);
                Assert.Equal(0, AppConfig.FlushCount - flushesBefore);
                Assert.Equal(0, applies);
                Assert.Equal(0, AppConfig.Get("custom_last_profile"));
            }
            finally { WinPowerPlan.ApplyProfileOverride = null; }
        }
    }

    [Fact]
    public async Task DuplicateClickWhileTheFirstSwitchIsInFlight_AppliesPowerAndFlushesOncePerSwitch()
    {
        var firstEchoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        int hardwareProfile = 0;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != FanControl) return Task.CompletedTask;
            string command = CommandOf(payload);
            if (command == "OPERATING_CUSTOM_MODE")
            {
                int requested = Convert.ToInt32(((IDictionary<string, object>)payload)["ProfileIndex"]);
                if (Interlocked.Increment(ref requests) == 1)
                {
                    // 第一次点击的回读被扣住（慢硬件）：用户因此再点了一次。
                    _ = Task.Run(async () =>
                    {
                        await firstEchoGate.Task;
                        hardwareProfile = requested;
                        EchoProfile(hardware!, requested);
                    });
                }
                else
                {
                    hardwareProfile = requested;
                    EchoProfile(hardware!, requested);
                }
            }
            else if (command == "GETSTATUS")
            {
                EchoProfile(hardware!, hardwareProfile);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            SeedProfileStorage();
            EchoProfile(hardware, 0);
            using var _ = ProgramScope.Enter(hardware);
            using var form = new CustomModeForm();
            Label status = StatusOf(form);
            RButton[] buttons = ButtonsOf(form);
            var seenStatus = new List<string>();
            status.TextChanged += (_, _) => { lock (seenStatus) seenStatus.Add(status.Text); };
            int applies = 0;
            WinPowerPlan.ApplyProfileOverride = _ => { applies++; return true; };
            int flushesBefore = AppConfig.FlushCount;
            try
            {
                Task<bool> first = form.ActivateProfileAsync(1);
                // 第一遍还在途（回读被扣住）时用户又点了第二个档：最新点击立即接管选中态。
                Task<bool> second = form.ActivateProfileAsync(2);
                Assert.True(buttons[2].Activated, "最新一次点击必须立刻接管选中态。");

                firstEchoGate.SetResult();

                Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(12)), "被取代的旧流程不得宣称成功。");
                Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(12)));

                Assert.Equal(1, applies);
                Assert.Equal(1, AppConfig.FlushCount - flushesBefore);   // T5 superseded switch does not flush.
                Assert.True(buttons[2].Activated);
                Assert.Equal("自定义 3 已激活", status.Text);
                lock (seenStatus)
                    Assert.DoesNotContain(CustomModeForm.SwitchUnconfirmedText, seenStatus);
            }
            finally { WinPowerPlan.ApplyProfileOverride = null; }
        }
    }
}
