using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T7（Wave B）happy 路径：充电上限不再靠机型串匹配，改由 <see cref="FeatureMatrix"/> + F3 判定；
/// <c>ec_charge_limit</c> 的强制开关语义（"1"/"0"）保留。失败断言见 <see cref="ChargeLimitGatingFailTests"/>。
/// </summary>
public class ChargeLimitGatingTests
{
    internal static readonly SupportDecision Supported = new(true, SupportReason.Ok, "PH4TRX1");
    internal static readonly SupportDecision Unparsable = SupportDecision.Unparsable();
    internal static readonly SupportDecision NotInSet = SupportDecision.NotInSet("PH6AGxx");

    internal static FeatureMatrix EmptyProfile => FeatureMatrix.FromValues(new Dictionary<string, object?>());

    internal static FeatureMatrix ServiceProfile => FeatureMatrix.FromValues(
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["KeyboardSupport"] = 1 });

    internal static IDisposable Force(string? value)
    {
        string? previous = AppConfig.GetString("ec_charge_limit");
        if (value is null) AppConfig.Remove("ec_charge_limit");
        else AppConfig.Set("ec_charge_limit", value);
        return new Restore(previous);
    }

    sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", previous);
        }
    }

    /// <summary>固定「厂商 EC 驱动是否在」与本机效果判定（测试不依赖宿主机真实驱动）。</summary>
    internal static IDisposable Driver(bool present, ChargeLimitVerdict verdict = ChargeLimitVerdict.Pending)
    {
        Func<bool>? previous = EcChargeLimit.DriverPresentOverride;
        string? previousVerdict = AppConfig.GetString(EcChargeLimit.VerdictKey);
        EcChargeLimit.DriverPresentOverride = () => present;
        EcChargeLimit.RecordVerdict(verdict);
        return new DriverRestore(previous, previousVerdict);
    }

    sealed class DriverRestore(Func<bool>? previous, string? previousVerdict) : IDisposable
    {
        public void Dispose()
        {
            EcChargeLimit.DriverPresentOverride = previous;
            if (previousVerdict is null) AppConfig.Remove(EcChargeLimit.VerdictKey);
            else AppConfig.Set(EcChargeLimit.VerdictKey, previousVerdict);
        }
    }

    [Fact]
    public void ASupportedModelWithTheVendorDriverOffersTheChargeLimit()
    {
        using var _ = Force(null);
        using var __ = Driver(present: true);
        Assert.True(EcChargeLimit.IsSupportedMachine(Supported),
            "service-served + vendor EC driver present: offered, then proven by charging evidence.");
    }

    [Fact]
    public void WithoutTheVendorDriverTheChargeLimitIsNotOffered()
    {
        using var _ = Force(null);
        using var __ = Driver(present: false);
        Assert.False(EcChargeLimit.IsSupportedMachine(Supported));
    }

    [Fact]
    public void AMachineWhoseLimitWasProvenIneffectiveIsNotOfferedAgain()
    {
        using var _ = Force(null);
        using var __ = Driver(present: true, ChargeLimitVerdict.Ineffective);
        Assert.False(EcChargeLimit.IsSupportedMachine(Supported),
            "charging continued above the limit on this machine: no fake slider.");
    }

    [Fact]
    public void AForceOnSwitchEnablesTheChannelRegardlessOfMatrixAndIdentity()
    {
        using var _ = Force("1");
        Assert.True(EcChargeLimit.IsSupportedMachine(Unparsable));
    }

    [Fact]
    public void AForceOffSwitchDisablesTheChannelRegardlessOfMatrixAndIdentity()
    {
        using var _ = Force("0");
        Assert.False(EcChargeLimit.IsSupportedMachine(Supported));
    }

    [Fact]
    public void TheModelNameStringPathIsGoneFromTheGate()
    {
        string source = SourceFile("Hardware", "EcChargeLimit.cs");
        Assert.DoesNotContain("YAOSHI", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GetModel", source);
    }

    internal static string SourceFile(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, Path.Combine("src", "MechrevoLiteWin", Path.Combine(tail))));
    }
}
