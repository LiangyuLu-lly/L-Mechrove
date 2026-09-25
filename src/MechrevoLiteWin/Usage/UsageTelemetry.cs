using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Update;

namespace MechrevoLite.Usage;

/// <summary>
    /// 匿名心跳：启动 / 周期 / 退出各一次。默认开，<c>usage_telemetry=0</c> 可关。
    /// 上报到统计站 <c>/api/heartbeat.php</c>，并附带日志尾。失败只记日志。
/// </summary>
internal static class UsageTelemetry
{
    internal const string EnabledKey = "usage_telemetry";
    internal const string InstallIdKey = "usage_install_id";
    internal const string BaseUrlKey = "usage_base_url";
    internal const string DefaultBaseUrl = "https://stats.l-mechrevo.cn";
    internal const int DefaultEnabled = 1;
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);
    internal const int MaxLogChars = 24 * 1024;

    internal static Func<string, string, CancellationToken, Task>? HttpPostOverride { get; set; }

    static readonly object Gate = new();
    static CancellationTokenSource? _cts;
    static Task? _loop;
    static int _stopped;

    internal static bool Enabled => AppConfig.Get(EnabledKey, DefaultEnabled) != 0;

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

    internal static string RedactLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* 脱敏失败仍继续 */ }
        return Regex.Replace(text, @"[A-Za-z]:\\Users\\[^\\]+", @"%USERPROFILE%", RegexOptions.IgnoreCase);
    }

    internal static string ReadLogTail()
    {
        var parts = new List<string>();
        AppendTail(parts, Logger.logFile);
        AppendTail(parts, Logger.crashFile);
        if (parts.Count == 0) return "";
        string combined = string.Join("\n--- crash ---\n", parts);
        combined = RedactLog(combined);
        if (combined.Length <= MaxLogChars) return combined;
        return combined[^MaxLogChars..];
    }

    static void AppendTail(List<string> parts, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length <= 0) return;
            int take = (int)Math.Min(info.Length, MaxLogChars);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (info.Length > take)
                stream.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd().Trim();
            if (text.Length > 0) parts.Add(text);
        }
        catch { /* 读日志失败不影响心跳 */ }
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
            caps.Lightbar || caps.RgbLightbar));
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
        });

    internal static void Start()
    {
        lock (Gate)
        {
            if (Program.UiAuditMode || !Enabled) return;
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
            if (Enabled)
                Post(Capture(UsageSnapshot.EventStop), CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { Logger.WriteLine("usage stop heartbeat failed: " + ex.Message); }
        try { cts?.Dispose(); } catch { /* ignore */ }
        _ = loop;
    }

    static async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstDelay, token).ConfigureAwait(false);
            await Post(Capture(UsageSnapshot.EventStart), token).ConfigureAwait(false);
            await PostLog(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
                await Post(Capture(UsageSnapshot.EventBeat), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex) { Logger.WriteLine("usage heartbeat loop failed: " + ex.Message); }
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
                Logger.WriteLine($"usage heartbeat HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Logger.WriteLine("usage heartbeat failed: " + ex.Message); }
    }

    static async Task PostLog(CancellationToken token)
    {
        string id = InstallId();
        if (id.Length < 8) return;
        string log = ReadLogTail();
        if (log.Length < 8) return;
        string url = BuildLogUrl(BaseUrl);
        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = id,
            ["ver"] = Program.ReleaseVersion ?? "",
            ["log"] = log,
        });
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
                Logger.WriteLine($"usage log HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Logger.WriteLine("usage log upload failed: " + ex.Message); }
    }

    static MechrevoDeviceCapabilities SafeCaps()
    {
        try { return MechrevoDeviceCapabilities.Current; }
        catch { return new MechrevoDeviceCapabilities(); }
    }
}
