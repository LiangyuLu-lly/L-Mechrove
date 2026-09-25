using System.Net;

namespace MechrevoLite.Update;

/// <summary>
/// 更新通道共用的 HttpClient。检测用短超时（不能拖住启动），下载用无限总超时
/// （大文件由调用方的 CancellationToken 控制时限）。
/// 永不跟随重定向：3xx 是硬失败，host 白名单只做请求前检查。
/// </summary>
internal static class UpdateHttp
{
    /// <summary>下载总时限：超过就放弃，避免无限挂在半死的连接上。</summary>
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    internal static readonly HttpClient Check = Create(UpdateChecker.HttpTimeout);

    internal static readonly HttpClient Download = Create(Timeout.InfiniteTimeSpan);

    internal static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    internal static string RedirectRefusedReason(HttpStatusCode status) =>
        $"HTTP {(int)status}：拒绝跟随重定向";

    static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"L-Mechrevo/{Program.ReleaseLabel}");
        return client;
    }
}
