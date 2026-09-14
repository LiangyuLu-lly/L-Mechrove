using System.Diagnostics;
using System.Text.Json;
using MechrevoLite.Gpu;

namespace MechrevoLite.Tests;

/// <summary>
/// 提权杀进程通道的安全件：token 校验、身份三元组核对、保护名单闸门。
/// 这个通道的失败模式是「管理员权限杀错进程」，所以身份核对必须钉死——
/// PID 复用、换名、换启动时间的目标一律拒绝。
/// </summary>
public class ElevatedProcessKillerTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdefgh0123456789abcde", false)]   // 31 位
    [InlineData("0123456789abcdef0123456789abcdeg", false)]   // 含非十六进制字符
    [InlineData("", false)]
    public void IsValidToken_AcceptsOnly32HexCharacters(string token, bool expected) =>
        Assert.Equal(expected, ElevatedProcessKiller.IsValidToken(token));

    [Fact]
    public void TokenMatches_RejectsWrongToken() =>
        Assert.False(ElevatedProcessKiller.TokenMatches(
            "0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcde0"));

    [Fact]
    public void ValidateTarget_AcceptsTheExactProcessIdentity()
    {
        using Process current = Process.GetCurrentProcess();
        var target = new ElevatedKillTarget(
            current.Id, current.ProcessName, current.StartTime.ToUniversalTime());
        using Process currentAgain = Process.GetCurrentProcess();

        // helperPid 刻意取别的值：IsSafeCandidate 会拒绝「杀自己」，真实流程里
        // 提权助手永远不会把目标定到自己头上；这里只验证身份三元组核对。
        Assert.Null(ElevatedProcessKiller.ValidateTarget(currentAgain, target, current.Id + 1, current.SessionId));
    }

    [Fact]
    public void ValidateTarget_RejectsAMismatchedName()
    {
        using Process current = Process.GetCurrentProcess();
        var target = new ElevatedKillTarget(
            current.Id, "definitely-not-" + current.ProcessName, current.StartTime.ToUniversalTime());

        string? rejection = ElevatedProcessKiller.ValidateTarget(current, target, current.Id, current.SessionId);
        Assert.NotNull(rejection);
        Assert.Contains("进程名", rejection);
    }

    [Fact]
    public void ValidateTarget_RejectsAMismatchedStartTime()
    {
        using Process current = Process.GetCurrentProcess();
        var target = new ElevatedKillTarget(
            current.Id, current.ProcessName, current.StartTime.ToUniversalTime() + TimeSpan.FromSeconds(2));

        string? rejection = ElevatedProcessKiller.ValidateTarget(current, target, current.Id, current.SessionId);
        Assert.NotNull(rejection);
        Assert.Contains("启动时间", rejection);
    }

    [Fact]
    public void Execute_WithNoTargets_SucceedsWithoutTouchingAnything()
    {
        ElevatedKillResult result = ElevatedProcessKiller.Execute(new ElevatedKillRequest("t", []));

        Assert.True(result.Success);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void RequestAndResult_RoundTripThroughJson()
    {
        var request = new ElevatedKillRequest("token", [new ElevatedKillTarget(123, "app", new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc))]);
        var result = new ElevatedKillResult(false, [new ElevatedKillTargetResult(123, "app", false, "拒绝")]);

        ElevatedKillRequest? parsedRequest = JsonSerializer.Deserialize<ElevatedKillRequest>(JsonSerializer.Serialize(request));
        ElevatedKillResult? parsedResult = JsonSerializer.Deserialize<ElevatedKillResult>(JsonSerializer.Serialize(result));

        Assert.NotNull(parsedRequest);
        Assert.Equal("token", parsedRequest!.Token);
        Assert.Equal(123, parsedRequest.Targets[0].ProcessId);
        Assert.Equal("app", parsedRequest.Targets[0].ProcessName);
        Assert.Equal(new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), parsedRequest.Targets[0].StartedAtUtc);
        Assert.NotNull(parsedResult);
        Assert.False(parsedResult!.Success);
        Assert.Equal("拒绝", parsedResult.Results[0].Error);
    }
}
