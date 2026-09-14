using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace MechrevoLite.Gpu.NVidia;

/// <summary>
/// 提权写入请求。<paramref name="Token"/> 是父进程在启动助手时生成的一次性凭据，
/// 助手只接受携带同一凭据的请求。
/// </summary>
public sealed record GpuOverclockApplyRequest(
    int? CoreOffset,
    int? MemoryOffset,
    bool? Enabled,
    string? Token = null);

public sealed record GpuOverclockApplyResult(
    bool Success,
    int CoreOffset,
    int MemoryOffset,
    string? Error);

public interface IGpuOverclockElevatedApplier
{
    Task<GpuOverclockApplyResult> ApplyAsync(GpuOverclockApplyRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 把 NvAPI 超频写入交给一个提权子进程执行，UI 本体保持非提权。
///
/// 威胁模型说明（重要，别把这里的防护当成它做不到的事）：
/// - 能挡住的：**本机其他用户**。管道 DACL 只授予预期的客户端 SID 与 LocalSystem，
///   服务端还会在连接后核对客户端令牌的 SID。
/// - 挡不住的：**同一用户下的恶意进程**。它本来就能直接驱动我们这个非提权 UI 去发起同样的请求，
///   所以「同用户不可驱动提权写入」在这个架构里不可能成立。一次性凭据 + 收紧的空闲窗口
///   + 不留孤儿进程属于纵深防御，用来抬高门槛和缩小暴露面，不是硬边界。
/// - 危害上限：<see cref="Apply"/> 会用驱动上报的范围校验请求值，因此不是任意代码执行，
///   而是「非特权代码驱动一次范围内的硬件写入」。
/// </summary>
public sealed class ElevatedGpuOverclockApplier : IGpuOverclockElevatedApplier, IDisposable
{
    const string PipePrefix = "LMechrevoGpuOc-";
    const int TokenHexLength = 32;
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ElevationFailureCooldown = TimeSpan.FromSeconds(45);
    static readonly TimeSpan ElevationRefusalCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 助手在无请求时的存活上限。过去是 20 分钟——一个可写超频的提权进程带着可连接的管道
    /// 空转 20 分钟，暴露面太大。5 分钟足以覆盖用户连续调节滑块的场景。
    /// </summary>
    internal static readonly TimeSpan HelperIdleTimeout = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim _requestLock = new(1, 1);
    string? _pipeName;
    string? _token;
    Process? _helper;
    DateTimeOffset _elevationBlockedUntil = DateTimeOffset.MinValue;

    public async Task<GpuOverclockApplyResult> ApplyAsync(GpuOverclockApplyRequest request, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipeName is not null && _token is not null)
            {
                GpuOverclockApplyResult? reused = await TrySendAsync(
                    _pipeName, request with { Token = _token }, cancellationToken).ConfigureAwait(false);
                if (reused is not null) return reused;
                // 复用失败说明助手已经不在了（或者不再响应）：清掉缓存并结束可能残留的进程。
                StopHelper();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!ShouldPromptForElevation(now, _elevationBlockedUntil))
            {
                return new GpuOverclockApplyResult(
                    false,
                    0,
                    0,
                    "GPU 写入助手刚刚启动失败或被取消，请稍后重试。重复请求不会再次弹出管理员权限。");
            }

            SecurityIdentifier? clientSid = TryGetCurrentUserSid();
            if (clientSid is null)
            {
                return new GpuOverclockApplyResult(
                    false, 0, 0, "无法确定当前用户 SID，出于安全考虑不启动提权写入助手。");
            }

            string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
            string token = Guid.NewGuid().ToString("N");
            if (!TryStartHelper(pipeName, token, clientSid, out Process? helper, out bool userRefused))
            {
                _elevationBlockedUntil = now +
                    (userRefused ? ElevationRefusalCooldown : ElevationFailureCooldown);
                return new GpuOverclockApplyResult(
                    false, 0, 0,
                    userRefused
                        ? "已取消管理员授权，未写入 GPU 频率。"
                        : "无法启动 GPU 写入助手，请查看日志。");
            }

            GpuOverclockApplyResult? result = await TrySendAsync(
                pipeName, request with { Token = token }, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                // 关键：助手已经提权起来了。发送失败时必须结束它，
                // 否则会留下一个带着可写超频管道的孤儿提权进程空转到空闲超时。
                KillHelper(helper);
                _elevationBlockedUntil = DateTimeOffset.UtcNow + ElevationFailureCooldown;
                return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入助手没有在限定时间内响应。");
            }

            _pipeName = pipeName;
            _token = token;
            _helper = helper;
            if (result.Success) _elevationBlockedUntil = DateTimeOffset.MinValue;
            return result;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    bool _disposed;

    /// <summary>UI 退出时结束可能还在空转的提权助手。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHelper();
        _requestLock.Dispose();
    }

    void StopHelper()
    {
        KillHelper(_helper);
        _helper = null;
        _pipeName = null;
        _token = null;
    }

    static void KillHelper(Process? helper)
    {
        if (helper is null) return;
        try
        {
            if (!helper.HasExited) helper.Kill();
        }
        catch (Exception ex) { Logger.WriteLine("Can't stop the GPU OC helper: " + ex.Message); }
        finally { helper.Dispose(); }
    }

    static SecurityIdentifier? TryGetCurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't read the current user SID: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 启动提权助手。<paramref name="userRefused"/> 区分「用户在 UAC 框里点了取消」
    /// 和「真的启动失败」——过去把所有 Win32Exception 都当成取消，
    /// 真实故障会被误报为"已取消授权"并触发 2 分钟冷却。
    /// </summary>
    static bool TryStartHelper(
        string pipeName,
        string token,
        SecurityIdentifier clientSid,
        out Process? helper,
        out bool userRefused)
    {
        helper = null;
        userRefused = false;
        try
        {
            // 提权目标是我们自己的镜像。镜像位于用户可写目录时给出警告：
            // 这类交互式提权由用户当场确认，风险远小于 SYSTEM 开机任务，
            // 但便携式部署下确实存在同目录镜像被替换的可能。
            if (!MechrevoLite.Helpers.ExecutableTrust.IsCurrentImageInProtectedLocation(out string trustReason))
                Logger.WriteLineThrottled(
                    "gpu-oc-helper-trust",
                    "GPU OC helper is being elevated from a user-writable location: " + trustReason,
                    60000);

            helper = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = $"--gpu-oc-helper {pipeName} {token} {clientSid.Value}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return helper is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户主动拒绝了提权。
            Logger.WriteLine("GPU OC elevation was cancelled by the user.");
            userRefused = true;
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU OC helper start failed: " + ex.Message);
            return false;
        }
    }

    internal static bool ShouldPromptForElevation(DateTimeOffset now, DateTimeOffset blockedUntil) =>
        now >= blockedUntil;

    /// <summary>
    /// The helper runs elevated while the UI remains unelevated. CurrentUserOnly
    /// uses the elevated process integrity context and can reject the UI token,
    /// so grant the same user SID explicit pipe access instead.
    /// </summary>
    internal static PipeSecurity CreatePipeSecurityForCurrentUser() =>
        CreatePipeSecurityForClient(
            WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("当前用户 SID 不可用。"));

    /// <summary>
    /// 只授予预期的客户端 SID 与 LocalSystem。显式传入客户端 SID（而不是用助手自己的身份）
    /// 也修掉了「以另一个管理员账户提权」时管道对原用户不可访问的问题。
    /// </summary>
    internal static PipeSecurity CreatePipeSecurityForClient(SecurityIdentifier clientSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRule(new PipeAccessRule(
            clientSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    internal static NamedPipeServerStream CreatePipeServer(string pipeName) =>
        CreatePipeServer(pipeName, CreatePipeSecurityForCurrentUser());

    internal static NamedPipeServerStream CreatePipeServer(string pipeName, PipeSecurity security) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None,
            // PipeDirection and PipeSecurity define read/write access; ReadWrite here is an invalid extra-rights value on Windows.
            (PipeAccessRights)0);

    /// <summary>
    /// 客户端连接的唯一入口。
    ///
    /// <see cref="TokenImpersonationLevel.Impersonation"/> 是必需的，不是可选加固：
    /// 服务端用 <c>RunAsClient</c> 读取连接方的令牌 SID 来核对身份，而
    /// NamedPipeClientStream 的多数构造重载把模拟级别默认成 <c>None</c>，
    /// 那样服务端根本无法模拟客户端，身份校验会恒定失败 —— 表现不是"更安全"，
    /// 而是整条提权写入通道被堵死。这里集中一处设置，避免以后在别的调用点漏掉。
    /// </summary>
    internal static NamedPipeClientStream CreateClientPipe(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);

    static async Task<GpuOverclockApplyResult?> TrySendAsync(
        string pipeName,
        GpuOverclockApplyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using NamedPipeClientStream pipe = CreateClientPipe(pipeName);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            string? response = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return response is null
                ? null
                : JsonSerializer.Deserialize<GpuOverclockApplyResult>(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("gpu-oc-helper-connect", "GPU OC helper connection failed: " + ex.Message, 5000);
            return null;
        }
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 3) return 2;
        if (!IsValidPipeName(args[0])) return 2;
        if (!IsValidToken(args[1])) return 2;
        SecurityIdentifier expectedClient;
        try { expectedClient = new SecurityIdentifier(args[2]); }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU OC helper received an invalid client SID: " + ex.Message);
            return 2;
        }
        return RunAsync(args[0], args[1], expectedClient).GetAwaiter().GetResult();
    }

    static bool IsValidPipeName(string pipeName) =>
        pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        pipeName.Length == PipePrefix.Length + TokenHexLength &&
        pipeName.Skip(PipePrefix.Length).All(char.IsAsciiHexDigit);

    static bool IsValidToken(string token) =>
        token.Length == TokenHexLength && token.All(char.IsAsciiHexDigit);

    /// <summary>
    /// 凭据比较用固定时间实现，避免通过响应时间逐字节猜测。
    /// </summary>
    internal static bool TokenMatches(string? expected, string? provided)
    {
        if (expected is null || provided is null) return false;
        if (expected.Length != provided.Length) return false;
        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ provided[i];
        return difference == 0;
    }

    static async Task<int> RunAsync(string pipeName, string token, SecurityIdentifier expectedClient)
    {
        PipeSecurity security = CreatePipeSecurityForClient(expectedClient);
        while (true)
        {
            using var pipe = CreatePipeServer(pipeName, security);
            using var idleTimeout = new CancellationTokenSource(HelperIdleTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(idleTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            try
            {
                // DACL 已经把其他用户挡在外面，这里再核对一次连接方令牌的 SID：
                // 纵深防御，也覆盖 DACL 被外部改动的情况。
                if (!ClientSidMatches(pipe, expectedClient))
                {
                    Logger.WriteLine("GPU OC helper rejected a connection from an unexpected identity.");
                    continue;
                }

                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? payload = await reader.ReadLineAsync(idleTimeout.Token).ConfigureAwait(false);
                GpuOverclockApplyResult result = ResolveRequest(payload, token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                // 服务端先把响应排空再断开，否则客户端可能读到 null，
                // 表现为"助手没有在限定时间内响应" + 冷却 + 孤儿进程。
                try { pipe.WaitForPipeDrain(); }
                catch (Exception ex) { Logger.WriteLine("GPU OC helper drain failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("GPU OC helper request failed: " + ex.Message);
            }
        }
    }

    /// <summary>测试用入口：直接验证请求解析与凭据校验，不触碰真实 NvAPI。</summary>
    internal static GpuOverclockApplyResult ResolveRequestForTests(string? payload, string expectedToken) =>
        ResolveRequest(payload, expectedToken);

    static GpuOverclockApplyResult ResolveRequest(string? payload, string expectedToken)
    {
        if (payload is null)
            return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入请求为空。");

        GpuOverclockApplyRequest? request;
        try { request = JsonSerializer.Deserialize<GpuOverclockApplyRequest>(payload); }
        catch (JsonException ex)
        {
            Logger.WriteLine("GPU OC helper received malformed JSON: " + ex.Message);
            return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入请求格式无效。");
        }

        if (request is null)
            return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入请求格式无效。");
        if (!TokenMatches(expectedToken, request.Token))
        {
            Logger.WriteLine("GPU OC helper rejected a request with an invalid token.");
            return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入请求未通过校验。");
        }

        return Apply(request);
    }

    internal static bool ClientSidMatches(NamedPipeServerStream pipe, SecurityIdentifier expectedClient)
    {
        try
        {
            SecurityIdentifier? actual = null;
            pipe.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                actual = identity.User;
            });
            return actual is not null && actual.Equals(expectedClient);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU OC helper can't verify the client identity: " + ex.Message);
            return false;
        }
    }

    static GpuOverclockApplyResult Apply(GpuOverclockApplyRequest? request)
    {
        if (request is null)
            return new GpuOverclockApplyResult(false, 0, 0, "GPU 写入请求格式无效。");

        using IGpuOverclockControl? control = NvidiaGpuControl.TryCreateOverclockControl();
        if (control is null || !control.IsAvailable)
            return new GpuOverclockApplyResult(false, 0, 0, "NVIDIA 驱动未提供可写入的超频接口。");

        int targetCore = request.Enabled == false ? 0 : request.CoreOffset ?? control.CoreOffset.Current;
        int targetMemory = request.Enabled == false ? 0 : request.MemoryOffset ?? control.MemoryOffset.Current;
        if (control.CoreOffset.IsAdjustable && !control.CoreOffset.Contains(targetCore) ||
            control.MemoryOffset.IsAdjustable && !control.MemoryOffset.Contains(targetMemory))
        {
            return new GpuOverclockApplyResult(
                false,
                control.CoreOffset.Current,
                control.MemoryOffset.Current,
                "请求值超出 NVIDIA 驱动允许范围。");
        }

        bool coreOk = !control.CoreOffset.IsAdjustable ||
            control.CoreOffset.Current == targetCore || control.SetCoreOffset(targetCore);
        bool memoryOk = !control.MemoryOffset.IsAdjustable ||
            control.MemoryOffset.Current == targetMemory || control.SetMemoryOffset(targetMemory);
        control.Refresh();
        bool confirmed = coreOk && memoryOk &&
            (!control.CoreOffset.IsAdjustable || control.CoreOffset.Current == targetCore) &&
            (!control.MemoryOffset.IsAdjustable || control.MemoryOffset.Current == targetMemory);
        Logger.WriteLine($"Elevated NVIDIA OC: core={targetCore}/{control.CoreOffset.Current}, " +
            $"memory={targetMemory}/{control.MemoryOffset.Current}, confirmed={confirmed}");
        return new GpuOverclockApplyResult(
            confirmed,
            control.CoreOffset.Current,
            control.MemoryOffset.Current,
            confirmed ? null : "NVIDIA 驱动回读值与请求值不一致。");
    }
}
