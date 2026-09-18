using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T7（Wave B）失败 / 边界：F3 不过或服务画像缺失时充电上限必须 fail-closed；
/// 强制开关只认 "1"/"0"，其他字符串不得当开关。happy 断言见 <see cref="ChargeLimitGatingTests"/>。
/// </summary>
public class ChargeLimitGatingFailTests
{
    [Fact]
    public void AnUnparsableIdentityIsRejectedEvenWithAServiceProfile()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Unparsable, ChargeLimitGatingTests.ServiceProfile));
    }

    [Fact]
    public void AModelOutsideTheTwentyFourIsRejected()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.NotInSet, ChargeLimitGatingTests.ServiceProfile));
    }

    /// <summary>
    /// N15 #15 契约变更：服务画像（ItemSupport）由厂商服务写入，可能晚于首次采样；把它当否决项
    /// 会把「服务已服务本机」的机器整个锁在充电上限之外（星耀14 ai9 365 现场 bug）。
    /// 支持判定本身已编码「服务服务本机」，画像只是附加信号，不再是否决项。
    /// 旧断言（Supported + 空画像 → false）编码的是被移除的旧契约，故更新为 true。
    /// </summary>
    [Fact]
    public void ASupportedModelIsAcceptedEvenBeforeTheServiceProfileArrives()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.True(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Supported, ChargeLimitGatingTests.EmptyProfile));
    }

    [Fact]
    public void ANonOneZeroForceValueIsNotTreatedAsAForceSwitch()
    {
        using var _ = ChargeLimitGatingTests.Force("yes");
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Unparsable, ChargeLimitGatingTests.EmptyProfile));
    }

    [Fact]
    public void TheGateDoesNotConsultTheWindowsModelString()
    {
        // 旧路径：IsAvailableOnThisMachine() => IsSupportedMachine(AppConfig.GetModel())。
        // 现在必须走矩阵 + F3，本测试用源码契约锁定不再出现该调用形状。
        string source = ChargeLimitGatingTests.SourceFile("Hardware", "EcChargeLimit.cs");
        Assert.DoesNotContain("Contains(\"YAOSHI\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("string? model", source);
    }
}
