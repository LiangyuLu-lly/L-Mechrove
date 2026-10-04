using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MechrevoLite.Gpu;
using MechrevoLite.Update;
using MechrevoLite.Usage;

namespace MechrevoLite.Tests;

/// <summary>
/// 任务 9：扩展心跳字段、日志上传（环形缓冲 + 新崩溃、UTF-8 字节上限、脱敏）、出错后补传节流、
/// 用户开关、问题反馈上传与窗口。全部不触网：发送走 HttpPostOverride / SendOverride。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class UsageTelemetryBeta21Tests
{
    sealed class ConfigRestore : IDisposable
    {
        readonly (string Key, bool Existed, int Value)[] _ints;

        public ConfigRestore(params string[] keys) =>
            _ints = keys.Select(k => (k, AppConfig.Exists(k), AppConfig.Get(k, 0))).ToArray();

        public void Dispose()
        {
            foreach ((string key, bool existed, int value) in _ints)
            {
                if (existed) AppConfig.Set(key, value);
                else AppConfig.Remove(key);
            }
        }
    }

    [Fact]
    public void GenerationAndTierTokensCoverTheNewValues()
    {
        Assert.Equal("1020", UsageSnapshot.GpuGenToken(DgpuGenerationKind.Gen1020));
        Assert.Equal("modern12", UsageSnapshot.TierToken(GcuServiceTier.Modern12));
        Assert.Equal("legacy1020", UsageSnapshot.TierToken(GcuServiceTier.Legacy1020));
        Assert.Equal("foreign", UsageSnapshot.TierToken(GcuServiceTier.Foreign));
        Assert.Equal("unknown", UsageSnapshot.TierToken(GcuServiceTier.Unknown));
    }

    [Fact]
    public void SanitizeBoundsTheNewFields()
    {
        var raw = new UsageSnapshot("abcd1234ef", "0.289.0-beta21", "beat", "M", "P", "B", "50", "G", "C",
            true, false, false, Elevated: true, Tier: "modern12<script>", Port: 70000, Os: "10.0.26100;",
            Update: "ok-0.289.0-beta21", Errors: -5);
        UsageSnapshot clean = UsageSnapshot.Sanitize(raw);

        Assert.True(clean.Elevated);
        Assert.Equal("modern12script", clean.Tier);
        Assert.Equal(0, clean.Port);                 // 不是合法端口 → 0
        Assert.Equal("10.0.26100", clean.Os);
        Assert.Equal("ok-0.289.0-beta21", clean.Update);
        Assert.Equal(0, clean.Errors);
        Assert.Equal(13688, UsageSnapshot.Sanitize(raw with { Port = 13688 }).Port);
    }

    [Fact]
    public void TheHeartbeatCarriesTheNewFields()
    {
        var snap = UsageSnapshot.Sanitize(new UsageSnapshot("abcd1234ef", "0.289.0-beta21", "start", "Model X", "PH6ARxx",
            "IDY", "1020", "GTX 1650", "i7", true, true, false, true, "legacy1020", 13688, "10.0.26100", "fail-1603", 3));
        using JsonDocument doc = JsonDocument.Parse(UsageTelemetry.Serialize(snap));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("elevated").GetBoolean());
        Assert.Equal("legacy1020", root.GetProperty("tier").GetString());
        Assert.Equal(13688, root.GetProperty("port").GetInt32());
        Assert.Equal("10.0.26100", root.GetProperty("os").GetString());
        Assert.Equal("fail-1603", root.GetProperty("upd").GetString());
        Assert.Equal(3, root.GetProperty("errors").GetInt32());
        Assert.Equal("1020", root.GetProperty("gpu").GetString());
    }

    [Fact]
    public void UpdateOutcomeTokensUseOnlyServerSafeCharacters()
    {
        Assert.Equal("ok-0.289.0-beta21", SilentUpdate.OutcomeToken(
            new UpdateOutcome(UpdateOutcomeKind.Succeeded, "0.289.0-beta20", "0.289.0-beta21", 0, "")));
        Assert.Equal("fail-1603", SilentUpdate.OutcomeToken(
            new UpdateOutcome(UpdateOutcomeKind.Failed, "a", "b", 1603, "")));
        Assert.Equal("unknown", SilentUpdate.OutcomeToken(new UpdateOutcome(UpdateOutcomeKind.Unknown, "a", "b", null, "")));
        Assert.Equal("", SilentUpdate.OutcomeToken(new UpdateOutcome(UpdateOutcomeKind.None, "", "", null, "")));
        Assert.Equal("fail-1603", UsageSnapshot.Clip("fail-1603"));
    }

    [Fact]
    public void RedactionCoversNamesMacsAndUserFolders()
    {
        string text = "C:\\Users\\Alice Smith\\AppData\\x.log opened on DESKTOP-ABC by alice; lc mac 3C:A5:51:0B:9F:12\n" +
                      "next line keeps its text";
        string redacted = UsageTelemetry.RedactLog(text, profile: @"C:\Users\Alice Smith", machineName: "DESKTOP-ABC", userName: "alice");

        Assert.DoesNotContain("Alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-ABC", redacted);
        Assert.DoesNotContain("3C:A5:51", redacted);
        Assert.Contains("%USERPROFILE%", redacted);
        Assert.Contains("%COMPUTERNAME%", redacted);
        Assert.Contains("%USERNAME%", redacted);
        Assert.Contains("xx:xx:xx:xx:xx:xx", redacted);
        Assert.Contains("next line keeps its text", redacted);   // 路径正则不跨行吞内容

        // 两个字符的名字不替换：否则日志里无关的字母都会被抹掉。
        Assert.Equal("ab cd", UsageTelemetry.RedactLog("ab cd", null, "ab", "cd"));
    }

    [Fact]
    public void TailUtf8NeverSplitsCharactersAndRespectsTheByteBudget()
    {
        string text = "电池状态读取失败😀abc";
        for (int budget = 0; budget <= Encoding.UTF8.GetByteCount(text) + 2; budget++)
        {
            string tail = UsageTelemetry.TailUtf8(text, budget);
            Assert.True(Encoding.UTF8.GetByteCount(tail) <= budget);
            Assert.EndsWith(tail, text, StringComparison.Ordinal);
            Assert.False(tail.Length > 0 && char.IsLowSurrogate(tail[0]), "tail must not start inside a surrogate pair");
        }
        Assert.Equal(text, UsageTelemetry.TailUtf8(text, 1000));

        string head = FeedbackUpload.HeadUtf8(text, 7);
        Assert.Equal("电池", head);   // 3 + 3 字节，第三个汉字放不下
        Assert.True(Encoding.UTF8.GetByteCount(FeedbackUpload.HeadUtf8(text, 25)) <= 25);
    }

    [Fact]
    public void TheLogPayloadCarriesTheCrashAndTheRingBufferWithinTheLimit()
    {
        string crash = "crash line 失败 " + new string('x', UsageTelemetry.MaxCrashBytes * 2);
        string ring = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"2026-09-30 12:00:00.000: 第 {i} 行 GCU 已连接"));

        string payload = UsageTelemetry.BuildLogPayload(crash, ring);

        Assert.True(Encoding.UTF8.GetByteCount(payload) <= UsageTelemetry.MaxLogBytes);
        Assert.Contains("第 4999 行", payload);                  // 最新的一行一定在
        Assert.StartsWith("--- crash ---", payload);             // 环形缓冲再长也挤不掉崩溃段
        Assert.Contains("--- recent ---", payload);
        Assert.Equal("", UsageTelemetry.BuildLogPayload(null, ""));
        Assert.StartsWith("--- crash ---", UsageTelemetry.BuildLogPayload("boom", ""));
    }

    [Fact]
    public void ChineseStaysReadableOnTheWire()
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["log"] = "电池状态读取失败" }, UsageTelemetry.WireJson);
        Assert.Contains("电池状态读取失败", body);   // 默认编码器会写成 \uXXXX（每字 6 字节）
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(5, 240)]
    [InlineData(50, 300)]
    public void LogRetriesBackOff(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), UsageTelemetry.LogRetryDelay(failures));

    [Fact]
    public void OnlyRealFailuresCountAsErrors()
    {
        int before = Logger.ErrorCount;
        Logger.WriteInfo("usage heartbeat failed: offline");       // 统计上报自己的网络失败不算
        Assert.Equal(before, Logger.ErrorCount);

        // 真机：每次启动都会回显一帧 {"FanErrorStatus":0}，字段名里的 Error 不是失败。
        // 计数只包住两次 HandleMessage（构造 / 释放硬件对象不在窗口内），避免别处的日志混进来。
        using (var hardware = new MechrevoLite.Hardware.MechrevoHw())
        {
            int beforeFan = Logger.ErrorCount;
            hardware.HandleMessage("System/FanErrorInfo", """{"FanErrorStatus":0}""");
            Assert.Equal(beforeFan, Logger.ErrorCount);
            hardware.HandleMessage("System/FanErrorInfo", """{"FanErrorStatus":1}""");
            Assert.Equal(beforeFan + 1, Logger.ErrorCount);
        }
        before = Logger.ErrorCount;
        Logger.WriteLine("GPU switch failed: test");                // 失败词归类
        Logger.WriteError("explicit error");
        Assert.Equal(before + 2, Logger.ErrorCount);
    }

    [Fact]
    public void TheUserSwitchPersistsAndSendsNothingWhenTurnedOff()
    {
        using var restore = new ConfigRestore(UsageTelemetry.EnabledKey);
        var posts = new List<string>();
        Func<string, string, CancellationToken, Task>? previous = UsageTelemetry.HttpPostOverride;
        UsageTelemetry.HttpPostOverride = (url, _, _) => { lock (posts) posts.Add(url); return Task.CompletedTask; };
        try
        {
            UsageTelemetry.SetEnabled(false);
            Assert.False(UsageTelemetry.Enabled);
            Assert.Equal(0, AppConfig.Get(UsageTelemetry.EnabledKey, 1));

            UsageTelemetry.SetEnabled(true);
            Assert.True(UsageTelemetry.Enabled);
            UsageTelemetry.SetEnabled(false);   // 首拍要等 20 s：立刻关掉，循环被取消
            Thread.Sleep(100);
            lock (posts) Assert.Empty(posts);
        }
        finally
        {
            UsageTelemetry.HttpPostOverride = previous;
        }
    }

    /// <summary>
    /// 告知之前什么都不发：心跳循环等到用户看过告知才发第一拍（真机：安装后 20 s 的启动心跳曾赶在告知窗口之前）。
    /// </summary>
    [Fact]
    public void NothingIsSentBeforeTheNoticeHasBeenShown()
    {
        using var restore = new ConfigRestore(UsageTelemetry.EnabledKey, UsageTelemetry.NoticeVersionKey);
        var posts = new List<string>();
        Func<string, string, CancellationToken, Task>? previousPost = UsageTelemetry.HttpPostOverride;
        TimeSpan previousDelay = UsageTelemetry.FirstDelay;
        bool audit = Program.UiAuditMode;
        UsageTelemetry.HttpPostOverride = (url, _, _) => { lock (posts) posts.Add(url); return Task.CompletedTask; };
        UsageTelemetry.FirstDelay = TimeSpan.FromMilliseconds(10);
        Program.UiAuditMode = false;
        try
        {
            AppConfig.Remove(UsageTelemetry.NoticeVersionKey);
            UsageTelemetry.ResetNoticeGateForTests();
            UsageTelemetry.SetEnabled(true);
            Thread.Sleep(400);
            lock (posts) Assert.Empty(posts);

            UsageTelemetry.MarkNoticeShown();
            Assert.True(SpinWait.SpinUntil(() => { lock (posts) return posts.Any(u => u.EndsWith("/api/heartbeat.php")); }, 5000),
                "the first heartbeat goes out once the notice has been shown");
        }
        finally
        {
            UsageTelemetry.SetEnabled(false);
            UsageTelemetry.HttpPostOverride = previousPost;
            UsageTelemetry.FirstDelay = previousDelay;
            Program.UiAuditMode = audit;
        }
    }

    [Fact]
    public void TheNoticeIsShownOnce()
    {
        using var restore = new ConfigRestore(UsageTelemetry.NoticeVersionKey);
        AppConfig.Remove(UsageTelemetry.NoticeVersionKey);
        Assert.False(UsageTelemetry.NoticeShown);
        UsageTelemetry.MarkNoticeShown();
        Assert.True(UsageTelemetry.NoticeShown);
    }
}

[Collection(nameof(SerialGpuSwitchCollection))]
public class FeedbackUploadTests
{
    [Fact]
    public void TheEndpointSitsNextToTheHeartbeat() =>
        Assert.Equal("https://stats.l-mechrevo.cn/api/feedback.php", FeedbackUpload.BuildUrl(UsageTelemetry.DefaultBaseUrl));

    [Theory]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("abc", false)]
    [InlineData("灯不亮了", true)]
    public void AMessageNeedsAFewCharacters(string? message, bool sendable) =>
        Assert.Equal(sendable, FeedbackUpload.IsSendable(message));

    [Fact]
    public void WithoutAttachmentOnlyTheMessageAndContactAreSent()
    {
        string body = FeedbackUpload.BuildBody("abcd1234ef", "0.289.0-beta21", "Model X", "PH6ARxx", "50",
            "  切换显卡后黑屏  ", " qq 12345 ", attach: false, systemInfo: "BIOS 1.0", log: "secret log");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.Equal("切换显卡后黑屏", root.GetProperty("message").GetString());
        Assert.Equal("qq 12345", root.GetProperty("contact").GetString());
        Assert.Equal("", root.GetProperty("sysinfo").GetString());
        Assert.Equal("", root.GetProperty("log").GetString());
        Assert.Contains("切换显卡后黑屏", body);   // UTF-8 原样，不是 \uXXXX
    }

    [Fact]
    public void AttachmentsAreRedactedAndBounded()
    {
        string sys = "用户目录 C:\\Users\\Bob\\x " + new string('系', 20_000);
        string log = string.Concat(Enumerable.Repeat("行 失败 3C:A5:51:0B:9F:12\n", 20_000)) + "最后一行";
        string body = FeedbackUpload.BuildBody("abcd1234ef", "v", "m", "p", "g", new string('问', 5000), new string('c', 500),
            attach: true, systemInfo: sys, log: log);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.Equal(FeedbackUpload.MaxMessageChars, root.GetProperty("message").GetString()!.Length);
        Assert.Equal(FeedbackUpload.MaxContactChars, root.GetProperty("contact").GetString()!.Length);
        string sent = root.GetProperty("sysinfo").GetString()!;
        Assert.DoesNotContain("Bob", sent);
        Assert.True(Encoding.UTF8.GetByteCount(sent) <= FeedbackUpload.MaxSysInfoBytes);
        string sentLog = root.GetProperty("log").GetString()!;
        Assert.EndsWith("最后一行", sentLog);
        Assert.DoesNotContain("3C:A5:51", sentLog);
        Assert.True(Encoding.UTF8.GetByteCount(sentLog) <= FeedbackUpload.MaxLogBytes);
        Assert.True(Encoding.UTF8.GetByteCount(body) < 256 * 1024, "the server rejects bodies over 256 KB");
    }

    [Fact]
    public void ServerRepliesMapToResults()
    {
        FeedbackResult ok = FeedbackUpload.ParseResponse(200, """{"ok":true,"ticket":"20260930-061500-a1b2c3"}""");
        Assert.True(ok.Ok);
        Assert.Equal("20260930-061500-a1b2c3", ok.Ticket);

        Assert.Equal(Properties.Strings.FeedbackRateLimited, FeedbackUpload.ParseResponse(429, """{"ok":false,"error":"rate limited"}""").Error);
        Assert.Equal(Properties.Strings.FeedbackTooLarge, FeedbackUpload.ParseResponse(413, "").Error);
        FeedbackResult failed = FeedbackUpload.ParseResponse(500, """{"ok":false,"error":"write"}""");
        Assert.False(failed.Ok);
        Assert.Equal("HTTP 500 · write", failed.Error);
        Assert.False(FeedbackUpload.ParseResponse(200, "<html>").Ok);
        Assert.False(FeedbackUpload.ParseResponse(200, """{"ok":true}""").Ok);   // 没编号不算成功
    }

    [Fact]
    public async Task SendingGoesToTheFeedbackEndpoint()
    {
        Func<string, string, CancellationToken, Task<string>>? previous = FeedbackUpload.HttpPostOverride;
        string? url = null, body = null;
        FeedbackUpload.HttpPostOverride = (u, b, _) =>
        {
            url = u;
            body = b;
            return Task.FromResult("""{"ok":true,"ticket":"t-1"}""");
        };
        try
        {
            FeedbackResult result = await FeedbackUpload.SendAsync("风扇一直满转", "mail@example.com", attach: false, CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Equal("t-1", result.Ticket);
            Assert.EndsWith("/api/feedback.php", url);
            Assert.Contains("风扇一直满转", body);

            FeedbackResult tooShort = await FeedbackUpload.SendAsync("  ", null, attach: false, CancellationToken.None);
            Assert.False(tooShort.Ok);
            Assert.Equal(Properties.Strings.FeedbackTooShort, tooShort.Error);
        }
        finally
        {
            FeedbackUpload.HttpPostOverride = previous;
        }
    }

    [Fact]
    public async Task TheFormShowsTheTicketOrTheReason()
    {
        var calls = new List<(string Message, string? Contact, bool Attach)>();
        Func<string, string?, bool, CancellationToken, Task<FeedbackResult>>? previous = FeedbackForm.SendOverride;
        try
        {
            using var form = new FeedbackForm();

            FeedbackForm.SendOverride = (m, c, a, _) => { calls.Add((m, c, a)); return Task.FromResult(new FeedbackResult(true, "t-9", null)); };
            form.SetInputsForTests("ab", null, attach: true);
            await form.SendAsync();
            Assert.Empty(calls);                                      // 描述太短：不发
            Assert.Equal(Properties.Strings.FeedbackTooShort, form.StatusText);

            FeedbackForm.SendOverride = (m, c, a, _) => { calls.Add((m, c, a)); return Task.FromResult(new FeedbackResult(false, null, "HTTP 500")); };
            form.SetInputsForTests("灯效切换后键盘不亮", "qq 1", attach: false);
            await form.SendAsync();
            Assert.Single(calls);
            Assert.Equal(("灯效切换后键盘不亮", "qq 1", false), calls[0]);
            Assert.Equal(string.Format(Properties.Strings.FeedbackFailed, "HTTP 500"), form.StatusText);

            FeedbackForm.SendOverride = (m, c, a, _) => { calls.Add((m, c, a)); return Task.FromResult(new FeedbackResult(true, "t-9", null)); };
            await form.SendAsync();
            Assert.Equal(2, calls.Count);
            Assert.Equal(string.Format(Properties.Strings.FeedbackSent, "t-9"), form.StatusText);
        }
        finally
        {
            FeedbackForm.SendOverride = previous;
        }
    }
}

[Collection(nameof(SerialGpuSwitchCollection))]
public class TelemetryNoticeUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGuideCarriesTheTelemetrySwitch(bool telemetryOnly)
    {
        using var form = new FirstRunGuideForm(telemetryOnly);
        var check = form.Controls.Find("checkFirstRunTelemetry", true).OfType<CheckBox>().Single();
        Assert.Equal(UsageTelemetry.Enabled, check.Checked);
        check.Checked = !check.Checked;
        Assert.Equal(check.Checked, form.TelemetryChecked);
    }

    [Fact]
    public void TheSettingsDialogHasThePrivacyGroup()
    {
        bool existed = AppConfig.Exists(UsageTelemetry.EnabledKey);
        int value = AppConfig.Get(UsageTelemetry.EnabledKey, 1);
        Func<string, string, CancellationToken, Task>? previous = UsageTelemetry.HttpPostOverride;
        UsageTelemetry.HttpPostOverride = (_, _, _) => Task.CompletedTask;
        try
        {
            AppConfig.Set(UsageTelemetry.EnabledKey, 1);
            var theme = SettingsForm.CreateThemeModePanel(v => v, _ => { }, out _, out _);
            using var dialog = new SettingsDialog(theme, null, displayGroupAvailable: false);
            var check = dialog.Controls.Find("checkUsageTelemetry", true).OfType<CheckBox>().Single();
            Assert.True(check.Checked);
            Assert.Single(dialog.Controls.Find("buttonFeedback", true));

            check.Checked = false;
            Assert.False(UsageTelemetry.Enabled);
        }
        finally
        {
            UsageTelemetry.SetEnabled(false);
            UsageTelemetry.HttpPostOverride = previous;
            if (existed) AppConfig.Set(UsageTelemetry.EnabledKey, value);
            else AppConfig.Remove(UsageTelemetry.EnabledKey);
        }
    }
}
