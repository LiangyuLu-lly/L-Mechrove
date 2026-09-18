using System.Globalization;
using System.Text.RegularExpressions;

namespace MechrevoLite.Gpu;

/// <summary>
/// **轴 2**：dGPU 代际。与轴 1（平台/机箱代号）正交，只由运行时 GPU 信号决定。
/// <para><see cref="Unknown"/>（无法判定）与 <see cref="NoDgpu"/>（确认没有独显）是**两个独立状态**，
/// 绝不并入某个默认代际——把"读不到"当成 40 系就会在别的机器上放开错误的显示路由动作。</para>
/// </summary>
public enum DgpuGenerationKind
{
    /// <summary>判据缺失或不在取值域 {30,40,50}（含 20 系及更早）。</summary>
    Unknown,

    /// <summary>枚举可用且没有任何 NVIDIA 独显。</summary>
    NoDgpu,

    Gen30,
    Gen40,
    Gen50,
}

/// <summary>代际判据来源。<see cref="None"/> 表示没有可用信号。</summary>
public enum DgpuProbeSource
{
    None,

    /// <summary>GPU 营销名（例如 <c>NVIDIA GeForce RTX 4060 Laptop GPU</c>）。</summary>
    MarketingName,

    /// <summary>NVIDIA PCI device-id 的高字节（INFERRED 区间，见 <see cref="DgpuGenerationProbe"/>）。</summary>
    PciDeviceId,
}

/// <summary>
/// 一张显卡的运行时标识。<see cref="VendorId"/> 是 PCI vendor（NVIDIA = <c>10DE</c>），
/// <see cref="DeviceId"/> 是 16 位 device-id 的十六进制字符串（例如 <c>2882</c>）。
/// </summary>
public sealed record GpuAdapter(string Name, string? VendorId, string? DeviceId)
{
    public const string NvidiaVendorId = "10DE";

    public bool IsNvidia => string.Equals(VendorId, NvidiaVendorId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一次 dGPU 代际探测的结果。<see cref="HasDgpu"/> 与 <see cref="Generation"/> 独立：
/// 存在 NVIDIA 独显但判不出代际时是 <c>Generation=Unknown, HasDgpu=true</c>；没有独显时是
/// <c>Generation=NoDgpu, HasDgpu=false</c>。
/// </summary>
public sealed record DgpuIdentity(
    DgpuGenerationKind Generation,
    DgpuProbeSource Source,
    bool HasDgpu,
    string? MarketingName,
    string? DeviceId)
{
    /// <summary>判据完全缺失（枚举不可用）。</summary>
    public static readonly DgpuIdentity Unknown =
        new(DgpuGenerationKind.Unknown, DgpuProbeSource.None, false, null, null);

    /// <summary>枚举可用且没有 NVIDIA 独显。</summary>
    public static readonly DgpuIdentity NoDgpu =
        new(DgpuGenerationKind.NoDgpu, DgpuProbeSource.None, false, null, null);

    /// <summary>是否判到了取值域内的代际。</summary>
    public bool IsResolved => Generation is DgpuGenerationKind.Gen30
        or DgpuGenerationKind.Gen40 or DgpuGenerationKind.Gen50;
}

/// <summary>
/// **轴 2** 代际探测：只吃 GPU 营销名与 NVIDIA PCI device-id，**绝不接受 <c>BIOS_PROJECT_ID</c>**
/// （它是平台码，不是代际，见 <see cref="MechrevoLite.Hardware.ModelRegistry"/> 的说明）。
///
/// <para>顺序：先按营销名（确定性高），名不可解析再按 device-id 高字节（INFERRED 区间）。
/// 两者都判不出而确实有 NVIDIA 独显时返回 <c>Unknown + HasDgpu=true</c>。</para>
///
/// <para>纯函数、不碰硬件：硬件读取在 <see cref="GpuAdapterReader"/>。</para>
/// </summary>
public static class DgpuGenerationProbe
{
    /// <summary>
    /// 探测代际。<paramref name="enumerationAvailable"/> 为 <c>false</c> 表示枚举本身失败——
    /// 此时返回 <see cref="DgpuIdentity.Unknown"/>，绝不能推断成"无独显"。
    /// </summary>
    public static DgpuIdentity Resolve(IReadOnlyList<GpuAdapter> adapters, bool enumerationAvailable)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (!enumerationAvailable) return DgpuIdentity.Unknown;

        GpuAdapter[] nvidia = adapters.Where(adapter => adapter.IsNvidia).ToArray();
        if (nvidia.Length == 0) return DgpuIdentity.NoDgpu;

        // 名称是确定性更高的信号，先全表扫名称再退回 device-id 区间。
        foreach (GpuAdapter adapter in nvidia)
        {
            if (FromMarketingName(adapter.Name) is { } byName)
                return new DgpuIdentity(byName, DgpuProbeSource.MarketingName, true, adapter.Name, adapter.DeviceId);
        }
        foreach (GpuAdapter adapter in nvidia)
        {
            if (FromDeviceId(adapter.DeviceId) is { } byDeviceId)
                return new DgpuIdentity(byDeviceId, DgpuProbeSource.PciDeviceId, true, adapter.Name, adapter.DeviceId);
        }

        // 确有 NVIDIA 独显但代际判不出：Unknown + HasDgpu=true，绝不落到某个默认代际。
        GpuAdapter first = nvidia[0];
        return new DgpuIdentity(DgpuGenerationKind.Unknown, DgpuProbeSource.None, true, first.Name, first.DeviceId);
    }

    /// <summary>营销名 -&gt; 代际；只有 30/40/50 在取值域内，其余（含 20 系）返回 <c>null</c>。</summary>
    public static DgpuGenerationKind? FromMarketingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // "GeForce RTX 4060 Laptop GPU" -> 4060；A5000 之类字母型号不匹配。
        Match match = Regex.Match(name, @"\bRTX\s*(\d{4})\b", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        return match.Groups[1].Value[..2] switch
        {
            "30" => DgpuGenerationKind.Gen30,
            "40" => DgpuGenerationKind.Gen40,
            "50" => DgpuGenerationKind.Gen50,
            _ => null,
        };
    }

    /// <summary>NVIDIA device-id -&gt; 代际；判不出返回 <c>null</c>。</summary>
    public static DgpuGenerationKind? FromDeviceId(string? deviceId)
    {
        if (!TryParseHex16(deviceId, out int value)) return null;

        int high = (value >> 8) & 0xFF;
        return high switch
        {
            // Ampere GA10x（0x20..0x25）/ Ada AD10x（0x26..0x28）/ Blackwell GB20x（0x2B..0x30）。
            // 区间是 INFERRED：名称可用时名称优先（见 Resolve）。
            >= 0x20 and <= 0x25 => DgpuGenerationKind.Gen30,
            >= 0x26 and <= 0x28 => DgpuGenerationKind.Gen40,
            >= 0x2B and <= 0x30 => DgpuGenerationKind.Gen50,
            _ => null,
        };
    }

    /// <summary>解析 16 位 device-id：接受 <c>2882</c>、<c>0x2882</c> 或含 <c>DEV_2882</c> 的硬件 id。</summary>
    static bool TryParseHex16(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string token = text.Trim();
        int devAt = token.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (devAt >= 0) token = token[(devAt + 4)..];
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token[2..];
        token = token.Trim();
        if (token.Length != 4) return false;

        return int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
