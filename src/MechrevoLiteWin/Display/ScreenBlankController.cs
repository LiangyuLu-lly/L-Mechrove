using System.Runtime.InteropServices;
using MechrevoLite.Helpers;

namespace MechrevoLite.Display;

/// <summary>一次「息屏」尝试的结果。只有 <see cref="Dimmed"/> 表示屏幕真的被压黑。</summary>
internal enum ScreenBlankOutcome
{
    Dimmed,
    AlreadyDimmed,
    ReadFailed,
    WriteFailed,
}


/// <summary>
/// 「息屏（不睡眠）」控制器。
///
/// 真机事故（2026-09-14）：本机只报 S0 低功耗待机（Modern Standby，无 S3），向
/// <c>HWND_BROADCAST</c> 广播 <c>WM_SYSCOMMAND / SC_MONITORPOWER(2)</c>「关显示器」被 Windows
/// 当成「进入 Connected Standby」，点一次开关机器就睡了（Kernel-Power 506/507/566 在约 2s 内触发）。
/// 所以这里不再关显示器电源，而是把面板亮度压到最低做黑屏，并用
/// <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)</c> 顶住待机：
/// ES_SYSTEM_REQUIRED 阻止系统空闲睡眠，ES_DISPLAY_REQUIRED 只重置显示器空闲计时器
/// （防止显示器超时熄灭再次触发 Connected Standby），两者都不写亮度寄存器，面板仍由本控制器压黑。
///
/// 恢复条件：首次真实输入（轮询 <c>GetLastInputInfo</c>）或到达 <see cref="MaxBlankDuration"/>。
/// 亮度读不到或写失败绝不动作（不猜、不硬调光）；退出/会话结束/未处理异常走 <see cref="RestoreImmediate"/> 兜底。
/// </summary>
internal static class ScreenBlankController
{
    /// <summary>最长黑屏时长：超时无条件强制恢复，避免应用异常或无人值守时把用户留在黑屏里。</summary>
    internal static readonly TimeSpan MaxBlankDuration = TimeSpan.FromMinutes(30);

    internal const int PollIntervalMs = 300;

    /// <summary>刚黑屏的这段时间内不判定「新输入」，否则触发本次动作的那次点击会被自己当成唤醒输入。</summary>
    internal const int MinBlankBeforeRestoreMs = 800;

    /// <summary>视为「刚刚有真实输入」的空闲窗口（毫秒）。轮询间隔 300ms，取 400ms 保证不漏。</summary>
    internal const int RestoreIdleWindowMs = 400;

    /// <summary>黑屏时写入的面板亮度（最低/全黑）。</summary>
    internal const int BlankLevel = 0;

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;
    internal const uint BlankExecutionState = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;

    // ---- 测试接缝（先例：NativeMethods.IdleTimeProvider / UpdateChecker.HttpGetOverride）----

    /// <summary>非 null 时替代 SetThreadExecutionState；测试只记录标志位，不碰真实电源状态。</summary>
    internal static Action<uint>? ExecutionStateOverride { get; set; }

    /// <summary>非 null 时替代 DateTime.UtcNow，用于驱动 watchdog。</summary>
    internal static Func<DateTime>? ClockOverride { get; set; }

    /// <summary>测试可关掉自动轮询（测试直接调 <see cref="Poll(DateTime)"/>），生产保持 true。</summary>
    internal static bool AutoPollEnabled { get; set; } = true;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    static readonly object Gate = new();
    static System.Windows.Forms.Timer? _poll;
    static int _savedBrightness = -1;
    static DateTime _blankStartedUtc;
    static bool _dimmed;
    static string? _lastFailureReason;

    internal static bool IsDimmed { get { lock (Gate) return _dimmed; } }

    /// <summary>上一次息屏失败的原因（可检测提示用）；成功或未尝试时为 <c>null</c>。</summary>
    internal static string? LastFailureReason { get { lock (Gate) return _lastFailureReason; } }

    /// <summary>
    /// 真实用户动作：保存当前亮度 → 压到最低 → 顶住待机 → 开始轮询恢复。
    /// **返回值即结果**：读不到 WMI 或写不下去时返回失败，绝不把"没做"当"已熄屏"。
    /// </summary>
    internal static ScreenBlankOutcome Dim()
    {
        lock (Gate)
        {
            if (_dimmed) return ScreenBlankOutcome.AlreadyDimmed;

            int current;
            try
            {
                if (!ScreenBrightness.TryGet(out current))
                    return FailLocked("brightness read failed (no WMI instance)", ScreenBlankOutcome.ReadFailed);
            }
            catch (Exception ex)
            {
                return FailLocked("brightness read threw " + ex.Message, ScreenBlankOutcome.ReadFailed);
            }

            if (!ScreenBrightness.TrySet(BlankLevel))
                return FailLocked($"brightness write to {BlankLevel}% failed", ScreenBlankOutcome.WriteFailed);

            _savedBrightness = current;
            _blankStartedUtc = Now();
            _dimmed = true;
            _lastFailureReason = null;
            ApplyExecutionState(BlankExecutionState);
            StartPolling();
            Logger.WriteLine($"Screen blank: dimmed to {BlankLevel}% (was {current}%); system held awake (ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED).");
            return ScreenBlankOutcome.Dimmed;
        }
    }

    /// <summary>在持有 <see cref="Gate"/> 时记录可检测的失败；绝不置 <c>_dimmed</c>。</summary>
    static ScreenBlankOutcome FailLocked(string reason, ScreenBlankOutcome outcome)
    {
        _lastFailureReason = reason;
        Logger.WriteLine("Screen blank failed: " + reason);
        return outcome;
    }

    /// <summary>首次真实输入 / watchdog 到期：恢复原亮度并清除执行状态标志。幂等。</summary>
    internal static void Restore() => RestoreCore("input/watchdog");

    /// <summary>退出/会话结束/未处理异常兜底：无论当前状态如何都把亮度恢复回去。</summary>
    internal static void RestoreImmediate() => RestoreCore("exit/session/failure");

    internal static void Poll() => Poll(Now());

    /// <summary>轮询体：到达上限或出现首次真实输入即恢复。测试直接传入受控时钟。</summary>
    internal static void Poll(DateTime nowUtc)
    {
        string? reason = null;
        lock (Gate)
        {
            if (!_dimmed) return;
            double elapsedMs = (nowUtc - _blankStartedUtc).TotalMilliseconds;
            if (elapsedMs >= MaxBlankDuration.TotalMilliseconds)
                reason = "watchdog";
            else if (elapsedMs >= MinBlankBeforeRestoreMs &&
                     RawIdleTime() <= TimeSpan.FromMilliseconds(RestoreIdleWindowMs))
                reason = "user input";
        }
        if (reason is not null) RestoreCore(reason);
    }

    static void RestoreCore(string reason)
    {
        int restore;
        lock (Gate)
        {
            if (!_dimmed) return;
            _dimmed = false;
            restore = _savedBrightness;
            _savedBrightness = -1;
            StopPolling();
        }

        TryWriteBrightness(restore);
        ApplyExecutionState(ES_CONTINUOUS);
        Logger.WriteLine($"Screen blank: restored brightness to {restore}% ({reason}).");
    }

    static void TryWriteBrightness(int brightness)
    {
        try { ScreenBrightness.Set(brightness); }
        catch (Exception ex) { Logger.WriteLine("Screen blank restore failed: " + ex.Message); }
    }

    static void ApplyExecutionState(uint flags)
    {
        if (ExecutionStateOverride is { } apply) { apply(flags); return; }
        SetThreadExecutionState(flags);
    }

    static DateTime Now() => ClockOverride is { } clock ? clock() : DateTime.UtcNow;

    static TimeSpan RawIdleTime() =>
        NativeMethods.IdleTimeProvider is { } provider ? provider() : NativeMethods.GetIdleTime();

    static void StartPolling()
    {
        if (!AutoPollEnabled) return;
        if (_poll is null)
        {
            _poll = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
            _poll.Tick += (_, _) => Poll();
        }
        _poll.Start();
    }

    static void StopPolling()
    {
        try { _poll?.Stop(); }
        catch (Exception ex) { Logger.WriteLine("Screen blank poll stop failed: " + ex.Message); }
    }
}
