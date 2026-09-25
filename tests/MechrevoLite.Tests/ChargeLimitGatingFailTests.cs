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
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Unparsable));
    }

    [Fact]
    public void AModelOutsideTheTwentyFourIsRejected()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.NotInSet));
    }

    /// <summary>
    /// Service-served is not a charge-limit capability bit. Profile lag must not be papered over
    /// by opening a channel whose addresses are not proven to control charging.
    /// </summary>
    [Fact]
    public void ASupportedModelIsNotAChargeLimitCapabilityEvenBeforeTheProfileArrives()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Supported));
        Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
    }

    [Fact]
    public void ANonOneZeroForceValueIsNotTreatedAsAForceSwitch()
    {
        using var _ = ChargeLimitGatingTests.Force("yes");
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Unparsable));
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
