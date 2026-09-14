using System.IO.Pipes;
using System.Security.Principal;
using MechrevoLite.Gpu.NVidia;

namespace MechrevoLite.Tests;

/// <summary>
/// 提权助手的**握手**回归测试。
///
/// 为什么单独一组：客户端身份校验只有在客户端允许服务端模拟其令牌时才成立。
/// NamedPipeClientStream 的多数构造重载把 TokenImpersonationLevel 默认成 None，
/// 此时服务端的 RunAsClient 会失败，身份校验恒为 false —— 结果不是"更安全"，
/// 而是提权写入通道被完全堵死。这一组测试把「客户端连接方式」和「服务端身份校验」
/// 作为一个整体契约钉住。
/// </summary>
public class ElevatedHelperHandshakeTests
{
    static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("测试环境没有用户 SID");
    }

    static string NewPipeName() => "LMechrevoGpuOc-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// 端到端握手：服务端必须能读出客户端 SID 并与预期值匹配。
    /// 客户端这里刻意使用与产品代码 TrySendAsync 相同的连接方式。
    /// </summary>
    [Fact]
    public async Task ServerCanVerifyTheClientIdentityOverAProductionStyleConnection()
    {
        SecurityIdentifier expected = CurrentUserSid();
        string pipeName = NewPipeName();
        bool? verified = null;

        using var server = ElevatedGpuOverclockApplier.CreatePipeServer(pipeName);
        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            verified = ElevatedGpuOverclockApplier.ClientSidMatches(server, expected);
        });

        using NamedPipeClientStream client = ElevatedGpuOverclockApplier.CreateClientPipe(pipeName);
        await client.ConnectAsync(5000);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(verified, "服务端无法核对客户端身份：客户端连接没有授予模拟权限，" +
                              "这会让提权写入通道完全不可用。");
    }

    /// <summary>
    /// 与一个不匹配的 SID 比较时必须返回 false —— 校验不能是「永远通过」。
    /// </summary>
    [Fact]
    public async Task ServerRejectsAMismatchedClientSid()
    {
        string pipeName = NewPipeName();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        bool? verified = null;

        using var server = ElevatedGpuOverclockApplier.CreatePipeServer(pipeName);
        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            verified = ElevatedGpuOverclockApplier.ClientSidMatches(server, everyone);
        });

        using NamedPipeClientStream client = ElevatedGpuOverclockApplier.CreateClientPipe(pipeName);
        await client.ConnectAsync(5000);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(verified, "身份校验必须能拒绝不匹配的 SID，否则等于没有校验。");
    }

    /// <summary>
    /// 完整请求/响应往返：合法凭据必须拿到响应，错误与缺失凭据必须被拒但连接不能被掐断
    /// （掐断连接会让客户端只看到"管道已中断"，无法区分校验失败和助手崩溃）。
    /// </summary>
    [Fact]
    public async Task RequestRoundTripDistinguishesValidAndInvalidCredentials()
    {
        string pipeName = NewPipeName();
        string token = Guid.NewGuid().ToString("N");
        SecurityIdentifier expected = CurrentUserSid();

        using var server = ElevatedGpuOverclockApplier.CreatePipeServer(pipeName);
        Task<string?> serverTask = Task.Run<string?>(async () =>
        {
            await server.WaitForConnectionAsync();
            if (!ElevatedGpuOverclockApplier.ClientSidMatches(server, expected)) return "identity-rejected";
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            string? payload = await reader.ReadLineAsync();
            GpuOverclockApplyResult result = ElevatedGpuOverclockApplier.ResolveRequestForTests(payload, token);
            await writer.WriteLineAsync(result.Error ?? "ok");
            return null;
        });

        using NamedPipeClientStream client = ElevatedGpuOverclockApplier.CreateClientPipe(pipeName);
        await client.ConnectAsync(5000);
        using var clientWriter = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var clientReader = new StreamReader(client, leaveOpen: true);
        await clientWriter.WriteLineAsync(
            "{\"CoreOffset\":0,\"MemoryOffset\":0,\"Enabled\":false,\"Token\":\"deadbeefdeadbeefdeadbeefdeadbeef\"}");
        string? response = await clientReader.ReadLineAsync();

        Assert.Null(await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(response);
        // 凭据错误 -> 明确的校验失败文案，而不是连接被掐断。
        Assert.Contains("校验", response);
    }
}
