using System.Runtime.InteropServices;
using MechrevoLite.Helpers;

namespace MechrevoLite.Gpu;

/// <summary>重启后核对的结论。</summary>
public enum GpuRestartVerdict
{
    /// <summary>没有挂起的显卡重启，或还没重启过。</summary>
    NotPending,

    /// <summary>硬件回读与目标一致。</summary>
    Applied,

    /// <summary>重启后仍是旧路由。</summary>
    NotApplied,

    /// <summary>回读不可用：不下结论，只记日志。</summary>
    Unconfirmed,
}

/// <summary>
/// MUX 切换要重启才生效（G12）：切换前记下目标，重启后读硬件回读（内屏接在哪块卡上 + 独显在位）比对，
/// 一致才提示「已生效」，不一致提示「没有生效」并以实际路由为准。服务的状态串不参与判断——
/// 它在写 NVRAM 之前就改了。
/// </summary>
internal static class GpuRestartVerifier
{
    internal const string PendingKey = "gpu_restart_pending";
    internal const string TickKey = "gpu_restart_tick";
    internal const string TargetKey = "gpu_restart_target";

    /// <summary>记下挂起的重启目标（在发出切换指令之前调用）。</summary>
    internal static void MarkPending(int targetMode)
    {
        AppConfig.Set(PendingKey, 1);
        AppConfig.Set(TickKey, Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppConfig.Set(TargetKey, targetMode);
    }

    /// <summary>清掉挂起标记（请求失败回滚、或核对完成）。</summary>
    internal static void Clear()
    {
        AppConfig.Set(PendingKey, 0);
        AppConfig.Remove(TickKey);
        AppConfig.Remove(TargetKey);
    }

    /// <summary>TickCount64 只在 Windows 真的重启后才会变小（10 s 容差）。</summary>
    internal static bool RebootHappened(long markedTick, long nowTick) => nowTick + 10_000 < markedTick;

    /// <summary>
    /// 本次开机内是否还有挂起的显卡重启（「重启生效」标签）。已经重启过的挂起项不算，
    /// 但也不在这里清掉——留给 <see cref="RunAfterBootAsync"/> 核对。
    /// </summary>
    internal static bool HasPendingThisBoot()
    {
        if (!AppConfig.Is(PendingKey)) return false;
        return !(long.TryParse(AppConfig.GetString(TickKey), out long markedTick) &&
                 RebootHappened(markedTick, Environment.TickCount64));
    }

    /// <summary>挂起的目标模式（没有时为 -1）。</summary>
    internal static int PendingTarget => AppConfig.Is(PendingKey) ? AppConfig.Get(TargetKey, -1) : -1;

    /// <summary>纯函数：目标 vs 回读路由。</summary>
    internal static GpuRestartVerdict Evaluate(int targetMode, GpuRoute actual)
    {
        if (targetMode is < Hardware.MechrevoService.GpuIGpu or > Hardware.MechrevoService.GpuDgpu)
            return GpuRestartVerdict.Unconfirmed;
        if (actual == GpuRoute.Unknown) return GpuRestartVerdict.Unconfirmed;
        return GpuRouteInference.ToGpuMode(actual) == targetMode
            ? GpuRestartVerdict.Applied
            : GpuRestartVerdict.NotApplied;
    }

    /// <summary>
    /// 若挂起的重启已经发生，取出目标并清掉标记。没有挂起、或还没重启过时返回 false（标记保持）。
    /// </summary>
    internal static bool TryTakeRebootTarget(out int targetMode)
    {
        targetMode = -1;
        if (!AppConfig.Is(PendingKey)) return false;
        if (!long.TryParse(AppConfig.GetString(TickKey), out long markedTick) ||
            !RebootHappened(markedTick, Environment.TickCount64))
            return false;
        targetMode = AppConfig.Get(TargetKey, -1);
        Clear();
        return true;
    }

    /// <summary>测试接缝：代替 Toast（生产为 null）。</summary>
    internal static Action<GpuRestartVerdict, string>? NotifyOverride { get; set; }

    /// <summary>回读不可用时两次重试之间的等待（开机时显示驱动可能还没就绪；测试可压缩）。</summary>
    internal static TimeSpan ReadbackRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 开机后核对一次：延迟 <paramref name="delay"/> 让显示驱动就绪，回读不可用时再试两次。
    /// </summary>
    internal static async Task<GpuRestartVerdict> RunAfterBootAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            if (!TryTakeRebootTarget(out int target))
            {
                // 没有挂起的切换：只留一行启动时的回读，供多机型核对（内屏接线 / 独显在位 / NVRAM 字节）。
                // 信息级：这一行是诊断回显，NVRAM 读不到（50 系根本没有这个变量）不算程序出错，
                // 按失败词归类会让每台 50 系机器每次启动都多记一条错误、触发日志补传。
                GpuRouteReadback atStart = GpuRouteMonitor.Refresh();
                Logger.WriteInfo($"GPU route at startup: {atStart}; {UefiDisplayModeDiagnostics.Describe()}");
                return GpuRestartVerdict.NotPending;
            }

            GpuRouteReadback readback = GpuRouteReadback.Unavailable;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                readback = GpuRouteMonitor.Refresh();
                if (readback.Route != GpuRoute.Unknown || attempt == 2) break;
                await Task.Delay(ReadbackRetryDelay).ConfigureAwait(false);
            }
            GpuRestartVerdict verdict = Evaluate(target, readback.Route);
            // 级别看结论而不是看字样：只有「重启后没生效」算错误；诊断尾巴里的 NVRAM 字样不参与归类。
            string verifyLine = $"GPU route verify: target={target} actual={readback.Route} ({readback}) verdict={verdict}; {UefiDisplayModeDiagnostics.Describe()}";
            if (verdict == GpuRestartVerdict.NotApplied) Logger.WriteError(verifyLine);
            else Logger.WriteInfo(verifyLine);

            string message = verdict switch
            {
                GpuRestartVerdict.Applied => string.Format(Properties.Strings.GpuRouteApplied, ModeName(target)),
                GpuRestartVerdict.NotApplied => string.Format(Properties.Strings.GpuRouteNotApplied,
                    ModeName(GpuRouteInference.ToGpuMode(readback.Route))),
                _ => "",
            };
            if (message.Length > 0)
            {
                if (NotifyOverride is { } notify) notify(verdict, message);
                else if (verdict == GpuRestartVerdict.Applied) ToastForm.ShowNotice(message);
                else ToastForm.ShowFailure(message);
            }
            return verdict;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU route verify failed: " + ex.Message);
            return GpuRestartVerdict.Unconfirmed;
        }
    }

    /// <summary>界面上的模式名（集显 / 标准 / 直连）。</summary>
    internal static string ModeName(int mode) => mode switch
    {
        Hardware.MechrevoService.GpuIGpu => Properties.Strings.GpuRouteIgpu,
        Hardware.MechrevoService.GpuDgpu => Properties.Strings.GpuRouteDirect,
        _ => Properties.Strings.GpuRouteStandard,
    };
}

/// <summary>
/// 诊断用：以管理员身份运行时读一次厂商 NVRAM 变量里的显示模式字节，只写日志，不参与任何判定——
/// 偏移 0x62 是按 S40 <c>NVRAM_STRUCT.cs</c> 的顺序布局推算的，还没在硬件上核对过。
/// 字段日志用来在不同机型上核对这个偏移。只读：只调 <c>GetFirmwareEnvironmentVariableW</c>。
/// </summary>
internal static class UefiDisplayModeDiagnostics
{
    const string VariableName = "UniWillVariable";
    const string VariableGuid = "{9f33f85c-13ca-4fd1-9c4a-96217722c593}";
    const int DisplayModeOffset = 0x62;
    const int ErrorEnvVarNotFound = 203;
    const uint TokenAdjustPrivileges = 0x0020;
    const uint TokenQuery = 0x0008;
    const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>一行日志描述；未提权、读失败时说明原因。</summary>
    internal static string Describe()
    {
        try
        {
            if (!ProcessHelper.IsUserAdministrator()) return "NVRAM display byte: not elevated";
            if (!EnableSystemEnvironmentPrivilege()) return "NVRAM display byte: privilege unavailable";
            var buffer = new byte[4096];
            uint length = GetFirmwareEnvironmentVariableW(VariableName, VariableGuid, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                int error = Marshal.GetLastWin32Error();
                // 203 = ERROR_ENVVAR_NOT_FOUND：固件里没有这个变量（50 系不走 NVRAM 路由），不是读取故障。
                return error == ErrorEnvVarNotFound
                    ? $"NVRAM display byte: variable absent ({error})"
                    : $"NVRAM display byte: read failed ({error})";
            }
            if (length <= DisplayModeOffset) return $"NVRAM display byte: variable too short ({length} B)";
            byte value = buffer[DisplayModeOffset];
            GpuRoute? intel = GpuRouteInference.DecodeOemDisplayMode(value, isAmd: false);
            return $"NVRAM display byte @0x{DisplayModeOffset:X}={value} (len={length}, intel-decoded={intel?.ToString() ?? "unrecognized"}; offset unverified)";
        }
        catch (Exception ex)
        {
            return "NVRAM display byte: " + ex.Message;
        }
    }

    static bool EnableSystemEnvironmentPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            return false;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out Luid luid)) return false;
            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;   // ERROR_NOT_ALL_ASSIGNED = 1300
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFirmwareEnvironmentVariableW(string name, string guid, [Out] byte[] buffer, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
}
