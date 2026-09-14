using System.Runtime.InteropServices;

namespace MechrevoLite.Helpers;

/// <summary>
/// 厂商 GCUBridge 内嵌的 MQTT broker 监听在 <c>0.0.0.0:13688</c> 和 <c>[::]:13688</c>，
/// 不是仅本机。凭据是固定写死的，任何能连到这个端口的主机都可以下发风扇曲线、
/// 功耗墙、显卡模式等控制命令。这不是本项目引入的问题，但本项目依赖该 broker，
/// 所以有责任让用户能一键把它关在本机内。
///
/// 此前这里只有 <see cref="HasInboundBlockRule"/> 检测，发现缺规则时仅向日志写一行
/// 警告——日志没人看，等于没有处置手段。现在补上创建/删除规则的能力，
/// 并由 <c>--secure-mqtt</c> / <c>--secure-mqtt-remove</c> 命令行入口驱动（需管理员）。
///
/// 规则只拦入站，不影响本机 127.0.0.1/::1 的连接，因此不会妨碍本程序自身工作。
/// </summary>
public static class MqttSecurity
{
    public const string FirewallRuleName = "L-Mechrevo - Block remote GCU MQTT";
    public const int DefaultPort = 13688;

    const int DirectionInbound = 1;
    const int ActionBlock = 0;
    const int ProtocolTcp = 6;
    const int ProfileAll = int.MaxValue;

    public static bool HasInboundBlockRule(int port = DefaultPort)
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return false;
            policy = Activator.CreateInstance(type);
            if (policy is null) return false;
            dynamic firewall = policy;
            int activeProfiles = firewall.CurrentProfileTypes;
            foreach (dynamic rule in firewall.Rules)
            {
                try
                {
                    if (rule.Enabled && rule.Direction == DirectionInbound && rule.Action == ActionBlock && rule.Protocol == ProtocolTcp
                        && string.Equals((string)rule.Name, FirewallRuleName, StringComparison.Ordinal)
                        && (((int)rule.Profiles & activeProfiles) != 0 || (int)rule.Profiles == ProfileAll)
                        && PortListContains((string?)rule.LocalPorts, port))
                        return true;
                }
                catch (Exception ex) { Logger.WriteLine("Firewall rule inspection failed: " + ex.Message); }
                finally
                {
                    if (Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule);
                }
            }
        }
        catch (Exception ex) { Logger.WriteLine("Firewall policy inspection failed: " + ex.Message); }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
        return false;
    }

    public sealed record OperationResult(bool Success, string Message);

    /// <summary>
    /// 创建入站阻断规则。幂等：已存在生效规则时直接返回成功。需要管理员权限。
    /// </summary>
    public static OperationResult TryCreateInboundBlockRule(int port = DefaultPort)
    {
        if (!ProcessHelper.IsUserAdministrator())
            return new OperationResult(false, "创建防火墙规则需要管理员权限。");

        if (HasInboundBlockRule(port))
            return new OperationResult(true, $"入站阻断规则已存在，远程访问 {port} 端口已被拦截。");

        object? policy = null;
        object? newRule = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (policyType is null || ruleType is null)
                return new OperationResult(false, "本机没有可用的 Windows 防火墙 COM 接口。");

            policy = Activator.CreateInstance(policyType);
            newRule = Activator.CreateInstance(ruleType);
            if (policy is null || newRule is null)
                return new OperationResult(false, "无法创建防火墙 COM 对象。");

            dynamic rule = newRule;
            rule.Name = FirewallRuleName;
            rule.Description = "阻止从其他主机访问机械革命 GCUBridge 的 MQTT 端口。本机连接不受影响。";
            rule.Protocol = ProtocolTcp;
            rule.LocalPorts = port.ToString();
            rule.Direction = DirectionInbound;
            rule.Action = ActionBlock;
            rule.Profiles = ProfileAll;
            rule.Enabled = true;

            dynamic firewall = policy;
            firewall.Rules.Add(rule);

            // 不信任「Add 没抛异常」就当成功，回读确认。
            return HasInboundBlockRule(port)
                ? new OperationResult(true, $"已创建入站阻断规则，{port} 端口不再接受来自其他主机的连接。")
                : new OperationResult(false, "规则已提交但回读校验失败，请检查组策略是否限制了防火墙规则。");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, "创建防火墙规则失败：" + ex.Message);
        }
        finally
        {
            if (newRule is not null && Marshal.IsComObject(newRule)) Marshal.FinalReleaseComObject(newRule);
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    /// <summary>删除本程序创建的入站阻断规则。只按精确规则名删除，不触碰其他规则。</summary>
    public static OperationResult TryRemoveInboundBlockRule()
    {
        if (!ProcessHelper.IsUserAdministrator())
            return new OperationResult(false, "删除防火墙规则需要管理员权限。");

        object? policy = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null) return new OperationResult(false, "本机没有可用的 Windows 防火墙 COM 接口。");
            policy = Activator.CreateInstance(policyType);
            if (policy is null) return new OperationResult(false, "无法创建防火墙 COM 对象。");

            dynamic firewall = policy;
            firewall.Rules.Remove(FirewallRuleName);

            return HasInboundBlockRule()
                ? new OperationResult(false, "删除后规则仍然存在。")
                : new OperationResult(true, "已删除入站阻断规则。");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, "删除防火墙规则失败：" + ex.Message);
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    /// <summary>缺少阻断规则时写入日志的提示文案。必须包含用户可直接照做的动作。</summary>
    internal static string MissingRuleWarning(int port = DefaultPort) =>
        $"SECURITY: 厂商 GCU 的 MQTT 端口 {port} 监听在所有网络接口且凭据固定，" +
        $"当前没有 L-Mechrevo 的入站阻断规则，同网络的其他主机可以控制本机硬件。" +
        $"以管理员身份运行 \"L-Mechrevo.exe --secure-mqtt\" 可创建阻断规则（不影响本机连接）。";

    internal static bool PortListContains(string? ports, int expected)
    {
        if (string.IsNullOrWhiteSpace(ports)) return false;
        foreach (string item in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(item, out int port) && port == expected) return true;
            string[] range = item.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out int from) && int.TryParse(range[1], out int to)
                && expected >= from && expected <= to) return true;
        }
        return false;
    }
}
