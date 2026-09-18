using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// T36（Wave G）happy 路径：重启后自动启动可靠 + 外来 GCU 共存提示。
///
/// 缺陷（#10 耀世16u）：重启后不自动启动。自启 = 用户级计划任务
/// （<c>Helpers/Startup.cs</c>，临时镜像不注册）；任务缺失但用户已启用时必须重建。
///
/// 外来 GCU（C3 重定义，round 5）：检测到厂商服务/13688 占用者时**提示用户自行移除官方控制台**，
/// 绝不静默删除；服务接管由安装器承担。失败路径见 <see cref="AutostartRebootFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class AutostartRebootTests
{
    [Fact]
    public async Task AMissingTaskIsRebuiltWhenTheUserEnabledAutostart()
    {
        bool? writtenTo = null;
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        try
        {
            Startup.ReadScheduledState = static () => false;      // 任务缺失
            Startup.WriteScheduledState = value => { writtenTo = value; return true; };

            bool result = await Task.FromResult(Startup.ApplyScheduledState(true));

            Assert.True(result);
            Assert.True(writtenTo);
        }
        finally
        {
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    [Fact]
    public void AnAlreadyCorrectStateIsNotRewritten()
    {
        int writes = 0;
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        try
        {
            Startup.ReadScheduledState = static () => true;
            Startup.WriteScheduledState = _ => { writes++; return true; };

            Assert.True(Startup.ApplyScheduledState(true));
            Assert.Equal(0, writes);   // 幂等：已经是目标态不再写
        }
        finally
        {
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    [Fact]
    public void TurningAutostartOffWritesOnce()
    {
        bool? writtenTo = null;
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        try
        {
            Startup.ReadScheduledState = static () => true;
            Startup.WriteScheduledState = value => { writtenTo = value; return true; };

            Assert.True(Startup.ApplyScheduledState(false));
            Assert.False(writtenTo);
        }
        finally
        {
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    [Theory]
    [InlineData(false, false, GcuCoexistenceKind.None)]
    [InlineData(true, false, GcuCoexistenceKind.ForeignService)]
    [InlineData(false, true, GcuCoexistenceKind.PortOwner)]
    [InlineData(true, true, GcuCoexistenceKind.Both)]
    public void ForeignGcuSignalsAreClassified(bool foreignService, bool foreignPortOwner, GcuCoexistenceKind expected)
    {
        Assert.Equal(expected, GcuCoexistence.Classify(foreignService, foreignPortOwner));
    }

    [Fact]
    public void AForeignGcuRequiresAVisibleConsoleRemovalPrompt()
    {
        foreach (GcuCoexistenceKind kind in new[]
                 { GcuCoexistenceKind.ForeignService, GcuCoexistenceKind.PortOwner, GcuCoexistenceKind.Both })
        {
            Assert.True(GcuCoexistence.RequiresConsoleRemovalPrompt(kind));
            string prompt = GcuCoexistence.BuildConsoleRemovalPrompt(kind);
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            Assert.Contains("卸载", prompt);
            Assert.Contains("官方", prompt);
        }
    }

    [Fact]
    public void NoForeignGcuMeansNoPrompt()
    {
        Assert.False(GcuCoexistence.RequiresConsoleRemovalPrompt(GcuCoexistenceKind.None));
    }
}
