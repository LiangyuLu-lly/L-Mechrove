using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Mode;

/// <summary>
/// 逐模式 PL/Tcc **默认值**来源。厂商命名：Gaming / Office / <b>Turbo</b>
/// （<c>GetTurboPLDefaultValue</c>，**不是** BatterySaver——40B 里 1959-1962 就是 Turbo 默认值）。
/// </summary>
public enum PlDefaultMode
{
    Gaming,
    Office,
    Turbo,
}

/// <summary>一组默认值。**读不到就是 null**；绝不填猜测的瓦数。</summary>
public sealed record PlDefaultSet(int? Pl1, int? Pl2, int? Pl4, int? TccOffset)
{
    public static readonly PlDefaultSet Unavailable = new(null, null, null, null);

    public bool IsAvailable => Pl1.HasValue && Pl2.HasValue && Pl4.HasValue && TccOffset.HasValue;
}

/// <summary>
/// 读默认值的结果。<see cref="Editable"/> 为 false 时 UI **必须禁用该模式的功耗编辑**，
/// 且不得显示任何硬编码瓦数（BLOCKED-HW 的正是"真机数值"这一半）。
/// </summary>
public sealed record PlDefaultsResult(PlDefaultMode? Mode, PlDefaultSet Values, bool Editable, string Reason)
{
    public static PlDefaultsResult Unavailable(PlDefaultMode? mode, string reason) =>
        new(mode, PlDefaultSet.Unavailable, false, reason);
}

/// <summary>
/// 逐 SKU 从 EC 读 PL/Tcc 默认值（GCUService <c>65047-65096</c>）：
/// Gaming <c>1840-1843</c>、Office <c>1844-1847</c>、Turbo <c>1959-1962</c>、Tcc <c>2008-2010</c>。
///
/// <para><b>fail-closed</b>：任一字节读不到（-1）→ 整组不可用、禁用编辑，绝不猜。只读，不写 EC。</para>
/// </summary>
public static class PlDefaults
{
    public static readonly int[] GamingAddresses = { 1840, 1841, 1842, 1843 };
    public static readonly int[] OfficeAddresses = { 1844, 1845, 1846, 1847 };
    public static readonly int[] TurboAddresses = { 1959, 1960, 1961, 1962 };

    /// <summary>逐模式 Tcc 默认偏移：Gaming=2008 / Office=2009 / Turbo=2010。</summary>
    public static readonly int[] TccAddresses = { 2008, 2009, 2010 };

    /// <summary>该模式的 PL 地址（顺序 = Pl1/Pl2/Pl4 与第四字节）。</summary>
    public static int[] AddressesFor(PlDefaultMode mode) => mode switch
    {
        PlDefaultMode.Gaming => GamingAddresses,
        PlDefaultMode.Office => OfficeAddresses,
        PlDefaultMode.Turbo => TurboAddresses,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown PL default mode"),
    };

    /// <summary>该模式的 Tcc 默认偏移地址。</summary>
    public static int TccAddressFor(PlDefaultMode mode) => TccAddresses[(int)mode];

    /// <summary>视觉枚举（<c>MechrevoService.Mode*</c>）→ 默认值模式；Customize 没有 EC 默认组（null）。</summary>
    public static PlDefaultMode? ModeForVisualMode(int visualMode) => visualMode switch
    {
        MechrevoService.ModeGaming => PlDefaultMode.Gaming,
        MechrevoService.ModeOffice => PlDefaultMode.Office,
        MechrevoService.ModeTurbo => PlDefaultMode.Turbo,
        _ => null,
    };

    /// <summary>读一组默认值；任一地址读不到（-1）或抛异常即整组不可用。</summary>
    public static PlDefaultsResult Read(PlDefaultMode mode, Func<int, int> readByte)
    {
        ArgumentNullException.ThrowIfNull(readByte);

        int[] addresses = AddressesFor(mode);
        var values = new int[addresses.Length];
        try
        {
            for (int i = 0; i < addresses.Length; i++)
            {
                int value = readByte(addresses[i]);
                if (value < 0)
                    return PlDefaultsResult.Unavailable(mode, $"EC {addresses[i]} unreadable; disabling PL editing for {mode}");
                values[i] = value;
            }

            int tccAddress = TccAddressFor(mode);
            int tcc = readByte(tccAddress);
            if (tcc < 0)
                return PlDefaultsResult.Unavailable(mode, $"EC {tccAddress} unreadable; disabling PL editing for {mode}");

            return new PlDefaultsResult(
                mode, new PlDefaultSet(values[0], values[1], values[2], tcc), true, $"EC defaults read for {mode}");
        }
        catch (Exception ex)
        {
            // EC 传输是外部边界：单次读故障只作废这组默认值，不上抛给 UI。
            return PlDefaultsResult.Unavailable(mode, $"EC read failed for {mode}: {ex.Message}");
        }
    }

    /// <summary>只读传输重载。</summary>
    public static PlDefaultsResult Read(PlDefaultMode mode, IEcReadTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Read(mode, transport.ReadByte);
    }
}
