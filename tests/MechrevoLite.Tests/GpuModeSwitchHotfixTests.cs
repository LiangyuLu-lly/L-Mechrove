using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using System.Reflection;

namespace MechrevoLite.Tests;

/// <summary>
/// 显卡模式切换热修（run5 gpu mode hotfix）。
///
/// 根因：显卡模式切换是「写入 → 重启生效」的多步流程。<see cref="SystemRestart.RequestRestart"/>
/// 原先只在调用当刻检查 <c>GetLastInputInfo ≤ 500ms</c>；而模式切换路径在用户点完确认框后还要
/// 写 EC / MUX 并 <c>await Task.Delay(500)</c>（iGPU 兜底路径还要 Flush 配置、弹 Toast），等真正
/// 请求重启时窗口早已过期 → 重启被静默拒绝：寄存器写了、机器不重启、重启后模式依旧不变。
///
/// 修复：确认框返回 Yes/OK 的当刻调用 <see cref="SystemRestart.CaptureUserConfirmation"/> 捕获一次
/// 短时凭证；<see cref="SystemRestart.RequestRestart"/> 的放行条件 = 新鲜真实输入 或 有效凭证。
/// 凭证只能在新鲜输入下取得，所以「程序化/自动化不得静默重启」的意图原样保留；模式写入本身与
/// 重启守卫无关，照常无条件执行。
/// </summary>
public class GpuModeSwitchHotfixTests
{
    sealed class RestartHarness : IDisposable
    {
        readonly Func<TimeSpan>? _previousIdle;
        readonly Action<string, string>? _previousStart;

        public List<(string File, string Args)> Started { get; } = new();
        public TimeSpan Idle { get; set; }

        public RestartHarness(TimeSpan idle)
        {
            _previousIdle = NativeMethods.IdleTimeProvider;
            _previousStart = SystemRestart.ProcessStartOverride;
            SystemRestart.ResetUserConfirmation();
            Idle = idle;
            NativeMethods.IdleTimeProvider = () => Idle;
            SystemRestart.ProcessStartOverride = (file, args) => Started.Add((file, args));
        }

        public bool WaitForStart(int ms = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && Started.Count == 0) Thread.Sleep(10);
            return Started.Count > 0;
        }

        public void Dispose()
        {
            SystemRestart.ProcessStartOverride = _previousStart;
            NativeMethods.IdleTimeProvider = _previousIdle;
            SystemRestart.ResetUserConfirmation();
        }
    }

    // ============================================================ 重启守卫 + 确认凭证

    /// <summary>确认框刚点过（新鲜输入）→ 捕获凭证；确认之后的 EC 写入/await 即使超过 500ms，重启仍放行。</summary>
    [Fact]
    public void ConfirmedRestart_CapturedAtDialog_SurvivesPostClickHardwareWork()
    {
        using var h = new RestartHarness(TimeSpan.Zero);
        Assert.True(SystemRestart.CaptureUserConfirmation());

        h.Idle = TimeSpan.FromSeconds(2);   // 之后写 EC + await Task.Delay(500) 吃掉 500ms 窗口

        Assert.True(SystemRestart.RequestRestart("gpu-mode", SystemRestart.RebootNowArguments));
        Assert.True(h.WaitForStart());
        Assert.Equal(("shutdown", SystemRestart.RebootNowArguments), Assert.Single(h.Started));
    }

    /// <summary>程序化调用：没有新鲜输入、也没有凭证 → 拒绝，绝不重启。</summary>
    [Fact]
    public void Restart_WithoutFreshInputOrConfirmation_IsRefused()
    {
        using var h = new RestartHarness(TimeSpan.FromSeconds(5));

        Assert.False(SystemRestart.RequestRestart("programmatic", SystemRestart.RebootNowArguments));
        Assert.Empty(h.Started);
    }

    /// <summary>陈旧输入下无法取得确认凭证，随后的重启请求同样被拒绝。</summary>
    [Fact]
    public void Confirmation_CannotBeCapturedFromStaleInput()
    {
        using var h = new RestartHarness(TimeSpan.FromSeconds(5));

        Assert.False(SystemRestart.CaptureUserConfirmation());
        Assert.False(SystemRestart.RequestRestart("stale", SystemRestart.RebootNowArguments));
        Assert.Empty(h.Started);
    }

    /// <summary>凭证是一次性：消费后不能再次放行。</summary>
    [Fact]
    public void Confirmation_IsConsumedOnce()
    {
        using var h = new RestartHarness(TimeSpan.Zero);
        Assert.True(SystemRestart.CaptureUserConfirmation());

        h.Idle = TimeSpan.FromSeconds(5);
        Assert.True(SystemRestart.RequestRestart("first", SystemRestart.RebootNowArguments));
        Assert.True(h.WaitForStart());
        Assert.False(SystemRestart.RequestRestart("second", SystemRestart.RebootNowArguments));
        Assert.Single(h.Started);
    }

    /// <summary>不依赖凭证的旧路径（新鲜输入仍在）行为不变。</summary>
    [Fact]
    public void FreshInput_StillAllowsRestartWithoutCapture()
    {
        using var h = new RestartHarness(TimeSpan.Zero);

        Assert.True(SystemRestart.RequestRestart("fresh", SystemRestart.RebootAfterFiveSecondsArguments));
        Assert.True(h.WaitForStart());
        Assert.Equal(("shutdown", SystemRestart.RebootAfterFiveSecondsArguments), Assert.Single(h.Started));
    }

    // ============================================================ UI 路径：写入 → 重启

    /// <summary>选择一个显卡模式：目标写入必须非空，且重启指令恰好是最后一步。</summary>
    [Fact]
    public async Task SelectingGpuMode_WritesTargetPayloadBeforeRequestingTheGcuReboot()
    {
        var actions = new List<string>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action))
                actions.Add(action?.ToString() ?? "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.RequestGpuModeRestartAsync(MechrevoService.GpuIGpu));

            Assert.NotEmpty(actions);
            Assert.Contains("DGPU_DIRECT_CONNECT_TOGGLE_IGPU", actions);
            Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", actions[^1]);
            Assert.True(
                actions.IndexOf("DGPU_DIRECT_CONNECT_TOGGLE_IGPU") < actions.IndexOf("DGPU_DIRECT_CONNECT_RESTART"),
                "目标硬件写入必须发生在重启指令之前。");
        }
    }

    // ============================================================ 重启后 reconcile

    static bool InvokeHasPendingGpuRestart() =>
        (bool)typeof(SettingsForm)
            .GetMethod("HasPendingGpuRestart", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;

    static void InvokeMarkGpuRestartPending() =>
        typeof(SettingsForm)
            .GetMethod("MarkGpuRestartPending", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);

    /// <summary>待重启标记：同一次开机内保持；检测到真实重启（TickCount64 归零）后清理。</summary>
    [Fact]
    public void PendingGpuRestart_SurvivesWithinTheSameBoot_AndClearsAfterARealRestart()
    {
        string? previousPending = AppConfig.GetString("gpu_restart_pending");
        string? previousTick = AppConfig.GetString("gpu_restart_tick");
        try
        {
            InvokeMarkGpuRestartPending();
            Assert.True(InvokeHasPendingGpuRestart(), "同一次开机内，待重启标记必须保持。");

            // 真机重启后 TickCount64 归零：旧 boot 记下的 tick 会远大于当前 TickCount64 → 必须清理。
            AppConfig.Set("gpu_restart_tick", (Environment.TickCount64 + 60_000).ToString());
            Assert.False(InvokeHasPendingGpuRestart(), "检测到真实重启后必须清理待重启标记。");
            Assert.False(AppConfig.Is("gpu_restart_pending"));
        }
        finally
        {
            if (previousPending is null) AppConfig.Set("gpu_restart_pending", 0);
            else AppConfig.Set("gpu_restart_pending", int.Parse(previousPending));
            if (previousTick is null) AppConfig.Remove("gpu_restart_tick");
            else AppConfig.Set("gpu_restart_tick", previousTick);
        }
    }
}
