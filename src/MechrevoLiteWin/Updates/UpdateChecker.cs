using MechrevoLite.Helpers;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Update;

/// <summary>服务端 <c>update_check</c> 返回的更新信息（data 段）。</summary>
internal sealed record UpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    string Channel,
    bool ChannelFallback,
    bool UpdateAvailable,
    string? ReleaseDate,
    string? Notes,
    string? FileName,
    long? Size,
    string? Sha256,
    string? DownloadUrl,
    string? DownloadPage)
{
    /// <summary>站内直链 + 哈希都在，才谈得上"下载后校验"。</summary>
    internal bool HasVerifiablePackage =>
        !string.IsNullOrWhiteSpace(DownloadUrl) && !string.IsNullOrWhiteSpace(Sha256);
}

/// <summary>
/// 更新检测（粉丝提供的 <c>/api/update_check.php</c>，2026-09-11 真机实测可用）。
///
/// 纪律：
/// <list type="bullet">
/// <item>version 参数传 <see cref="Program.ReleaseVersion"/>（"0.289.0-beta13"）：服务端只认点分版本，
/// 裸标签 "beta13" 会被判格式错误、update_available 恒为 false；也不能传裸 AssemblyVersion（丢 beta 后缀）；</item>
/// <item>只走 HTTPS（回环地址例外，供本地联调）；任何失败静默返回 null，不阻塞启动；</item>
/// <item>结果进程内缓存；跨会话按 <see cref="AutoCheckInterval"/> 节流，手动检查可强制刷新。</item>
/// </list>
/// </summary>
internal static class UpdateChecker
{
    internal const string DefaultBaseUrl = "https://l-mechrevo.onismy.cn";
    internal const string StableChannel = "stable";
    internal const string BetaChannel = "beta";

    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    /// <summary>下载包体上限：远超正常体积的响应一律拒绝（防止把磁盘写满或拿到垃圾文件）。</summary>
    internal const long MaxPackageBytes = 400L * 1024 * 1024;

    static readonly SemaphoreSlim Gate = new(1, 1);
    static UpdateInfo? cached;

    /// <summary>测试接缝：替换"取 JSON 文本"这一步，其余逻辑（URL、解析、节流）照常走。</summary>
    internal static Func<string, Task<string>>? HttpGetOverride { get; set; }

    /// <summary>诊断模式（--updatetest &lt;baseUrl&gt;）临时覆盖基址，不改用户配置。</summary>
    internal static string? BaseUrlOverride { get; set; }

    internal static string BaseUrl =>
        NormalizeBaseUrl(BaseUrlOverride ?? AppConfig.GetString("update_base_url") ?? DefaultBaseUrl);

    /// <summary>自动检测开关（默认开）。这是我们客户端唯一的出站请求，用户必须能关掉。</summary>
    internal static bool AutoCheckEnabled => AppConfig.Get("check_updates", 1) != 0;

    /// <summary>本次会话已拿到的结果（不触发网络）。</summary>
    internal static UpdateInfo? Cached => cached;

    internal static bool ShouldAutoCheck(DateTime nowUtc, DateTime lastCheckUtc) =>
        nowUtc - lastCheckUtc >= AutoCheckInterval;

    /// <summary>跨会话节流：距上次自动检测不足 <see cref="AutoCheckInterval"/> 就跳过。</summary>
    internal static bool ShouldAutoCheckNow(DateTime nowUtc) =>
        ShouldAutoCheck(nowUtc, DateTimeOffset.FromUnixTimeSeconds(AppConfig.Get("update_last_check", 0)).UtcDateTime);

    internal static string BuildCheckUrl(string baseUrl, string version, string channel = BetaChannel)
    {
        string root = NormalizeBaseUrl(baseUrl);
        return $"{root}/api/update_check.php?version={Uri.EscapeDataString(version)}&channel={Uri.EscapeDataString(channel)}";
    }

    /// <summary>
    /// 检测一次。<paramref name="force"/> = 手动检查（跳过节流与缓存）。
    /// 失败一律返回 null——调用方按"检测不到"处理，绝不能当成"已是最新"。
    /// </summary>
    internal static async Task<UpdateInfo?> CheckAsync(bool force = false, string? versionOverride = null)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && cached is not null) return cached;
            if (!force && !ShouldAutoCheckNow(DateTime.UtcNow)) return cached;

            string version = versionOverride ?? Program.ReleaseVersion;
            string url = BuildCheckUrl(BaseUrl, version);
            string? json;
            try
            {
                json = HttpGetOverride is not null
                    ? await HttpGetOverride(url).ConfigureAwait(false)
                    : await UpdateHttp.Check.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"更新检测失败（静默跳过）：{ex.GetType().Name} {ex.Message}");
                return null;
            }

            UpdateInfo? info = ParseResponse(json);
            if (info is null)
            {
                Logger.WriteLine("更新检测失败（静默跳过）：响应无法解析");
                return null;
            }

            // 交叉校验：服务端说有更新，但按它的比较规则最新版并不比当前版新——服务端数据不一致
            // （比如运营把 latest_version 改回旧版本）。这种情况只记一行日志、不打扰用户。
            if (info.UpdateAvailable && !UpdateVersion.IsNewer(info.LatestVersion, info.CurrentVersion))
            {
                Logger.WriteLine(
                    $"更新检测：服务端报有更新，但版本比较并不成立（{info.LatestVersion} vs {info.CurrentVersion}），按无更新处理");
                info = info with { UpdateAvailable = false };
            }

            cached = info;
            AppConfig.Set("update_last_check", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Logger.WriteLine(
                $"更新检测：本地={info.CurrentVersion} 服务端={info.LatestVersion ?? "(无)"} " +
                $"有更新={info.UpdateAvailable} 通道={info.Channel}{(info.ChannelFallback ? "(回退)" : "")}");
            return info;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>只认 https；http 仅放行回环地址（本地联调桩服务器用）。</summary>
    internal static string NormalizeBaseUrl(string? raw)
    {
        string value = (raw ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return DefaultBaseUrl;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return DefaultBaseUrl;
        if (uri.Scheme == Uri.UriSchemeHttps) return value;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return value;
        return DefaultBaseUrl;
    }

    /// <summary>宽松解析：字段缺失/类型不符按 null 处理，只有 ok=false 或结构不对才整体失败。</summary>
    internal static UpdateInfo? ParseResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JObject root;
        try { root = JObject.Parse(json); }
        catch { return null; }

        if (root["ok"]?.Type == JTokenType.Boolean && root.Value<bool>("ok") == false) return null;
        if (root["data"] is not JObject data) return null;

        string? latest = Clean(data.Value<string>("latest_version"));
        bool available = data.Value<bool?>("update_available") ?? false;
        // 服务端从未发过版本时 latest_version 为 null 且 is_latest=true —— 不提示更新。
        if (latest is null) available = false;

        return new UpdateInfo(
            CurrentVersion: Clean(data.Value<string>("current_version")) ?? "",
            LatestVersion: latest,
            Channel: Clean(data.Value<string>("channel")) ?? "",
            ChannelFallback: data.Value<bool?>("channel_fallback") ?? false,
            UpdateAvailable: available,
            ReleaseDate: Clean(data.Value<string>("release_date")),
            Notes: Clean(data.Value<string>("notes")),
            FileName: Clean(data.Value<string>("filename")),
            Size: data.Value<long?>("size"),
            Sha256: Clean(data.Value<string>("sha256")),
            DownloadUrl: Clean(data.Value<string>("download_url")),
            DownloadPage: Clean(data.Value<string>("download_page")));
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
