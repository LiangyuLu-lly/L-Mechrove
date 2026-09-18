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

    [Fact]
    public void ASupportedModelWithAServiceProfileIsAllowed()
    {
        using var _ = Force(null);
        Assert.True(EcChargeLimit.IsSupportedMachine(Supported, ServiceProfile));
    }

    [Fact]
    public void AForceOnSwitchEnablesTheChannelRegardlessOfMatrixAndIdentity()
    {
        using var _ = Force("1");
        Assert.True(EcChargeLimit.IsSupportedMachine(Unparsable, EmptyProfile));
    }

    [Fact]
    public void AForceOffSwitchDisablesTheChannelRegardlessOfMatrixAndIdentity()
    {
        using var _ = Force("0");
        Assert.False(EcChargeLimit.IsSupportedMachine(Supported, ServiceProfile));
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
