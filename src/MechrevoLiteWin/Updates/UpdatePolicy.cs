using System.Text.RegularExpressions;

namespace MechrevoLite.Update;

/// <summary>
/// 更新包准入策略（纯函数，全部 fail-closed）。
///
/// 背景：更新后端已从粉丝托管的 PHP 换成业主自己的静态 OSS 对象。静态 JSON 一旦被篡改，
/// 客户端不再有任何服务端逻辑兜底，所以"元数据合法 + 包体哈希匹配"必须由客户端自己强制：
/// 任何一条不满足都要拒绝下载/安装，绝不降级成"只做结构校验"。
/// </summary>
internal static class UpdatePolicy
{
    /// <summary>允许下载更新包的 host：GitHub Release 资产 + 业主的 OSS 桶。回环地址另行放行（本地联调）。</summary>
    internal static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "lmechrevo.oss-cn-hangzhou.aliyuncs.com",
    };

    // 版本 token：可选前导 v、首字符必须是字母/数字，其余只允许字母数字与 . _ + -（拒绝空格/路径/HTML）。
    static readonly Regex SaneVersionToken = new(@"^[vV]?[0-9A-Za-z][0-9A-Za-z._+\-]*$", RegexOptions.Compiled);

    /// <summary>sha256 必须是 64 位十六进制；缺省、长度不符、含非十六进制字符一律不算数。</summary>
    internal static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// 版本串是否像样：只含字母数字与 . _ + -，至少一位数字，不以分隔符开头/结尾，不含 ".."，长度 ≤ 64。
    /// 明显是垃圾（HTML、空白、路径、纯字母）的 latest_version 会被拒。
    /// </summary>
    internal static bool IsSaneVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string value = version.Trim();
        if (value.Length > 64) return false;
        if (!SaneVersionToken.IsMatch(value)) return false;
        if (!value.Any(char.IsAsciiDigit)) return false;
        if (value[0] is '.' or '-' or '+' or '_' || value[^1] is '.' or '-' or '+' or '_') return false;
        if (value.Contains("..")) return false;
        return true;
    }

    /// <summary>下载地址只接受 https；http 仅放行回环地址（本地联调桩服务器用）。</summary>
    internal static bool IsAcceptableDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>host 是否在允许列表内；回环地址放行（仅测试用途）。</summary>
    internal static bool IsAllowedDownloadHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        foreach (string host in AllowedDownloadHosts)
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>下载地址是否通过"协议 + host 白名单"。<paramref name="reason"/> 为拒绝原因。</summary>
    internal static bool TryAcceptDownloadUrl(string? url, out Uri? uri, out string reason)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) { reason = "服务端没有提供下载地址"; return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            reason = "下载地址不是合法的 URL";
            return false;
        }
        if (!IsAcceptableDownloadUri(parsed))
        {
            reason = $"下载地址必须使用 HTTPS（当前 {parsed.Scheme}）";
            return false;
        }
        if (!IsAllowedDownloadHost(parsed))
        {
            reason = $"下载地址主机不在允许列表内（{parsed.Host}）";
            return false;
        }
        uri = parsed;
        reason = "";
        return true;
    }

    /// <summary>
    /// 是否允许自动安装这个更新：更新可用、版本号像样、下载地址通过协议+host 白名单、sha256 合法。
    /// 失败时 <paramref name="reason"/> 是给用户看的原因（fail-closed 的单一判定入口）。
    /// </summary>
    internal static bool TryAcceptOffer(UpdateInfo info, out string reason)
    {
        if (!info.UpdateAvailable) { reason = "没有可用的更新"; return false; }
        if (!IsSaneVersion(info.LatestVersion)) { reason = "服务端版本号不合法"; return false; }
        if (!TryAcceptDownloadUrl(info.DownloadUrl, out _, out reason)) return false;
        if (!IsValidSha256(info.Sha256))
        {
            reason = "服务端未提供有效的 SHA-256 校验值，已拒绝自动安装";
            return false;
        }
        reason = "";
        return true;
    }
}
