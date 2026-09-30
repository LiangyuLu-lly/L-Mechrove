using System.Reflection;
using System.Reflection.Emit;
using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T16 的固件变量写守卫：显式枚举 <c>L-Mechrevo</c> + <c>Probe</c> 两个程序集，逐方法解 IL，
/// 查找 <c>OemDisplayMode</c> / <c>SetFwVars</c> 之类的**固件变量写接缝**（字符串字面量与同名调用）。
///
/// <para>绑定规则（勿重开）：显示路由固件变量只走 MQTT，控制台**不写** <c>OemDisplayMode</c>、
/// 不存在写固件变量的接缝。这不是源码 grep——读的是编译后的 IL。</para>
/// </summary>
internal static class FirmwareWriteGuard
{
    /// <summary>字符串字面量里禁止出现的固件变量/写入口标记。</summary>
    internal static readonly string[] ForbiddenStringTokens =
    {
        "OemDisplayMode", "SetFwVars", "SetFirmwareVariable", "SetNvramVariable", "SetUefiVariable",
    };

    /// <summary>调用目标名里禁止出现的**写**动词（只禁写，不禁"读到/记录这个变量名"）。</summary>
    internal static readonly string[] ForbiddenCallTokens =
    {
        "SetFwVars", "SetFirmware", "SetNvram", "SetUefi", "WriteFirmware", "WriteNvram", "WriteUefi",
    };

    internal static int AssembliesScanned { get; private set; }

    internal static int TypesScanned { get; private set; }

    internal static IReadOnlyList<string> Scan()
    {
        var hits = new List<string>();
        Assembly[] assemblies = { typeof(MechrevoHw).Assembly, typeof(IEcReadTransport).Assembly };
        AssembliesScanned = assemblies.Length;
        TypesScanned = 0;

        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                TypesScanned++;
                foreach (MethodBase method in Methods(type))
                {
                    byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null || il.Length == 0) continue;

                    IlScan scan = IlScanner.Run(method.Module, il);
                    foreach (string text in scan.Strings)
                    {
                        // 只认把标记当作**标识符**用的字面量（`"OemDisplayMode"` / P/Invoke 入口名）；
                        // 证据说明里的散文提到这个变量名不算接缝。
                        string literal = text.Trim();
                        if (ForbiddenStringTokens.Contains(literal, StringComparer.OrdinalIgnoreCase))
                            hits.Add($"{assembly.GetName().Name}:{type.FullName}.{method.Name} -> \"{text}\"");
                    }
                    foreach (MethodBase called in scan.Calls)
                    {
                        if (ForbiddenCallTokens.Any(token => called.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                            hits.Add($"{assembly.GetName().Name}:{type.FullName}.{method.Name} -> calls {called.DeclaringType?.FullName}.{called.Name}");
                    }
                }
            }
        }
        return hits;
    }

    static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(flags)) yield return method;
        foreach (MethodBase ctor in type.GetConstructors(flags)) yield return ctor;
    }

    internal sealed record IlScan(IReadOnlyList<string> Strings, IReadOnlyList<MethodBase> Calls);

    /// <summary>最小 IL 解码器：只取 <c>ldstr</c> 字符串与 <c>call/callvirt</c> 目标。</summary>
    internal static class IlScanner
    {
        static readonly Dictionary<ushort, OpCode> Opcodes = Build();

        internal static IlScan Run(Module module, byte[] il)
        {
            var strings = new List<string>();
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
                        if (at + 4 > il.Length) { at = il.Length; break; }
                        int token = BitConverter.ToInt32(il, at);
                        if (op == OpCodes.Call || op == OpCodes.Callvirt)
                        {
                            try
                            {
                                if (module.ResolveMethod(token) is MethodBase target) calls.Add(target);
                            }
                            catch { /* unresolvable token: not a call target we care about */ }
                        }
                        else if (op == OpCodes.Ldstr)
                        {
                            try { strings.Add(module.ResolveString(token)); }
                            catch { /* unresolvable string token */ }
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

            return new(strings, calls);
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
