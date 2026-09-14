using System.Text;

namespace MechrevoLite.Update;

/// <summary>
/// 版本号比较：口径与粉丝的服务端实测行为一致——**提取数字段后按数值逐段比较**，
/// 缺段补 0，数字段全等时再退回整体字符串序（保证 beta13.1 &gt; beta13 这类后缀可比）。
///
/// 真机实测（2026-09-11，l-mechrevo.onismy.cn）：
/// <list type="bullet">
/// <item><c>beta13</c> 与 <c>13-beta</c> 判为相同 → 两边都是 [13]；</item>
/// <item><c>14-beta</c> &gt; <c>13-beta</c>；</item>
/// <item><c>0.289.0.0</c> &lt; <c>13-beta</c> → [0,289,0,0] 首段 0 &lt; 13。</item>
/// </list>
/// 所以客户端要传 <c>Program.ReleaseVersion</c>（"0.289.0-beta13" 这样的完整点分版本，带 beta 后缀）：
/// 裸标签 "beta13" 服务端判格式错误，裸 AssemblyVersion "0.289.0.0" 又会丢 beta 后缀。
/// </summary>
internal static class UpdateVersion
{
    /// <summary>提取版本号里的数字段（非数字只作分隔，连续数字算一段）。</summary>
    internal static IReadOnlyList<int> NumberSegments(string? version)
    {
        var segments = new List<int>();
        if (string.IsNullOrWhiteSpace(version)) return segments;

        var current = new StringBuilder();
        foreach (char c in version)
        {
            if (char.IsAsciiDigit(c))
            {
                current.Append(c);
                continue;
            }

            if (current.Length == 0) continue;
            segments.Add(ParseSegment(current.ToString()));
            current.Clear();
        }
        if (current.Length > 0) segments.Add(ParseSegment(current.ToString()));
        return segments;
    }

    /// <summary>超长数字段按 int 上限截断即可——它只用于比较大小，不需要精确值。</summary>
    static int ParseSegment(string digits) =>
        int.TryParse(digits, out int value) ? value : int.MaxValue;

    /// <summary>&lt;0：left 更旧；0：等价；&gt;0：left 更新。</summary>
    internal static int Compare(string? left, string? right)
    {
        IReadOnlyList<int> a = NumberSegments(left);
        IReadOnlyList<int> b = NumberSegments(right);
        int shared = Math.Min(a.Count, b.Count);
        for (int i = 0; i < shared; i++)
        {
            int diff = a[i].CompareTo(b[i]);
            if (diff != 0) return diff;
        }
        if (a.Count == b.Count) return 0;   // beta13 ≡ 13-beta（服务端实测就是等价）

        // 缺段补 0：beta13 vs beta13.0 等价，beta13 vs beta13.1 判后者更新。
        IReadOnlyList<int> longer = a.Count > b.Count ? a : b;
        for (int i = shared; i < longer.Count; i++)
        {
            if (longer[i] != 0) return a.Count > b.Count ? 1 : -1;
        }
        return 0;
    }

    internal static bool IsNewer(string? candidate, string? current) =>
        Compare(candidate, current) > 0;
}
