using System.Net.Http;
using System.Text;
using System.Text.Json;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Update;

namespace MechrevoLite.Usage;

/// <summary>一次反馈上传的结果：成功时带服务端编号，失败时带原因（界面原样显示）。</summary>
internal sealed record FeedbackResult(bool Ok, string? Ticket, string? Error);

/// <summary>
/// 问题反馈：用户在「问题反馈」窗口点「发送」时才上传——描述、可选联系方式，以及（用户勾选时）
/// 系统信息与脱敏日志。与匿名统计开关无关：这是用户主动发起的一次发送。
/// 服务端 <c>/api/feedback.php</c>：请求体 ≤ 256 KB，每 IP 每小时 ≤ 6 条。
/// </summary>
internal static class FeedbackUpload
{
    internal const int MinMessageChars = 4;
    internal const int MaxMessageChars = 4000;
    internal const int MaxContactChars = 120;

    /// <summary>系统信息的 UTF-8 字节上限（服务端保留前 32 KB）。</summary>
    internal const int MaxSysInfoBytes = 24 * 1024;

    /// <summary>日志的 UTF-8 字节上限（服务端保留末尾 128 KB）。</summary>
    internal const int MaxLogBytes = 96 * 1024;

    internal static Func<string, string, CancellationToken, Task<string>>? HttpPostOverride { get; set; }

    internal static string BuildUrl(string baseUrl) =>
        UpdateChecker.NormalizeBaseUrl(baseUrl) + "/api/feedback.php";

    /// <summary>描述够不够发（去掉空白后至少 <see cref="MinMessageChars"/> 个字符）。</summary>
    internal static bool IsSendable(string? message) =>
        (message?.Trim().Length ?? 0) >= MinMessageChars;

    /// <summary>
    /// 请求体（纯函数）。系统信息与日志只在 <paramref name="attach"/> 时带，且已脱敏、按字节截断；
    /// 日志保留末尾（最近的部分）。
    /// </summary>
    internal static string BuildBody(
        string id, string version, string model, string project, string gpu,
        string message, string? contact, bool attach, string? systemInfo, string? log)
    {
        string text = message.Trim();
        if (text.Length > MaxMessageChars) text = text[..MaxMessageChars];
        string who = (contact ?? "").Trim();
        if (who.Length > MaxContactChars) who = who[..MaxContactChars];
        string sys = attach ? HeadUtf8(UsageTelemetry.RedactLog(systemInfo ?? ""), MaxSysInfoBytes) : "";
        string tail = attach ? UsageTelemetry.TailUtf8(UsageTelemetry.RedactLog(log ?? ""), MaxLogBytes) : "";
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = id,
            ["ver"] = version,
            ["model"] = UsageSnapshot.Clip(model),
            ["project"] = UsageSnapshot.Clip(project),
            ["gpu"] = UsageSnapshot.Clip(gpu),
            ["message"] = text,
            ["contact"] = who,
            ["sysinfo"] = sys,
            ["log"] = tail,
        }, UsageTelemetry.WireJson);
    }

    /// <summary>保留开头不超过 <paramref name="maxBytes"/> 个 UTF-8 字节（按字符切）。</summary>
    internal static string HeadUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        int bytes = 0;
        int end = 0;
        while (end < text.Length)
        {
            int step = end + 1 < text.Length && char.IsHighSurrogate(text[end]) && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.AsSpan(end, step));
            if (bytes + size > maxBytes) break;
            bytes += size;
            end += step;
        }
        return text[..end];
    }

    /// <summary>服务端应答 → 结果（纯函数）：<c>{"ok":true,"ticket":"..."}</c> 才算成功。</summary>
    internal static FeedbackResult ParseResponse(int statusCode, string? body)
    {
        string? error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True &&
                    root.TryGetProperty("ticket", out JsonElement ticket) && ticket.ValueKind == JsonValueKind.String)
                    return new FeedbackResult(true, ticket.GetString(), null);
                if (root.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String)
                    error = e.GetString();
            }
        }
        catch (JsonException) { /* 非 JSON 应答：按状态码说明 */ }
        return new FeedbackResult(false, null, statusCode switch
        {
            429 => Properties.Strings.FeedbackRateLimited,
            413 => Properties.Strings.FeedbackTooLarge,
            _ => $"HTTP {statusCode}" + (string.IsNullOrWhiteSpace(error) ? "" : " · " + error),
        });
    }

    /// <summary>采集当前机器信息与日志并发送。网络失败返回失败结果，不抛异常。</summary>
    internal static async Task<FeedbackResult> SendAsync(string message, string? contact, bool attach, CancellationToken token)
    {
        if (!IsSendable(message)) return new FeedbackResult(false, null, Properties.Strings.FeedbackTooShort);

        string model = "", project = "", gpu = "";
        try { model = MechrevoDeviceCapabilities.Current.Model ?? ""; } catch { }
        try { project = RuntimeModelSupport.Current().ProjectId ?? ""; } catch { }
        try { gpu = UsageSnapshot.GpuGenToken(GpuGenerationProvider.Current().Generation); } catch { }

        string? systemInfo = null;
        string? log = null;
        if (attach)
        {
            // 系统信息里有 WMI 与 EC 只读查询，放线程池，不压 UI 线程。
            systemInfo = await Task.Run(() =>
            {
                try { return Diagnostics.DiagnosticSystemInfo.Build(); }
                catch (Exception ex) { return "系统信息采集失败：" + ex.Message; }
            }, token).ConfigureAwait(false);
            log = Logger.SnapshotRingBuffer();
        }

        string body = BuildBody(UsageTelemetry.InstallId(), Program.ReleaseVersion ?? "", model, project, gpu,
            message, contact, attach, systemInfo, log);
        string url = BuildUrl(UsageTelemetry.BaseUrl);
        try
        {
            if (HttpPostOverride is not null)
                return ParseResponse(200, await HttpPostOverride(url, body, token).ConfigureAwait(false));

            // 带日志时请求体上百 KB：不用 6 s 的检查客户端，单独给 30 s（慢网也发得完），用户取消照样生效。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SendTimeout);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await UpdateHttp.Download.PostAsync(url, content, timeout.Token).ConfigureAwait(false);
            string reply = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            FeedbackResult result = ParseResponse((int)response.StatusCode, reply);
            Logger.WriteInfo(result.Ok
                ? $"Feedback sent: ticket {result.Ticket} ({Encoding.UTF8.GetByteCount(body)} bytes)"
                : $"Feedback not accepted: {result.Error}");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            Logger.WriteInfo("Feedback upload timed out.");
            return new FeedbackResult(false, null, Properties.Strings.FeedbackTimedOut);
        }
        catch (Exception ex)
        {
            Logger.WriteInfo("Feedback upload did not reach the server: " + ex.Message);
            return new FeedbackResult(false, null, ex.Message);
        }
    }

    /// <summary>一次发送的总时限。</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
}
