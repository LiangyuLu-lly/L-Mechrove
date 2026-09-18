using System.Reflection;
using System.Reflection.Emit;
using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T15 的 EC 写反射守卫：显式枚举 <c>L-Mechrevo</c> + <c>Probe</c> 两个程序集，逐方法解 IL，
/// 检测**设备中介**（ECWRITE IOCTL 0x9C40A48C、WRITE_PORT_* IOCTL）与**非设备中介**
/// （经 <c>PawnIO</c> 载体的 EC 写）两条路，以及门铃写实现（写 IOCTL + 3933/3934/3935）。
///
/// <para>不是源码 grep：读的是编译后的 IL 立即数与调用目标。意图 = 除既有已证明逐字节等价的
/// 限充（<c>EcChargeLimit</c>）与 Probe 诊断 CLI 外，**不新增任何 EC 写路径**。</para>
/// </summary>
internal static class EcWriteGuard
{
    /// <summary>厂商 ECWRITE IOCTL（<c>[u32 地址][u8 值]</c>）。</summary>
    internal const int EcWriteIoctl = unchecked((int)0x9C40A48C);

    /// <summary>端口级写 IOCTL（WRITE_PORT_UCHAR/USHORT/ULONG，docs/hardware/ec-registers.json）。</summary>
    internal static readonly int[] WritePortIoctls =
    {
        unchecked((int)0x9C40A440), unchecked((int)0x9C40A444), unchecked((int)0x9C40A448),
    };

    /// <summary>门铃地址（写实现必须碰它们）。</summary>
    internal static readonly int[] DoorbellAddresses = { 3933, 3934, 3935 };

    /// <summary>
    /// 允许存在的 EC 写类型（已证逐字节等价 / 既有诊断 CLI）。
    /// <c>EcChargeLimit</c> = 产品唯一 EC 写（限充）；<c>EcProbe</c> = Probe 诊断 CLI，<c>IsPublishable=false</c>。
    /// </summary>
    internal static readonly string[] PermittedEcWriters =
    {
        "MechrevoLite.Hardware.EcChargeLimit",
        "Probe.EcProbe",
    };

    internal static IReadOnlyList<Assembly> GuardedAssemblies { get; } =
        new[] { typeof(MechrevoHw).Assembly, typeof(IEcReadTransport).Assembly };

    internal sealed record Hit(string Assembly, string Type, string Method, string Signal);

    internal static IReadOnlyList<Hit> Scan()
    {
        var hits = new List<Hit>();
        foreach (Assembly assembly in GuardedAssemblies)
            foreach (Type type in assembly.GetTypes())
                foreach (MethodBase method in EnumerateMethods(type))
                {
                    byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null || il.Length == 0) continue;

                    IlScanResult scan = IlScan.Run(method.Module, il);
                    bool callsPawn = scan.Calls.Any(called =>
                        (called.DeclaringType?.Namespace ?? string.Empty).StartsWith("PawnIO", StringComparison.Ordinal));
                    bool hasDoorbell = scan.Immediates.Any(value => DoorbellAddresses.Contains(value));
                    bool hasEcRange = scan.Immediates.Any(IsEcRange);
                    bool writesEc = scan.Immediates.Contains(EcWriteIoctl);

                    if (writesEc) hits.Add(new(assembly.GetName().Name!, type.FullName!, method.Name, "EC_WRITE_IOCTL"));
                    if (scan.Immediates.Any(WritePortIoctls.Contains))
                        hits.Add(new(assembly.GetName().Name!, type.FullName!, method.Name, "WRITE_PORT_IOCTL"));
                    if (callsPawn && (hasEcRange || hasDoorbell))
                        hits.Add(new(assembly.GetName().Name!, type.FullName!, method.Name, "PAWN_CARRIER_EC_WRITE"));
                    if ((writesEc || scan.Immediates.Any(WritePortIoctls.Contains)) && hasDoorbell)
                        hits.Add(new(assembly.GetName().Name!, type.FullName!, method.Name, "DOORBELL_WRITE"));
                }
        return hits;
    }

    static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(flags)) yield return method;
        foreach (MethodBase method in type.GetConstructors(flags)) yield return method;
    }

    static bool IsEcRange(int value) =>
        (value >= 0x700 && value <= 0x7FF) ||
        (value >= 0xF00 && value <= 0xFFF) ||
        value is 1856 or 1868 or 1934 or 1994;

    internal sealed record IlScanResult(IReadOnlyList<int> Immediates, IReadOnlyList<MethodBase> Calls);

    /// <summary>最小 IL 解码器：只取 <c>ldc.i4*</c> 立即数与 <c>call/callvirt</c> 目标。</summary>
    static class IlScan
    {
        static readonly Dictionary<ushort, OpCode> Opcodes = Build();

        internal static IlScanResult Run(Module module, byte[] il)
        {
            var immediates = new List<int>();
            var calls = new List<MethodBase>();
            int at = 0;

            while (at < il.Length)
            {
                ushort code = il[at];
                if (code == 0xFE)
                {
                    if (at + 1 >= il.Length) break;
                    code = (ushort)(0xFE00 | il[at + 1]);
                    at += 2;
                }
                else at += 1;

                if (!Opcodes.TryGetValue(code, out OpCode op)) break;

                if (op == OpCodes.Ldc_I4_M1) { immediates.Add(-1); continue; }
                if (op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value)
                {
                    immediates.Add(op.Value - OpCodes.Ldc_I4_0.Value);
                    continue;
                }
                if (op == OpCodes.Ldc_I4_S) { if (at >= il.Length) break; immediates.Add((sbyte)il[at]); at += 1; continue; }
                if (op == OpCodes.Ldc_I4) { if (at + 4 > il.Length) break; immediates.Add(BitConverter.ToInt32(il, at)); at += 4; continue; }
                if (op == OpCodes.Ldc_I8) { at += 8; continue; }

                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        at += 1;
                        break;
                    case OperandType.InlineVar:
                        at += 2;
                        break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineString:
                    case OperandType.InlineSig:
                        if (at + 4 > il.Length) break;
                        if (op == OpCodes.Call || op == OpCodes.Callvirt)
                        {
                            try
                            {
                                if (module.ResolveMethod(BitConverter.ToInt32(il, at)) is MethodBase target)
                                    calls.Add(target);
                            }
                            catch { /* unresolvable token: not a call target we care about */ }
                        }
                        at += 4;
                        break;
                    case OperandType.ShortInlineR:
                        at += 4;
                        break;
                    case OperandType.InlineR:
                        at += 8;
                        break;
                    case OperandType.InlineSwitch:
                        if (at + 4 > il.Length) { at = il.Length; break; }
                        at += 4 + 4 * BitConverter.ToInt32(il, at);
                        break;
                    default:
                        at += 4;
                        break;
                }

                if (at < 0) break;
            }

            return new(immediates, calls);
        }

        static Dictionary<ushort, OpCode> Build()
        {
            var map = new Dictionary<ushort, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode opcode)
                    map[(ushort)opcode.Value] = opcode;
            return map;
        }
    }
}
