using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MechrevoLite.Gpu.NVidia;

namespace MechrevoLite.Tests;

/// <summary>
/// 提权 GPU 写入助手的 IPC 契约。
/// 能挡住的是本机其他用户；同一用户下的恶意进程本来就能驱动非提权 UI 发起同样请求，
/// 所以凭据与收紧的窗口属于纵深防御，不是硬边界——这些测试守住的正是那部分可验证的边界。
/// </summary>
public class ElevatedHelperProtocolTests
{
    static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("测试环境没有用户 SID");
    }

    // ---- 管道 ACL ----

    /// <summary>
    /// 只有预期的客户端 SID 与 LocalSystem 被授权，不存在授予 Everyone/Users 的项。
    /// </summary>
    [Fact]
    public void PipeSecurityGrantsOnlyTheExpectedClientAndLocalSystem()
    {
        SecurityIdentifier client = CurrentUserSid();
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        PipeSecurity security = ElevatedGpuOverclockApplier.CreatePipeSecurityForClient(client);

        var granted = security
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToList();

        Assert.Contains(client, granted);
        Assert.Contains(localSystem, granted);
        Assert.All(granted, sid => Assert.True(
            sid.Equals(client) || sid.Equals(localSystem),
            $"意外被授权的身份：{sid}"));
    }

    /// <summary>
    /// 显式传入客户端 SID 也修掉了「以另一个管理员账户提权」时管道对原用户不可访问的问题。
    /// </summary>
    [Fact]
    public void PipeSecurityCanTargetAClientOtherThanTheHostingIdentity()
    {
        var otherClient = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        PipeSecurity security = ElevatedGpuOverclockApplier.CreatePipeSecurityForClient(otherClient);

        Assert.Contains(
            otherClient,
            security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Select(rule => (SecurityIdentifier)rule.IdentityReference));
    }

    [Fact]
    public void PipeCanBeCreatedWithTheGeneratedAcl()
    {
        string pipeName = "LMechrevoGpuOc-" + Guid.NewGuid().ToString("N");

        using NamedPipeServerStream pipe = ElevatedGpuOverclockApplier.CreatePipeServer(pipeName);

        Assert.False(pipe.IsConnected);
    }

    // ---- 一次性凭据 ----

    [Fact]
    public void TokenComparisonAcceptsOnlyAnExactMatch()
    {
        string token = Guid.NewGuid().ToString("N");

        Assert.True(ElevatedGpuOverclockApplier.TokenMatches(token, token));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(token, token.ToUpperInvariant()));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(token, token[..^1]));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(token, token + "0"));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(token, ""));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(token, null));
        Assert.False(ElevatedGpuOverclockApplier.TokenMatches(null, token));
    }

    /// <summary>请求记录必须能携带凭据并原样往返，否则助手侧永远校验失败。</summary>
    [Fact]
    public void RequestSerialisationRoundTripsTheToken()
    {
        var request = new GpuOverclockApplyRequest(150, 500, true, "abc123");

        var roundTripped = JsonSerializer.Deserialize<GpuOverclockApplyRequest>(
            JsonSerializer.Serialize(request));

        Assert.NotNull(roundTripped);
        Assert.Equal(request, roundTripped);
    }

    /// <summary>凭据是可选参数，既有的三参数构造点不受影响。</summary>
    [Fact]
    public void RequestTokenIsOptionalSoExistingCallSitesStillCompile()
    {
        var request = new GpuOverclockApplyRequest(100, 200, true);

        Assert.Null(request.Token);
        Assert.Equal("xyz", (request with { Token = "xyz" }).Token);
    }

    // ---- 命令行参数校验 ----

    /// <summary>
    /// 参数个数、管道名格式、凭据格式、客户端 SID 任一不合法都必须直接退出，
    /// 绝不能起一个不设防的服务端。
    /// </summary>
    [Fact]
    public void HelperRejectsWrongArgumentCount()
    {
        string pipeName = "LMechrevoGpuOc-" + Guid.NewGuid().ToString("N");
        string token = Guid.NewGuid().ToString("N");
        string sid = CurrentUserSid().Value;

        Assert.Equal(2, ElevatedGpuOverclockApplier.Run([]));
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run([pipeName]));
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run([pipeName, token]));
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run([pipeName, token, sid, "extra"]));
    }

    [Theory]
    [InlineData("wrong-prefix-00000000000000000000000000000000")]
    [InlineData("LMechrevoGpuOc-tooshort")]
    [InlineData("LMechrevoGpuOc-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("LMechrevoGpuOc-../../evil00000000000000000000")]
    public void HelperRejectsMalformedPipeNames(string pipeName) =>
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run(
            [pipeName, Guid.NewGuid().ToString("N"), CurrentUserSid().Value]));

    [Theory]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("")]
    public void HelperRejectsMalformedTokens(string token) =>
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run(
            ["LMechrevoGpuOc-" + Guid.NewGuid().ToString("N"), token, CurrentUserSid().Value]));

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-")]
    [InlineData("")]
    public void HelperRejectsMalformedClientSids(string sid) =>
        Assert.Equal(2, ElevatedGpuOverclockApplier.Run(
            ["LMechrevoGpuOc-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), sid]));

    // ---- 暴露窗口 ----

    /// <summary>
    /// 助手空闲存活时间必须明显短于原来的 20 分钟：一个可写超频的提权进程
    /// 带着可连接的管道空转越久，暴露面越大。
    /// </summary>
    [Fact]
    public void HelperIdleWindowIsTightenedButStillUsableForSliderTweaking()
    {
        Assert.True(ElevatedGpuOverclockApplier.HelperIdleTimeout <= TimeSpan.FromMinutes(5));
        Assert.True(ElevatedGpuOverclockApplier.HelperIdleTimeout >= TimeSpan.FromMinutes(1));
    }

    /// <summary>提权被拒后进入冷却期，不再反复弹 UAC。</summary>
    [Theory]
    [InlineData(0, 120, false)]
    [InlineData(60, 120, false)]
    [InlineData(120, 120, true)]
    [InlineData(180, 120, true)]
    public void ElevationPromptIsSuppressedUntilCooldownExpires(
        long nowSeconds, long blockedUntilSeconds, bool expected)
    {
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, ElevatedGpuOverclockApplier.ShouldPromptForElevation(
            origin.AddSeconds(nowSeconds),
            origin.AddSeconds(blockedUntilSeconds)));
    }

    /// <summary>Dispose 必须可重复调用，UI 退出路径会走到它。</summary>
    [Fact]
    public void DisposeIsIdempotent()
    {
        var applier = new ElevatedGpuOverclockApplier();

        applier.Dispose();
        applier.Dispose();
    }
}
