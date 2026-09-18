namespace MechrevoLite.Tests;

/// <summary>
/// T15（Wave C）失败/边界面：反射级断言锁定"不新增 EC 写路径"——除已证逐字节等价的限充
/// （<c>EcChargeLimit</c>）与既有 Probe 诊断 CLI 外，两个程序集里不得再有 EC 写类型；
/// 端口级写、经 <c>PawnIO</c> 载体的 EC 写、门铃写实现一律为零。
/// </summary>
public class FanTableNoEcWriteFailTests
{
    static readonly IReadOnlyList<EcWriteGuard.Hit> Hits = EcWriteGuard.Scan();

    [Fact]
    public void TheGuardCoversExactlyTwoAssembliesWithTypes()
    {
        // 防空扫描假绿：必须确实枚举了两个程序集且类型数 > 0。
        Assert.Equal(2, EcWriteGuard.GuardedAssemblies.Distinct().Count());
        Assert.True(EcWriteGuard.GuardedAssemblies.Sum(assembly => assembly.GetTypes().Length) > 0);
    }

    [Fact]
    public void NoEcWriteTypeOutsideThePermittedSetExists()
    {
        string[] writers = Hits
            .Where(hit => hit.Signal == "EC_WRITE_IOCTL")
            .Select(hit => hit.Type)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            EcWriteGuard.PermittedEcWriters.OrderBy(name => name, StringComparer.Ordinal),
            writers);
    }

    [Fact]
    public void TheProductAssemblyWritesEcOnlyFromTheChargeLimit()
    {
        string[] writers = Hits
            .Where(hit => hit.Signal == "EC_WRITE_IOCTL" && hit.Assembly == "L-Mechrevo")
            .Select(hit => hit.Type)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "MechrevoLite.Hardware.EcChargeLimit" }, writers);
    }

    [Fact]
    public void NoPortIoEcWriteExists()
    {
        Assert.DoesNotContain(Hits, hit => hit.Signal == "WRITE_PORT_IOCTL");
    }

    [Fact]
    public void NoEcWriteRidesThePawnCarrier()
    {
        // PawnIO 是非 EC 直写的既有载体；不得经它去写 EC。
        Assert.DoesNotContain(Hits, hit => hit.Signal == "PAWN_CARRIER_EC_WRITE");
    }

    [Fact]
    public void NoDoorbellWriteImplementationExists()
    {
        // 门铃写实现必须同时具备写 IOCTL 与 3933/3934/3935——两样都没有即未实现（本任务只做只读校验）。
        Assert.DoesNotContain(Hits, hit => hit.Signal == "DOORBELL_WRITE");
    }

    [Fact]
    public void TheFanNamespaceAddsNoEcWriteSurface()
    {
        Assert.DoesNotContain(Hits, hit => hit.Type.StartsWith("MechrevoLite.Fan", StringComparison.Ordinal));
    }
}
