using System.Reflection;
using System.Text.RegularExpressions;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// MQTT 主题常量与订阅/分派的一致性护栏（docs/beta17-wave-plan.md Wave A3）。
///
/// 主题是逐字节精确匹配的：拼错一个字符既不编译报错也不运行报错，只会静默收不到
/// 状态。这份测试把三件事锁在一起：
/// ① 每个 switch 分支都被某个订阅过滤器覆盖（分支不会永远不触发）；
/// ② 每个订阅都有 switch 分支（订阅不会没人处理；例外见 <see cref="ObserveOnlyFilters"/>）；
/// ③ 分支标签必须引用 <see cref="MqttTopics"/> 常量，不得再写裸主题字面量。
///
/// 订阅侧读的是运行时真值 <see cref="MechrevoHw.SubscribedTopicFilters"/>（不是源码扫描），
/// 分派侧从 MechrevoHw.cs 源码提取 case 标签后再反查常量值。
/// </summary>
public class MqttTopicsContractTests
{
    /// <summary>
    /// 订阅了但没有 switch 分支的主题族，逐条写明理由。新增例外必须改这份文件——
    /// 「订阅了一个没人处理的主题」永远是一次被看见的决策。
    /// </summary>
    static readonly (string Filter, string Why)[] ObserveOnlyFilters =
    {
        (MqttTopics.CustomizeFilter,
            "初始快照以来就有的旁路订阅：官方 Customize 族当前没有解析分支，报文只走 RawMessageObserved 诊断通道；删订阅属于行为变更，不在 A3 机械替换范围内"),
    };

    [Fact]
    public void EveryHandlerArmIsCoveredByASubscription()
    {
        string[] filters = MechrevoHw.SubscribedTopicFilters;
        string[] uncovered = HandlerTopicValues()
            .Where(arm => !filters.Any(filter => MqttFilterCovers(filter, arm)))
            .ToArray();

        Assert.True(uncovered.Length == 0,
            "HandleMessage 有分支、但没有任何订阅过滤器覆盖这些主题，分支永远不会被触发：\n  "
            + string.Join("\n  ", uncovered));
    }

    [Fact]
    public void EverySubscriptionHasAHandlerArm()
    {
        string[] arms = HandlerTopicValues();
        var violations = new List<string>();
        foreach (string filter in MechrevoHw.SubscribedTopicFilters)
        {
            if (ObserveOnlyFilters.Any(entry => entry.Filter == filter)) continue;
            if (!arms.Any(arm => MqttFilterCovers(filter, arm))) violations.Add(filter);
        }

        Assert.True(violations.Count == 0,
            "订阅了这些过滤器，但 HandleMessage 里没有任何匹配的 case 分支。新增订阅必须同时加分派分支；"
            + "若确属只做旁路观察，请把过滤器加进 ObserveOnlyFilters 并写明理由：\n  "
            + string.Join("\n  ", violations));

        // 例外清单必须仍与实际订阅一致，防止订阅被删后白名单变成僵尸条目。
        foreach ((string filter, _) in ObserveOnlyFilters)
            Assert.Contains(filter, MechrevoHw.SubscribedTopicFilters);
    }

    [Fact]
    public void HandlerArmsUseNamedTopicConstants()
    {
        string[] raw = Regex.Matches(HandlerSource(), "case\\s+\"([^\"]*/[^\"]*)\"\\s*:")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(raw.Length == 0,
            "switch 分支必须引用 MqttTopics 常量，不得再写裸主题字面量：\n  "
            + string.Join("\n  ", raw));
    }

    [Fact]
    public void TopicConstantsAreDistinctAndNonEmpty()
    {
        var constants = typeof(MqttTopics)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToArray();

        Assert.NotEmpty(constants);
        foreach ((string name, string value) in constants)
            Assert.False(string.IsNullOrWhiteSpace(value), name + " 是空字符串");

        string[] duplicates = constants
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key + " ← " + string.Join(", ", group.Select(entry => entry.Name)))
            .ToArray();
        Assert.True(duplicates.Length == 0, "主题常量值重复：\n  " + string.Join("\n  ", duplicates));
    }

    /// <summary>HandleMessage 的 switch 会分派到的主题值（含尚未换成常量的裸字面量形式）。</summary>
    static string[] HandlerTopicValues()
    {
        string source = HandlerSource();
        var values = new HashSet<string>(StringComparer.Ordinal);

        // case MqttTopics.<Name>: → 用反射取常量值。
        var constants = typeof(MqttTopics)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(source, @"case\s+MqttTopics\.(\w+)\s*:"))
        {
            string name = match.Groups[1].Value;
            Assert.True(constants.ContainsKey(name), $"case 引用了不存在的 MqttTopics.{name}");
            values.Add(constants[name]);
        }

        // 裸字面量形式也算数，否则「加了分支但漏订阅」会被扫描静默漏掉。
        foreach (Match match in Regex.Matches(source, "case\\s+\"([^\"]*/[^\"]*)\"\\s*:"))
            values.Add(match.Groups[1].Value);

        Assert.True(values.Count > 0, "没有从 MechrevoHw.cs 提取到任何主题 case 分支——扫描规则需要更新");
        return values.ToArray();
    }

    /// <summary>MQTT 3.1.1 通配符匹配：<c>+</c> 匹配单层，<c>#</c> 只能是最后一段。</summary>
    static bool MqttFilterCovers(string filter, string topic)
    {
        string[] filterParts = filter.Split('/');
        string[] topicParts = topic.Split('/');
        for (int i = 0; i < filterParts.Length; i++)
        {
            if (filterParts[i] == "#") return i == filterParts.Length - 1;
            if (i >= topicParts.Length) return false;
            if (filterParts[i] != "+" && !string.Equals(filterParts[i], topicParts[i], StringComparison.Ordinal))
                return false;
        }
        return filterParts.Length == topicParts.Length;
    }

    static string HandlerSource() =>
        File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "Hardware", "MechrevoHw.cs"));

    static string RepoRootPath(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, Path.Combine(tail));
    }
}
