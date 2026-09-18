using MechrevoLite.Hardware;

namespace MechrevoLite.Fan;

/// <summary>
/// EC <c>0x751</c>（1873）风扇模式 + 增压位编解码，逐条对齐厂商
/// <c>MyFanManager_RamFan1p5_NV.SetFanMode/SetFanBoost/GetFanMode</c>（<c>:1028-1111</c>）：
/// 基础字节 mode0=<c>0x00</c> / mode1=<c>0xA0</c> / mode2=<c>0x10</c>，增压 = bit6（<c>0x40</c>）；
/// 读回 = bit4（低位）+ bit7（高位）。
///
/// <para><b>只读语义</b>：本类不含 EC 访问（无 IOCTL/DllImport），编解码纯函数；
/// 控制台不写 EC，解码用于读取服务/EC 上报，编码用于往返校验与诊断。</para>
/// </summary>
public static class FanBoostEncoding
{
    public const int Address = 1873;   // 0x751
    public const int BoostMask = 0x40; // bit6
    public const int ModeBitLow = 4;
    public const int ModeBitHigh = 7;

    /// <summary>逻辑模式（= <c>GetFanMode</c> 读回值）→ 基础字节。</summary>
    static readonly int[] Bases = { 0x00, 0xA0, 0x10 };

    /// <summary>编码：逻辑模式 0/1/2 + 是否增压。</summary>
    public static byte Encode(int logicalMode, bool boostEnabled)
    {
        if (logicalMode is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(logicalMode), logicalMode, "logical mode must be 0..2");
        int value = Bases[logicalMode];
        if (boostEnabled) value |= BoostMask;
        return (byte)value;
    }

    /// <summary>解码 bit4+bit7 回逻辑模式；bit4=1 且 bit7=1 时按厂商返回 0。</summary>
    public static int Decode(byte value)
    {
        bool low = (value & (1 << ModeBitLow)) != 0;
        bool high = (value & (1 << ModeBitHigh)) != 0;
        if (!low && !high) return 0;
        if (!low && high) return 1;
        if (low && !high) return 2;
        return 0;   // 两位同置：厂商 GetFanMode 保留 result=0。
    }

    /// <summary>增压位（bit6）是否置位。</summary>
    public static bool BoostEnabled(byte value) => (value & BoostMask) != 0;

    /// <summary>
    /// 厂商 <c>SetFanMode</c> 的**入参**与读回逻辑模式的对应（厂商入参编号有偏移）：
    /// 1→0、0→1、2→2。
    /// </summary>
    public static int LogicalModeForSetterParameter(int parameter) => parameter switch
    {
        1 => 0,
        0 => 1,
        2 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "setter parameter must be 0/1/2"),
    };

    /// <summary>该机型是否支持风扇增压（门控走 <see cref="FeatureMatrix"/>，缺值 fail-closed）。</summary>
    public static bool IsAvailable(FeatureMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return matrix.IsSupported(FeatureBit.FanBoost);
    }
}

/// <summary>
/// EC <c>1859-1862</c> 的 cTGP / DynamicBoost 编码（厂商
/// <c>SetGpuConfigurableTgpFunCtrlEnable/SetGpuDynamicBoostEnable</c>，GCUService <c>24815-24872</c>）：
/// 1859 bit2=cTGP enable、bit1=DB enable、bit0=DB fun-ctrl；1860 cTGP 目标增量；1861/1862 DB 瓦数。
///
/// <para>只读语义同 <see cref="FanBoostEncoding"/>：控制台不写 EC，仅解码与往返校验。</para>
/// </summary>
public static class GpuPowerControlEncoding
{
    public const int ControlAddress = 1859;
    public const int ConfigurableTgpAddress = 1860;
    public const int DynamicBoostAddress = 1861;
    public const int MaximumTgpAddress = 1862;

    public const int CtgpEnableBit = 2;
    public const int DynamicBoostEnableBit = 1;
    public const int DynamicBoostFunCtrlBit = 0;

    /// <summary>编码 1859 控制位。</summary>
    public static byte EncodeControl(bool ctgpEnabled, bool dynamicBoostEnabled, bool dynamicBoostFunCtrl)
    {
        int value = 0;
        if (ctgpEnabled) value |= 1 << CtgpEnableBit;
        if (dynamicBoostEnabled) value |= 1 << DynamicBoostEnableBit;
        if (dynamicBoostFunCtrl) value |= 1 << DynamicBoostFunCtrlBit;
        return (byte)value;
    }

    /// <summary>解码 1859 控制位三元组。</summary>
    public static (bool CtgpEnabled, bool DynamicBoostEnabled, bool DynamicBoostFunCtrl) DecodeControl(byte value) =>
        ((value & (1 << CtgpEnableBit)) != 0,
         (value & (1 << DynamicBoostEnableBit)) != 0,
         (value & (1 << DynamicBoostFunCtrlBit)) != 0);
}
