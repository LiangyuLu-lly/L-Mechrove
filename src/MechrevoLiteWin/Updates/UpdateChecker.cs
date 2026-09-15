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
    string? DownloadPage,
    string? ServerCurrentVersion = null)
{
    /// <summary>
    /// 直链 + **合法**的 64 位十六进制缓存哈希都在，才谈得上"下载后校验"。
    /// 静态后端下没有校验值就等于无法确认来源，一律不算可安装。
    /// </summary>
    internal bool HasVerifiablePackage =>
        !string.IsNullOrWhiteSpace(DownloadUrl) && UpdatePolicy.IsValidSha256(Sha256);
}

/// <summary>
/// 更新检测（业主自建的静态 OSS 对象 <c>/api/update_check.php</c>）。
///
/// 纪律：
/// <list type="bullet">
/// <item>version 参数传 <see cref="Program.ReleaseVersion"/>（"0.289.0-beta15"）——静态对象忽略查询串，
/// 但客户端仍按契约上报自己的完整点分版本；</item>
/// <item>只走 HTTPS（回环地址例外，供本地联调）；基址不合法时**记录日志**后回退到业主 OSS 默认基址，
/// 绝不静默改打第三方域名；</item>
/// <item>版本比较一律用客户端自己的版本（上报值），不用静态 JSON 里冻结的 <c>current_version</c>。</item>
/// </list>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>业主自建的静态 OSS 基址（对象键 lmechrevo-oss/api/update_check.php，匿名只读）。</summary>
    internal const string DefaultBaseUrl = "https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss";
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

            // 版本比较用**客户端自己的版本**（URL 里上报的那个）。静态 JSON 里的 current_version 是
            // 发布者冻结的值，拿它做基线会让"已经是最新"的用户被永久误报有更新。
            info = info with { CurrentVersion = version };

            // 交叉校验：服务端 update_available=true，但最新版并不比本机版本新（版本被改回旧的、
            // 或发布者忘了递增）——按无更新处理，既不打扰用户，也绝不降级安装。
            if (info.UpdateAvailable && !UpdateVersion.IsNewer(info.LatestVersion, info.CurrentVersion))
            {
                Logger.WriteLine(
                    $"更新检测：服务端报有更新，但 {info.LatestVersion} 并不比本机 {info.CurrentVersion} 新，按无更新处理");
                info = info with { UpdateAvailable = false };
            }

            cached = info;
            AppConfig.Set("update_last_check", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Logger.WriteLine(
                $"更新检测：本机={info.CurrentVersion} 服务端={info.LatestVersion ?? "(无)"} " +
                $"(服务端上报 current_version={info.ServerCurrentVersion ?? "(无)"}) " +
                $"有更新={info.UpdateAvailable} 通道={info.Channel}{(info.ChannelFallback ? "(回退)" : "")}");
            return info;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 只认 https；http 仅放行回环地址（本地联调桩服务器用）。
    /// 不合法时**记录日志**后回退到业主 OSS 默认基址——不再静默改打第三方域名。
    /// </summary>
    internal static string NormalizeBaseUrl(string? raw)
    {
        string value = (raw ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return DefaultBaseUrl;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return value;
        }

        Logger.WriteLine($"更新基址不合法（{(value.Length == 0 ? "(空)" : value)}），已回退到默认基址 {DefaultBaseUrl}");
        return DefaultBaseUrl;
    }

    /// <summary>
    /// 严格解析：<c>ok</c> 不为 false、<c>data</c> 是对象、<c>latest_version</c>（若有）像样、
    /// <c>download_url</c>（若有）必须是 https（回环 http 例外）。任一条不满足整体拒绝。
    /// </summary>
    internal static UpdateInfo? ParseResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JObject root;
        try { root = JObject.Parse(json); }
        catch { return null; }

        if (root["ok"]?.Type == JTokenType.Boolean && root.Value<bool>("ok") == false) return null;
        if (root["data"] is not JObject data) return null;

        string? serverCurrent = Clean(data.Value<string>("current_version"));
        string? latest = Clean(data.Value<string>("latest_version"));
        if (latest is not null && !UpdatePolicy.IsSaneVersion(latest))
        {
            Logger.WriteLine($"更新检测：服务端 latest_version 不合法（{latest}），拒绝该响应");
            return null;
        }

        string? downloadUrl = Clean(data.Value<string>("download_url"));
        if (downloadUrl is not null)
        {
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? downloadUri)
                || !UpdatePolicy.IsAcceptableDownloadUri(downloadUri))
            {
                Logger.WriteLine($"更新检测：download_url 不是 HTTPS 地址（{downloadUrl}），拒绝该响应");
                return null;
            }
        }

        bool available = data.Value<bool?>("update_available") ?? false;
        // 服务端从未发过版本时 latest_version 为 null 且 is_latest=true —— 不提示更新。
        if (latest is null) available = false;

        return new UpdateInfo(
            CurrentVersion: serverCurrent ?? "",
            LatestVersion: latest,
            Channel: Clean(data.Value<string>("channel")) ?? "",
            ChannelFallback: data.Value<bool?>("channel_fallback") ?? false,
            UpdateAvailable: available,
            ReleaseDate: Clean(data.Value<string>("release_date")),
            Notes: Clean(data.Value<string>("notes")),
            FileName: Clean(data.Value<string>("filename")),
            Size: data.Value<long?>("size"),
            Sha256: Clean(data.Value<string>("sha256")),
            DownloadUrl: downloadUrl,
            DownloadPage: Clean(data.Value<string>("download_page")),
            ServerCurrentVersion: serverCurrent);
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
