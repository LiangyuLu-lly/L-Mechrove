using Probe;

namespace MechrevoLite.Fan;

/// <summary>风扇表规格类（EC 寄存器布局族）。厂商按 EC 能力位二选一，第三类没有实现路径。</summary>
public enum FanSpecClass
{
    /// <summary>传统 <c>RamFan1_ECSpec</c>：六个基址、无门铃。</summary>
    RamFan1,

    /// <summary><c>RamFan1p5_ECSpec</c>：六个基址 + 门铃 3933/3934/3935；商用机型走逐机型目录。</summary>
    RamFan1p5,

    /// <summary><c>RamFan2_ECSpec</c>：三段堆叠表；<c>FanTable_Manager2</c> 在 5.17.51 反编译里从未实例化——不可达。</summary>
    RamFan2,
}

/// <summary>
/// 规格类选择器：严格按 **EC 1934 bit6**（厂商 <c>MyEcCtrl.IsSuportRamFan1p5()</c>，
/// <c>MyEcCtrl.cs:233-238</c>）在 <see cref="FanSpecClass.RamFan1"/> /
/// <see cref="FanSpecClass.RamFan1p5"/> 之间选择。
///
/// <para>读失败按厂商语义落到 <see cref="FanSpecClass.RamFan1"/>：厂商用 <c>ref byte</c>（初值 0）
/// 接读值，失败时 bit6 为 0，走 legacy 路径——不猜 1p5。</para>
///
/// <para><b><see cref="FanSpecClass.RamFan2"/> 显式不可达</b>：5.17.51 反编译中
/// <c>FanTable_Manager2</c> 无任何构造点，把 1p5 表按它解释必须报错，不得静默选中。
/// 只读：本类不写 EC。</para>
/// </summary>
public static class FanSpecSelector
{
    /// <summary>能力字节地址（厂商 <c>IsSuportRamFan1p5</c> 的 1934）。</summary>
    public const int CapabilityAddress = 1934;

    /// <summary>RamFan1p5 能力位（bit6 / 0x40）。</summary>
    public const int RamFan1p5CapabilityBit = 6;

    /// <summary>只读 EC 1934，取 bit6。传输异常按"无能力"处理，不上抛。</summary>
    public static bool HasRamFan1p5(IEcReadTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            int value = transport.ReadByte(CapabilityAddress);
            return value >= 0 && (value & (1 << RamFan1p5CapabilityBit)) != 0;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("FanSpecSelector capability read failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>能力位 -&gt; 规格类。<b>绝不返回 <see cref="FanSpecClass.RamFan2"/></b>。</summary>
    public static FanSpecClass Select(bool ramFan1p5Capability) =>
        ramFan1p5Capability ? FanSpecClass.RamFan1p5 : FanSpecClass.RamFan1;

    /// <summary>读一次能力位并选规格类。</summary>
    public static FanSpecClass SelectFor(IEcReadTransport transport) => Select(HasRamFan1p5(transport));

    /// <summary>该类是否有实现路径。唯一不可达的是 <see cref="FanSpecClass.RamFan2"/>。</summary>
    public static bool IsReachable(FanSpecClass specClass) => specClass != FanSpecClass.RamFan2;

    /// <summary>声明了不可达的规格类即抛 <see cref="NotSupportedException"/>——显式拒绝而非静默降级。</summary>
    public static FanSpecClass RequireReachable(FanSpecClass declared) =>
        IsReachable(declared)
            ? declared
            : throw new NotSupportedException(
                $"{declared} has no implementation path: FanTable_Manager2 is never instantiated");
}
