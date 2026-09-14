namespace MechrevoLite.Update;

/// <summary>
/// 更新通道共用的 HttpClient。检测用短超时（不能拖住启动），下载用无限总超时
/// （大文件由调用方的 CancellationToken 控制时限）。
/// </summary>
internal static class UpdateHttp
{
    /// <summary>下载总时限：超过就放弃，避免无限挂在半死的连接上。</summary>
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    internal static readonly HttpClient Check = Create(UpdateChecker.HttpTimeout);

    internal static readonly HttpClient Download = Create(Timeout.InfiniteTimeSpan);

    static HttpClient Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"L-Mechrevo/{Program.ReleaseLabel}");
        return client;
    }
}
