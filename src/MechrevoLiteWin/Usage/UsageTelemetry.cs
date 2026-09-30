using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.Update;

namespace MechrevoLite.Usage;

/// <summary>
/// 匿名心跳：启动 / 周期 / 退出各一次。默认开，<c>usage_telemetry=0</c> 可关（设置 → 隐私与反馈）。
/// 上报到统计站 <c>/api/heartbeat.php</c>；脱敏日志（内存环形缓冲 + 新的崩溃现场）在启动后传一次，
/// 运行中出现新的失败行时最多每 30 分钟补传一次。失败只记日志。
/// </summary>
internal static class UsageTelemetry
{
    internal const string EnabledKey = "usage_telemetry";
    internal const string InstallIdKey = "usage_install_id";
    internal const string BaseUrlKey = "usage_base_url";
    internal const string DefaultBaseUrl = "https://stats.l-mechrevo.cn";
    internal const int DefaultEnabled = 1;
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    /// <summary>启动后第一拍前的等待（测试接缝：可改短）。</summary>
    internal static TimeSpan FirstDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>日志上传的 UTF-8 字节上限（服务端 log.php 保留末尾 96 KB，这里留余量）。</summary>
    internal const int MaxLogBytes = 64 * 1024;

    /// <summary>其中留给新崩溃现场的上限；其余给内存环形缓冲（最近的运行轨迹）。</summary>
    internal const int MaxCrashBytes = 16 * 1024;

    /// <summary>出现新的失败行后补传日志的最短间隔。</summary>
    internal static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(30);

    /// <summary>已上传过的崩溃文件时间戳（UTC ticks）：同一份崩溃现场只传一次。</summary>
    internal const string CrashUploadedKey = "usage_crash_uploaded";

    /// <summary>「匿名统计」告知的版本：低于它就在主窗首次显示时告知一次（新装在首次引导里，老用户单独一次）。</summary>
    internal const string NoticeVersionKey = "usage_notice_version";
    internal const int CurrentNoticeVersion = 1;

    /// <summary>
    /// 上传用的 JSON 选项：中文按 UTF-8 原样输出。默认编码器把每个汉字写成 6 字节的 <c>\uXXXX</c>，
    /// 带中文的日志因此常常超过服务端的请求体上限而被整条拒收。
    /// </summary>
    internal static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static Func<string, string, CancellationToken, Task>? HttpPostOverride { get; set; }

    static readonly object Gate = new();
    static CancellationTokenSource? _cts;
    static Task? _loop;
    static int _stopped;
    static int _errorsAtLastLogUpload;
    static long _lastLogUploadTick;

    internal static bool Enabled => AppConfig.Get(EnabledKey, DefaultEnabled) != 0;

    /// <summary>用户是否已经看过「匿名统计」告知。</summary>
    internal static bool NoticeShown => AppConfig.Get(NoticeVersionKey, 0) >= CurrentNoticeVersion;

    /// <summary>
    /// 告知之前什么都不发：心跳循环在这里等到用户看过告知（首次引导 / 老用户的单独告知）为止。
    /// 开机自启到托盘、一直没打开主窗的用户，看到告知之前不会发出任何数据。
    /// </summary>
    static TaskCompletionSource<bool> _noticeGate = NewNoticeGate();

    static TaskCompletionSource<bool> NewNoticeGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>测试接缝：重新关上告知闸门（生产代码里闸门只开不关）。</summary>
    internal static void ResetNoticeGateForTests() => Interlocked.Exchange(ref _noticeGate, NewNoticeGate());

    /// <summary>本进程是否已经发过启动心跳（没发过就不发退出心跳）。</summary>
    static int _startSent;

    internal static void MarkNoticeShown()
    {
        AppConfig.Set(NoticeVersionKey, CurrentNoticeVersion);
        Volatile.Read(ref _noticeGate).TrySetResult(true);
    }

    /// <summary>
    /// 用户开关：打开即启动心跳循环；关闭立即停掉循环，**不再发任何东西**（包括退出心跳）。
    /// </summary>
    internal static void SetEnabled(bool enabled)
    {
        AppConfig.Set(EnabledKey, enabled ? 1 : 0);
        Logger.WriteLine("Usage telemetry " + (enabled ? "enabled" : "disabled") + " by the user.");
        if (enabled) Start();
        else CancelLoop();
    }

    static void CancelLoop()
    {
        CancellationTokenSource? cts;
        lock (Gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }
        try { cts?.Cancel(); } catch { /* 已释放 */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    internal static string BaseUrl =>
        UpdateChecker.NormalizeBaseUrl(AppConfig.GetString(BaseUrlKey) ?? DefaultBaseUrl);

    internal static string InstallId()
    {
        string? existing = AppConfig.GetString(InstallIdKey);
        if (!string.IsNullOrWhiteSpace(existing) && existing.Length >= 8)
            return existing.Trim();
        string created = Guid.NewGuid().ToString("N");
        AppConfig.Set(InstallIdKey, created);
        return created;
    }

    internal static string BuildUrl(string baseUrl) =>
        UpdateChecker.NormalizeBaseUrl(baseUrl) + "/api/heartbeat.php";

    internal static string BuildLogUrl(string baseUrl) =>
        UpdateChecker.NormalizeBaseUrl(baseUrl) + "/api/log.php";

    static readonly Regex UsersPath = new(@"[A-Za-z]:\\Users\\[^\\\r\n""'<>|]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MacAddress = new(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);

    /// <summary>
    /// 日志脱敏：用户目录、计算机名、用户名、MAC 地址（液冷 / 蓝牙设备）。短于 3 个字符的名字不替换——
    /// 否则会把日志里无关的字母一起抹掉。
    /// </summary>
    internal static string RedactLog(string text) =>
        RedactLog(text, SafeFolder(Environment.SpecialFolder.UserProfile), SafeName(() => Environment.MachineName),
            SafeName(() => Environment.UserName));

    internal static string RedactLog(string text, string? profile, string? machineName, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!string.IsNullOrEmpty(profile))
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = UsersPath.Replace(text, "%USERPROFILE%");
        text = ReplaceName(text, machineName, "%COMPUTERNAME%");
        text = ReplaceName(text, userName, "%USERNAME%");
        return MacAddress.Replace(text, "xx:xx:xx:xx:xx:xx");
    }

    static string ReplaceName(string text, string? name, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 3) return text;
        return Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(name.Trim()) + @"(?![A-Za-z0-9])", placeholder,
            RegexOptions.IgnoreCase);
    }

    static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch { return null; }
    }

    static string? SafeName(Func<string> read)
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>保留末尾不超过 <paramref name="maxBytes"/> 个 UTF-8 字节（按字符切，不切半个汉字）。</summary>
    internal static string TailUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        int bytes = 0;
        int start = text.Length;
        while (start > 0)
        {
            int step = start >= 2 && char.IsLowSurrogate(text[start - 1]) && char.IsHighSurrogate(text[start - 2]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.AsSpan(start - step, step));
            if (bytes + size > maxBytes) break;
            bytes += size;
            start -= step;
        }
        return text[start..];
    }

    /// <summary>
    /// 上传的日志正文：新的崩溃现场（上次上传之后才写入的 crash.txt 末尾）+ 内存环形缓冲（最近的运行轨迹，
    /// 与日志级别无关——默认 OFF 时 log.txt 根本不存在）。已脱敏、按 UTF-8 字节截到上限。
    /// </summary>
    internal static string BuildLogPayload(string? crashTail, string ringBuffer)
    {
        const string CrashHeader = "--- crash ---\n";
        const string RecentHeader = "--- recent ---\n";
        // 崩溃现场先占（最多 MaxCrashBytes），环形缓冲拿剩下的预算、保留最新的部分——
        // 整体截尾会把崩溃段和段头一起切掉。
        string crash = string.IsNullOrWhiteSpace(crashTail)
            ? ""
            : CrashHeader + TailUtf8(RedactLog(crashTail.Trim()), MaxCrashBytes);
        if (string.IsNullOrWhiteSpace(ringBuffer)) return crash;
        int used = Encoding.UTF8.GetByteCount(crash) + (crash.Length > 0 ? 1 : 0) + Encoding.UTF8.GetByteCount(RecentHeader);
        string recent = RecentHeader + TailUtf8(RedactLog(ringBuffer.Trim()), Math.Max(0, MaxLogBytes - used));
        return crash.Length > 0 ? crash + "\n" + recent : recent;
    }

    /// <summary>读取尚未上传过的崩溃现场（crash.txt 末尾）；没有新的返回 null，同时给出文件时间戳。</summary>
    static string? ReadNewCrashTail(out long stamp)
    {
        stamp = 0;
        try
        {
            string path = Logger.crashFile;
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            stamp = info.LastWriteTimeUtc.Ticks;
            long uploaded = long.TryParse(AppConfig.GetString(CrashUploadedKey), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : 0;
            if (info.Length <= 0 || stamp <= uploaded) return null;
            int take = (int)Math.Min(info.Length, MaxCrashBytes * 2L);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (info.Length > take) stream.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd().Trim();
            return text.Length > 0 ? text : null;
        }
        catch { return null; }   // 读日志失败不影响心跳
    }

    internal static UsageSnapshot Capture(string eventName)
    {
        MechrevoDeviceCapabilities caps = SafeCaps();
        MechrevoHw? hw = null;
        try { hw = Program.hw; } catch { /* 启动早期 */ }

        string project = "";
        try
        {
            SupportDecision decision = RuntimeModelSupport.Current();
            project = decision.ProjectId ?? "";
        }
        catch { /* 身份读失败就留空 */ }

        string gpuName = "";
        DgpuGenerationKind gen = DgpuGenerationKind.Unknown;
        try
        {
            DgpuIdentity identity = GpuGenerationProvider.Current();
            gen = identity.Generation;
            gpuName = identity.MarketingName ?? "";
        }
        catch { /* 无独显探测 */ }

        string cpu = "";
        try { cpu = PawnIO.CpuInfo.Name ?? ""; } catch { }

        bool gcu = false;
        try { gcu = hw is { IsConnected: true }; } catch { }

        bool elevated = false;
        try { elevated = ProcessHelper.IsUserAdministrator(); } catch { }

        string tier = "";
        try { tier = UsageSnapshot.TierToken(GcuServiceTierProbe.Current()); } catch { }

        int port = 0;
        try { port = GcuEndpoint.Port; } catch { }

        string os = "";
        try
        {
            Version v = Environment.OSVersion.Version;
            os = $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }

        return UsageSnapshot.Sanitize(new UsageSnapshot(
            InstallId(),
            Program.ReleaseVersion,
            eventName,
            caps.Model ?? "",
            project,
            caps.BiosVersion ?? "",
            UsageSnapshot.GpuGenToken(gen),
            gpuName,
            cpu,
            gcu,
            caps.Keyboard,
            caps.Lightbar || caps.RgbLightbar,
            elevated,
            tier,
            port,
            os,
            SilentUpdate.LastOutcomeToken,
            Logger.ErrorCount));
    }

    internal static string Serialize(UsageSnapshot snapshot) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = snapshot.Id,
            ["ver"] = snapshot.Ver,
            ["event"] = snapshot.Event,
            ["model"] = snapshot.Model,
            ["project"] = snapshot.Project,
            ["bios"] = snapshot.Bios,
            ["gpu"] = snapshot.Gpu,
            ["gpu_name"] = snapshot.GpuName,
            ["cpu"] = snapshot.Cpu,
            ["gcu"] = snapshot.Gcu,
            ["keyboard"] = snapshot.Keyboard,
            ["lightbar"] = snapshot.Lightbar,
            ["elevated"] = snapshot.Elevated,
            ["tier"] = snapshot.Tier,
            ["port"] = snapshot.Port,
            ["os"] = snapshot.Os,
            ["upd"] = snapshot.Update,
            ["errors"] = snapshot.Errors,
        }, WireJson);

    internal static void Start()
    {
        lock (Gate)
        {
            if (Program.UiAuditMode || !Enabled) return;
            if (NoticeShown) Volatile.Read(ref _noticeGate).TrySetResult(true);
            if (_loop is { IsCompleted: false }) return;
            Volatile.Write(ref _stopped, 0);
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    internal static void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        CancellationTokenSource? cts;
        Task? loop;
        lock (Gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        try { cts?.Cancel(); } catch { /* 退出路径 */ }
        try
        {
            if (Enabled && !Program.UiAuditMode && Volatile.Read(ref _startSent) != 0)
            {
                // 退出心跳与（有新失败行时的）日志补传并行，合计最多等 2.5 s，不拖慢退出。
                var pending = new List<Task> { Post(Capture(UsageSnapshot.EventStop), CancellationToken.None) };
                if (ShouldUploadErrorLog(Environment.TickCount64, ignoreInterval: true))
                    pending.Add(PostLog(CancellationToken.None));
                Task.WhenAll(pending).Wait(TimeSpan.FromMilliseconds(2500));
            }
        }
        catch (Exception ex) { Logger.WriteInfo("usage stop heartbeat failed: " + ex.Message); }
        try { cts?.Dispose(); } catch { /* ignore */ }
        _ = loop;
    }

    /// <summary>
    /// 运行中出现了上次上传之后的新失败行，且距上次上传超过 <see cref="ErrorLogInterval"/>（退出时不看间隔）。
    /// </summary>
    internal static bool ShouldUploadErrorLog(long nowTick, bool ignoreInterval = false) =>
        ShouldUploadErrorLog(Logger.ErrorCount, Volatile.Read(ref _errorsAtLastLogUpload),
            nowTick - Interlocked.Read(ref _lastLogUploadTick), ignoreInterval);

    /// <summary>纯函数形式（便于测试）。</summary>
    internal static bool ShouldUploadErrorLog(int errorsNow, int errorsAtLastUpload, long millisecondsSinceUpload, bool ignoreInterval) =>
        errorsNow > errorsAtLastUpload &&
        (ignoreInterval || millisecondsSinceUpload >= (long)ErrorLogInterval.TotalMilliseconds);

    static async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstDelay, token).ConfigureAwait(false);
            if (!NoticeShown) await Volatile.Read(ref _noticeGate).Task.WaitAsync(token).ConfigureAwait(false);
            if (!Enabled) return;   // 用户在告知里关掉了
            Interlocked.Exchange(ref _startSent, 1);
            await Post(Capture(UsageSnapshot.EventStart), token).ConfigureAwait(false);
            await PostLog(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
                await Post(Capture(UsageSnapshot.EventBeat), token).ConfigureAwait(false);
                if (ShouldUploadErrorLog(Environment.TickCount64))
                    await PostLog(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex) { Logger.WriteInfo("usage heartbeat loop failed: " + ex.Message); }
    }

    static async Task Post(UsageSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Id.Length < 8) return;
        string url = BuildUrl(BaseUrl);
        string body = Serialize(snapshot);
        try
        {
            if (HttpPostOverride is not null)
            {
                await HttpPostOverride(url, body, token).ConfigureAwait(false);
                return;
            }
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await UpdateHttp.Check.PostAsync(url, content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Logger.WriteInfo($"usage heartbeat HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Logger.WriteInfo("usage heartbeat failed: " + ex.Message); }
    }

    static async Task PostLog(CancellationToken token)
    {
        string id = InstallId();
        if (id.Length < 8) return;
        // 先记下「传到哪儿了」：上传失败不重试同一批（下一批新失败行出现时再传），避免失败时每拍重发。
        Volatile.Write(ref _errorsAtLastLogUpload, Logger.ErrorCount);
        Interlocked.Exchange(ref _lastLogUploadTick, Environment.TickCount64);
        string? crash = ReadNewCrashTail(out long crashStamp);
        string log = BuildLogPayload(crash, Logger.SnapshotRingBuffer());
        if (log.Length < 8) return;
        string url = BuildLogUrl(BaseUrl);
        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = id,
            ["ver"] = Program.ReleaseVersion ?? "",
            ["log"] = log,
        }, WireJson);
        try
        {
            if (HttpPostOverride is not null)
            {
                await HttpPostOverride(url, body, token).ConfigureAwait(false);
            }
            else
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await UpdateHttp.Check.PostAsync(url, content, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.WriteInfo($"usage log HTTP {(int)response.StatusCode}");
                    return;
                }
            }
            if (crash is not null)
                AppConfig.Set(CrashUploadedKey, crashStamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Logger.WriteInfo("usage log upload failed: " + ex.Message); }
    }

    static MechrevoDeviceCapabilities SafeCaps()
    {
        try { return MechrevoDeviceCapabilities.Current; }
        catch { return new MechrevoDeviceCapabilities(); }
    }
}
