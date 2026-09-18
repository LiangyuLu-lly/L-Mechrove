namespace MechrevoLite.Hardware;

/// <summary>服务写入值缺失时的处置策略。</summary>
public enum FeatureMissingPolicy
{
    /// <summary>EC / NVRAM 派生位：服务没写 = 不支持（fail-closed）。</summary>
    FailClosed,

    /// <summary>厂商常量位：服务没写仍镜像厂商默认「开」。</summary>
    VendorConstantOn,

    /// <summary>厂商常量位「仅非商用机型为 1」：服务没写时按是否商用决定。</summary>
    VendorConstantNonCommercial,
}

/// <summary>
/// 机型 × 功能矩阵的一个能力位。<see cref="Key"/> 是厂商服务写入
/// <c>ItemSupport</c>/<c>GpuConfig</c> 的注册表值名，逐条对照
/// <c>GCUService.decompiled.cs:27164-27241</c>。
/// </summary>
public enum FeatureBit
{
    AcRecoverySwitch,
    AcRecoveryBios,
    Keyboard,
    SystemMonitor,
    FanSettings,
    OverclockSettings,
    ColorCalibration,
    DgpuDirect,
    FanBoost,
    AmdPlatform,
    NvidiaGpu,
    Lightbar,
    RgbLightbar,
    Numpad,
    LiquidCooling,
    LiquidCoolingAutoMode,
    RamFan15,
    TurboMode,
    TypeC,
    Commercial,
    CommercialHave20Db,
    GpuHotSwapSwitch,
    HotSwapStatus,
}

/// <summary>对照表的一行：能力位 → 服务键 → 缺失处置。</summary>
public sealed record FeatureBitDefinition(FeatureBit Bit, string Key, FeatureMissingPolicy Missing);

/// <summary>
/// 机型 × 功能可用性矩阵（**轴 1**）。
///
/// <para><b>单一真源</b>：厂商服务写入的 <c>ItemSupport</c>（<c>GpuConfig</c> 同规则）是唯一来源，
/// 控制台只读、<b>不重推厂商判据</b>。存在即照用（哪怕与"常量"印象相反）；仅当缺失时才按
/// <see cref="FeatureMissingPolicy"/> 处置——EC/NVRAM 派生位 fail-closed，厂商常量位镜像默认。</para>
///
/// <para>本类不含任何 EC 访问、IOCTL 或写路径：构造上碰不到硬件。</para>
/// </summary>
public sealed class FeatureMatrix
{
    static readonly FeatureBitDefinition[] Table =
    {
        // 常量位（厂商侧恒为 1 或按是否商用派生）——读失败不得被禁用。
        new(FeatureBit.AcRecoverySwitch, "AcRecoverySwitchSupport", FeatureMissingPolicy.VendorConstantOn),
        new(FeatureBit.Keyboard, "KeyboardSupport", FeatureMissingPolicy.VendorConstantOn),
        new(FeatureBit.SystemMonitor, "SystemMonitorSupport", FeatureMissingPolicy.VendorConstantOn),
        new(FeatureBit.FanSettings, "FanSettingsSupport", FeatureMissingPolicy.VendorConstantNonCommercial),
        new(FeatureBit.OverclockSettings, "OcSettingsSupport", FeatureMissingPolicy.VendorConstantNonCommercial),
        // EC / NVRAM 派生位——缺值 fail-closed。
        new(FeatureBit.AcRecoveryBios, "AcRecoverySwitchBiosSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.ColorCalibration, "ColorCalibrationSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.DgpuDirect, "DGpuDirectConnectionSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.FanBoost, "FanBoostBtnSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.AmdPlatform, "IsAMDPlatform", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.NvidiaGpu, "IsNvGpu", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.Lightbar, "LightbarSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.RgbLightbar, "RGBLightbarSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.Numpad, "NumPadSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.LiquidCooling, "LiquidCoolingSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.LiquidCoolingAutoMode, "LiquidCoolingAutoModeSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.RamFan15, "RamFan1p5Support", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.TurboMode, "TurboModeSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.TypeC, "TypeCSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.Commercial, "IsProjectIdCommercial", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.CommercialHave20Db, "IsProjectIdCommercialHAVE20DB", FeatureMissingPolicy.FailClosed),
        // 热切换（见 T9：判定 = 这两个服务值，缺一不提供）。
        new(FeatureBit.GpuHotSwapSwitch, "GpuHotSwapSwitchSupport", FeatureMissingPolicy.FailClosed),
        new(FeatureBit.HotSwapStatus, "lgpuHotSwapSwitchStatus", FeatureMissingPolicy.FailClosed),
    };

    static readonly IReadOnlyDictionary<FeatureBit, FeatureBitDefinition> ByBit =
        Table.ToDictionary(definition => definition.Bit);

    readonly IReadOnlyDictionary<string, object?> _values;

    FeatureMatrix(IReadOnlyDictionary<string, object?> values)
    {
        _values = values;
        IsCommercial = PresentTruthy("IsProjectIdCommercial");
        ProfileAvailable = values.Count > 0;
    }

    /// <summary>对照表（服务键 → 读法 → 缺失处置）；测试逐位核对。</summary>
    public static IReadOnlyList<FeatureBitDefinition> Definitions => Table;

    /// <summary>服务是否写过至少一个值。全空即画像缺失，非 EC 派生位的默认仍按各自策略。</summary>
    public bool ProfileAvailable { get; }

    /// <summary>厂商分类：商用机型。服务未写时按非商用（与厂商「非商用 → FanSettings=1」默认一致）。</summary>
    public bool IsCommercial { get; }

    /// <summary>
    /// 热切换是否提供：服务必须同时写 <c>GpuHotSwapSwitchSupport</c> 与
    /// <c>lgpuHotSwapSwitchStatus</c>，且两者都为真——缺一即不提供。
    /// </summary>
    public bool GpuHotSwapAvailable =>
        IsPresent(FeatureBit.GpuHotSwapSwitch) && IsPresent(FeatureBit.HotSwapStatus) &&
        IsSupported(FeatureBit.GpuHotSwapSwitch) && IsSupported(FeatureBit.HotSwapStatus);

    /// <summary>服务是否显式写了该位（区分「写了 0」与「没写」）。</summary>
    public bool IsPresent(FeatureBit bit) => _values.ContainsKey(ByBit[bit].Key);

    /// <summary>能力位是否可用：写了照用，没写按缺失策略。</summary>
    public bool IsSupported(FeatureBit bit)
    {
        FeatureBitDefinition definition = ByBit[bit];
        if (_values.TryGetValue(definition.Key, out object? value)) return Truthy(value);
        return definition.Missing switch
        {
            FeatureMissingPolicy.VendorConstantOn => true,
            FeatureMissingPolicy.VendorConstantNonCommercial => !IsCommercial,
            _ => false,
        };
    }

    /// <summary>从服务写入的原始值构造。纯函数——不读注册表、不碰硬件。</summary>
    public static FeatureMatrix FromValues(IReadOnlyDictionary<string, object?> serviceValues)
    {
        ArgumentNullException.ThrowIfNull(serviceValues);
        var copy = new Dictionary<string, object?>(serviceValues, StringComparer.OrdinalIgnoreCase);
        return new FeatureMatrix(copy);
    }

    /// <summary>读当前机器的服务画像并构造（注册表读不到时是全空矩阵，仍按缺失策略）。</summary>
    public static FeatureMatrix Current() => FromValues(MechrevoDeviceCapabilities.ReadServiceValues());

    bool PresentTruthy(string key) => _values.TryGetValue(key, out object? value) && Truthy(value);

    static bool Truthy(object? value)
    {
        if (value is null) return false;
        if (value is bool boolean) return boolean;
        if (int.TryParse(value.ToString(), out int number)) return number > 0;
        return value.ToString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "supported" or "enable" or "enabled" => true,
            _ => false,
        };
    }
}
