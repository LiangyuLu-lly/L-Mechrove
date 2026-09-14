using System.Diagnostics;
using System.Text.Json;

namespace MechrevoLite.Gpu;

internal sealed record ElevatedKillTarget(int ProcessId, string ProcessName, DateTime StartedAtUtc);

internal sealed record ElevatedKillRequest(string Token, IReadOnlyList<ElevatedKillTarget> Targets);

internal sealed record ElevatedKillTargetResult(int ProcessId, string ProcessName, bool Killed, string? Error = null);

internal sealed record ElevatedKillResult(bool Success, IReadOnlyList<ElevatedKillTargetResult> Results);

/// <summary>
/// 一次性提权杀进程通道。核显切换的独显占用预检里，普通权限杀不掉管理员权限运行的
/// 占卡进程——把目标清单写进请求文件，以 runas 重启自身镜像执行
/// <c>--kill-process &lt;请求文件&gt; &lt;结果文件&gt; &lt;token&gt;</c>，杀完写结果文件退出。
///
/// 与 OC 助手（常驻命名管道）刻意分开：杀进程是低频一次性动作，不值得留一个提权进程空转。
/// 安全边界与 OC 助手同款：token 经命令行携带、请求体内核对（固定时间比较）；目标必须
/// 通过 <see cref="DgpuApplicationSafety.IsSafeCandidate"/>（保护名单/同会话/我们自己和
/// GCU 绝不在列）；执行前按「进程名 + 启动时间」重新核对身份，PID 复用或已换人的目标
/// 一律跳过——宁可杀不掉，也不杀错。
/// </summary>
internal static class ElevatedProcessKiller
{
    static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(30);

    // ---------- 客户端（普通权限侧） ----------

    /// <summary>
    /// 以管理员权限强杀目标进程，返回 (是否全部结束, 描述)。UAC 被取消不算错误，
    /// 是用户明确说「不」。
    /// </summary>
    internal static async Task<(bool Clean, string Message)> KillWithElevationAsync(
        IReadOnlyList<DgpuApplication> targets)
    {
        if (targets.Count == 0) return (true, "没有需要提权结束的目标。");

        string token = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(Path.GetTempPath(), $"LMechrevoKill-{Guid.NewGuid():N}.json");
        string resultPath = Path.Combine(Path.GetTempPath(), $"LMechrevoKill-{Guid.NewGuid():N}.result.json");
        var request = new ElevatedKillRequest(token, targets
            .Where(t => t.StartedAtUtc.HasValue)
            .Select(t => new ElevatedKillTarget(t.ProcessId, t.ProcessName, t.StartedAtUtc!.Value))
            .ToList());
        if (request.Targets.Count == 0) return (false, "目标缺少启动时间，无法安全核对身份。");

        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request)).ConfigureAwait(false);
            Process? helper;
            try
            {
                if (!MechrevoLite.Helpers.ExecutableTrust.IsCurrentImageInProtectedLocation(out string trustReason))
                    Logger.WriteLineThrottled(
                        "kill-helper-trust",
                        "Kill helper is being elevated from a user-writable location: " + trustReason,
                        60000);

                helper = Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = $"--kill-process \"{requestPath}\" \"{resultPath}\" {token}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.WriteLine("Elevated kill was cancelled by the user.");
                return (false, "已取消管理员授权，未结束任何进程。");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Elevated kill helper start failed: " + ex.Message);
                return (false, "无法启动提权进程，请查看日志。");
            }

            if (helper is null) return (false, "无法启动提权进程。");
            try
            {
                await helper.WaitForExitAsync().WaitAsync(KillWaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.WriteLine("Elevated kill helper did not exit in time; continuing with its result file if any.");
            }
            helper.Dispose();

            if (!File.Exists(resultPath))
                return (false, "提权进程未返回结果。");

            ElevatedKillResult? result;
            try
            {
                result = JsonSerializer.Deserialize<ElevatedKillResult>(
                    await File.ReadAllTextAsync(resultPath).ConfigureAwait(false));
            }
            catch (JsonException ex)
            {
                Logger.WriteLine("Elevated kill result is malformed: " + ex.Message);
                return (false, "提权进程返回了无效结果。");
            }

            if (result is null) return (false, "提权进程返回了无效结果。");
            int killed = result.Results.Count(r => r.Killed);
            return (result.Success,
                result.Success
                    ? $"已结束 {killed} 个独显程序。"
                    : "部分目标未能结束：" + string.Join("；", result.Results
                        .Where(r => !r.Killed)
                        .Select(r => $"{r.ProcessName}({r.Error ?? "未知原因"})")));
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.WriteLine("Elevated kill temp file cleanup failed: " + ex.Message); }
    }

    // ---------- 助手（管理员权限侧） ----------

    /// <summary>Program 的 --kill-process 入口。返回进程退出码。</summary>
    internal static int RunHelper(string[] args)
    {
        if (args.Length != 3) return 2;
        if (!IsValidToken(args[2]))
        {
            Logger.WriteLine("Elevated kill helper received an invalid token.");
            return 2;
        }

        ElevatedKillRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ElevatedKillRequest>(File.ReadAllText(args[0]));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Elevated kill request is unreadable: " + ex.Message);
            return 2;
        }
        if (request is null || !TokenMatches(args[2], request.Token))
        {
            Logger.WriteLine("Elevated kill helper rejected a request with an invalid token.");
            return 2;
        }

        ElevatedKillResult result = Execute(request);
        try
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Elevated kill result write failed: " + ex.Message);
            return 3;
        }
        return result.Success ? 0 : 1;
    }

    internal static ElevatedKillResult Execute(ElevatedKillRequest request)
    {
        using Process current = Process.GetCurrentProcess();
        var results = new List<ElevatedKillTargetResult>();
        foreach (ElevatedKillTarget target in request.Targets)
            results.Add(KillTarget(target, current.Id, current.SessionId));
        return new ElevatedKillResult(results.All(r => r.Killed), results);
    }

    static ElevatedKillTargetResult KillTarget(ElevatedKillTarget target, int helperPid, int helperSession)
    {
        // 先做「这还是不是我们看到的那个人」的核对，再决定杀不杀。
        try
        {
            using Process process = Process.GetProcessById(target.ProcessId);
            string? rejection = ValidateTarget(process, target, helperPid, helperSession);
            if (rejection is not null)
            {
                Logger.WriteLine($"Elevated kill skipped {target.ProcessName}#{target.ProcessId}: {rejection}");
                return new ElevatedKillTargetResult(target.ProcessId, target.ProcessName, false, rejection);
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(1500);
            Logger.WriteLine($"Elevated kill: {target.ProcessName}#{target.ProcessId} killed");
            return new ElevatedKillTargetResult(target.ProcessId, target.ProcessName, true);
        }
        catch (ArgumentException)
        {
            // 目标已经退出了——视为成功，这正是我们想要的状态。
            return new ElevatedKillTargetResult(target.ProcessId, target.ProcessName, true, "已退出");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Elevated kill failed for {target.ProcessName}#{target.ProcessId}: {ex.Message}");
            return new ElevatedKillTargetResult(target.ProcessId, target.ProcessName, false, ex.Message);
        }
    }

    /// <summary>执行前校验：身份三元组核对 + 保护名单 + 同会话。返回 null 表示可以杀。</summary>
    internal static string? ValidateTarget(Process process, ElevatedKillTarget target, int helperPid, int helperSession)
    {
        try
        {
            if (!string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                return "进程名不匹配（PID 可能已被复用）";
            if (process.StartTime.ToUniversalTime() != target.StartedAtUtc)
                return "启动时间不匹配（PID 可能已被复用）";
        }
        catch (Exception ex)
        {
            return "无法读取进程身份：" + ex.Message;
        }

        // 保护名单/同会话/自身与 GCU 排除——与预检阶段同一份判据，双保险。
        var candidate = new DgpuApplication(target.ProcessId, target.ProcessName, helperSession, target.StartedAtUtc);
        return DgpuApplicationSafety.IsSafeCandidate(candidate, helperPid, helperSession)
            ? null
            : "目标在保护名单内或不满足安全条件";
    }

    internal static bool IsValidToken(string token) =>
        token.Length == 32 && token.All(char.IsAsciiHexDigit);

    /// <summary>凭据比较用固定时间实现，与 OC 助手同一纪律。</summary>
    internal static bool TokenMatches(string? expected, string? provided)
    {
        if (expected is null || provided is null) return false;
        if (expected.Length != provided.Length) return false;
        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ provided[i];
        return difference == 0;
    }
}
