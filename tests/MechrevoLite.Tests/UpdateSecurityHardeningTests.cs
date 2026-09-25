using MechrevoLite.Update;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MechrevoLite.Tests;

/// <summary>
/// 更新链安全加固（粉丝站）：sha256 必备、下载 host 白名单、严格响应校验、
/// 版本比较用客户端自己的版本、非法基址回退到粉丝站默认基址。
///
/// 这些测试全部 fail-closed：任何一条准则被破坏都必须表现为"拒绝"，绝不是"跳过校验"。
/// </summary>
public class UpdateSecurityHardeningTests
{
    // 合法 sha256 形状（64 位十六进制）。
    const string Sha = "0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef";

    static UpdateInfo Offer(string? url, string? sha, long? size = null, string latest = "0.289.0-beta16") =>
        new("0.289.0-beta15", latest, "beta", false, true, null, null, null, size, sha, url, null);

    // ---------------------------------------------------------------- sha256 必备

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]  // 63
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0", false)] // 65
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]    // 非十六进制
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sha256MustBeSixtyFourHexChars(string? value, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.IsValidSha256(value));

    [Fact]
    public async Task MissingShaIsRefusedBeforeAnyDownload()
    {
        UpdateInfo info = Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", null);

        Assert.False(UpdatePolicy.TryAcceptOffer(info, out string reason));
        Assert.Contains("SHA-256", reason);
        Assert.Null((await UpdateInstaller.DownloadAsync(info)).Path);
    }

    [Fact]
    public async Task NonHexShaIsRefusedBeforeAnyDownload()
    {
        UpdateInfo info = Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", new string('z', 64));

        Assert.False(UpdatePolicy.TryAcceptOffer(info, out string reason));
        Assert.Contains("SHA-256", reason);
        Assert.Null((await UpdateInstaller.DownloadAsync(info)).Path);
    }

    // ---------------------------------------------------------------- 下载 host 白名单

    [Theory]
    [InlineData("https://stats.l-mechrevo.cn/uploads/pkg.exe", true)]
    [InlineData("https://stats.l-mechrevo.cn/uploads/pkg.zip", true)]
    [InlineData("https://l-mechrevo.cn/files/pkg.exe", true)]
    [InlineData("https://l-mechrevo.onismy.cn/uploads/pkg.exe", false)]
    [InlineData("https://github.com/x/y.zip", false)]
    [InlineData("https://objects.githubusercontent.com/x/y.zip", false)]
    [InlineData("https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss/pkg.exe", false)]
    [InlineData("https://evil.example.com/pkg.zip", false)]
    [InlineData("https://github.com.evil.example.com/pkg.zip", false)]
    [InlineData("https://stats.l-mechrevo.cn.evil.example.com/pkg.exe", false)]
    public void OnlyAllowlistedDownloadHostsAreAccepted(string url, bool expected)
    {
        Assert.True(UpdatePolicy.TryAcceptDownloadUrl(url, out _, out _) == expected,
            $"url={url} expected accepted={expected}");
    }

    [Fact]
    public async Task NonAllowlistedDownloadHostIsRefusedAndLogged()
    {
        UpdateInfo info = Offer("https://evil.example.com/pkg.zip", Sha, size: 123);

        Assert.False(UpdatePolicy.TryAcceptOffer(info, out string reason));
        Assert.Contains("允许列表", reason);
        Assert.Null((await UpdateInstaller.DownloadAsync(info)).Path);
    }

    /// <summary>
    /// T0.1 加固：DownloadAsync 改为返回 DownloadResult 后，拒绝语义必须原样保留，
    /// 且失败原因可读（UI 能解释为什么下不了）。
    /// </summary>
    [Fact]
    public async Task RefusalsStayFailClosedAndCarryAReadableReason()
    {
        DownloadResult noSha = await UpdateInstaller.DownloadAsync(
            Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", null));
        Assert.Null(noSha.Path);
        Assert.Contains("SHA-256", noSha.Reason!);

        DownloadResult nonHexSha = await UpdateInstaller.DownloadAsync(
            Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", new string('z', 64)));
        Assert.Null(nonHexSha.Path);
        Assert.Contains("SHA-256", nonHexSha.Reason!);

        DownloadResult nonAllowlisted = await UpdateInstaller.DownloadAsync(
            Offer("https://evil.example.com/pkg.zip", Sha, size: 123));
        Assert.Null(nonAllowlisted.Path);
        Assert.Contains("允许列表", nonAllowlisted.Reason!);

        DownloadResult plainHttp = await UpdateInstaller.DownloadAsync(
            Offer("http://evil.example.com/pkg.zip", Sha, size: 123));
        Assert.Null(plainHttp.Path);
        Assert.Contains("HTTPS", plainHttp.Reason!);
    }

    [Fact]
    public void HttpDownloadUrlIsRefused()
    {
        Assert.False(UpdatePolicy.TryAcceptDownloadUrl("http://evil.example.com/pkg.zip", out _, out string reason));
        Assert.Contains("HTTPS", reason);
    }

    [Fact]
    public void LoopbackHttpIsAllowedForLocalStubs()
    {
        Assert.True(UpdatePolicy.TryAcceptDownloadUrl("http://127.0.0.1:8899/pkg.zip", out Uri? uri, out _));
        Assert.True(uri!.IsLoopback);
    }

    /// <summary>
    /// 3xx 是硬失败：Location 即使指向另一台回环（或任意 evil host）也不得跟随，
    /// 不得写出包体。host 白名单仍只做请求前检查，不为 GitHub 302 加 host。
    /// </summary>
    [Fact]
    public async Task RedirectedDownloadIsRefusedAndWritesNoBytes()
    {
        byte[] evilPayload = Encoding.ASCII.GetBytes("HARDENING-EVIL-PAYLOAD");
        var evilHits = new ConcurrentQueue<string>();
        (HttpListener evil, string evilUrl) = StartLoopbackListener();
        (HttpListener origin, string originUrl) = StartLoopbackListener();
        using var stop = new CancellationTokenSource();
        Task evilTask = Task.Run(() => ServeBytes(evil, evilPayload, evilHits, stop.Token));
        Task originTask = Task.Run(() => ServeRedirect(origin, evilUrl + "/malware.zip", stop.Token));

        string sandbox = NewSandbox();
        string isolatedTemp = Path.Combine(sandbox, "sys-temp");
        Directory.CreateDirectory(isolatedTemp);
        string? oldTmp = Environment.GetEnvironmentVariable("TMP");
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            Environment.SetEnvironmentVariable("TMP", isolatedTemp);
            Environment.SetEnvironmentVariable("TEMP", isolatedTemp);

            DownloadResult result = await UpdateInstaller.DownloadAsync(
                Offer(originUrl + "/pkg.zip", Sha, size: evilPayload.Length));

            Assert.Null(result.Path);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.Contains("302", result.Reason);
            Assert.Contains("重定向", result.Reason);
            Assert.True(evilHits.IsEmpty, "不得跟随 Location 去 evil host");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            stop.Cancel();
            origin.Stop();
            origin.Close();
            evil.Stop();
            evil.Close();
            await originTask;
            await evilTask;
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    [Fact]
    public async Task LoopbackHttp200WithValidShaStillDownloads()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hardening-loopback-ok");
        string sha = Convert.ToHexString(SHA256.HashData(payload));
        var hits = new ConcurrentQueue<string>();
        (HttpListener listener, string baseUrl) = StartLoopbackListener();
        using var stop = new CancellationTokenSource();
        Task server = Task.Run(() => ServeBytes(listener, payload, hits, stop.Token));

        string sandbox = NewSandbox();
        string isolatedTemp = Path.Combine(sandbox, "sys-temp");
        Directory.CreateDirectory(isolatedTemp);
        string? oldTmp = Environment.GetEnvironmentVariable("TMP");
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            Environment.SetEnvironmentVariable("TMP", isolatedTemp);
            Environment.SetEnvironmentVariable("TEMP", isolatedTemp);

            DownloadResult result = await UpdateInstaller.DownloadAsync(
                Offer(baseUrl + "/pkg.zip", sha, size: payload.Length));

            Assert.NotNull(result.Path);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(payload, File.ReadAllBytes(result.Path!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            stop.Cancel();
            listener.Stop();
            listener.Close();
            await server;
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- 严格响应校验

    [Fact]
    public void OkFalseIsRejected()
    {
        Assert.Null(UpdateChecker.ParseResponse(
            """{"ok":false,"data":{"latest_version":"0.289.0-beta16","update_available":true}}"""));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("not a version")]
    [InlineData("1..2")]
    [InlineData("latest")]
    public void MalformedLatestVersionIsRejected(string latest)
    {
        string json = "{\"ok\":true,\"data\":{\"latest_version\":\"" + latest + "\",\"update_available\":true}}";
        Assert.Null(UpdateChecker.ParseResponse(json));
    }

    [Fact]
    public void HttpDownloadUrlMakesTheWholeResponseInvalid()
    {
        string json =
            """{"ok":true,"data":{"latest_version":"0.289.0-beta16","update_available":true,"download_url":"http://evil.example.com/pkg.zip"}}""";
        Assert.Null(UpdateChecker.ParseResponse(json));
    }

    // ---------------------------------------------------------------- 版本比较用客户端自己的版本

    const string NewestJson = """
    {"ok":true,"data":{"current_version":"0.289.0-beta13","latest_version":"0.289.0-beta15",
    "update_available":true,"channel":"beta",
    "download_url":"https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v1/pkg.zip",
    "sha256":"0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef"}}
    """;

    /// <summary>
    /// 已知缺陷回归：客户端已经是 JSON 里的 latest_version，但 JSON 的 current_version 是旧的
    /// （静态文件冻结值）→ 过去会永久误报"有更新"。现在按客户端自己的版本比较，必须是"无更新"。
    /// </summary>
    [Fact]
    public async Task ClientAtTheNewestVersionSeesNoUpdateEvenThoughJsonSaysAvailable()
    {
        UpdateChecker.HttpGetOverride = _ => Task.FromResult(NewestJson);
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: "0.289.0-beta15");

            Assert.NotNull(info);
            Assert.False(info!.UpdateAvailable);
            Assert.Equal("0.289.0-beta15", info.CurrentVersion);        // 本机版本
            Assert.Equal("0.289.0-beta13", info.ServerCurrentVersion);  // 服务端 echo 仅作参考
        }
        finally { UpdateChecker.HttpGetOverride = null; }
    }

    [Fact]
    public async Task AnActuallyNewerOfferIsKept()
    {
        UpdateChecker.HttpGetOverride = _ => Task.FromResult(NewestJson);
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: "0.289.0-beta14");
            Assert.NotNull(info);
            Assert.True(info!.UpdateAvailable);
        }
        finally { UpdateChecker.HttpGetOverride = null; }
    }

    [Fact]
    public async Task ADowngradeOfferIsTreatedAsNoUpdate()
    {
        const string downgrade = """
        {"ok":true,"data":{"current_version":"0.289.0-beta15","latest_version":"0.289.0-beta14",
        "update_available":true}}
        """;
        UpdateChecker.HttpGetOverride = _ => Task.FromResult(downgrade);
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: "0.289.0-beta15");
            Assert.NotNull(info);
            Assert.False(info!.UpdateAvailable);
        }
        finally { UpdateChecker.HttpGetOverride = null; }
    }

    // ---------------------------------------------------------------- 无静默回退到旧第三方 host

    [Fact]
    public async Task RequestGoesToTheConfiguredBaseAndNeverToOssOrGitHub()
    {
        const string fanBase = "https://stats.l-mechrevo.cn";
        string? requested = null;
        UpdateChecker.BaseUrlOverride = fanBase;
        UpdateChecker.HttpGetOverride = url => { requested = url; return Task.FromResult(NewestJson); };
        try
        {
            await UpdateChecker.CheckAsync(force: true, versionOverride: "0.289.0-beta14");
        }
        finally
        {
            UpdateChecker.BaseUrlOverride = null;
            UpdateChecker.HttpGetOverride = null;
        }

        Assert.NotNull(requested);
        Assert.StartsWith(fanBase + "/api/update_check.php", requested);
        Assert.DoesNotContain("oss-cn-hangzhou", requested);
        Assert.DoesNotContain("github.com", requested);
    }

    [Fact]
    public void DefaultBaseIsTheFanHost()
    {
        Assert.Contains("stats.l-mechrevo.cn", UpdateChecker.DefaultBaseUrl);
        Assert.DoesNotContain("oss-cn-hangzhou", UpdateChecker.DefaultBaseUrl);
        Assert.Equal(UpdateChecker.DefaultBaseUrl, UpdateChecker.NormalizeBaseUrl("http://evil.example.com"));
        Assert.Equal(UpdateChecker.DefaultBaseUrl, UpdateChecker.NormalizeBaseUrl("http://stats.l-mechrevo.cn"));
        Assert.Equal(UpdateChecker.DefaultBaseUrl, UpdateChecker.NormalizeBaseUrl("ftp://x"));
    }

    // ---------------------------------------------------------------- 校验失败必须删包

    static string NewSandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lmechrevo-sec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void ServeRedirect(HttpListener listener, string location, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = location;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Close();
        }
    }

    static void ServeBytes(HttpListener listener, byte[] body, ConcurrentQueue<string> hits, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            hits.Enqueue(context.Request.RawUrl ?? "");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }
    }

    static (HttpListener Listener, string BaseUrl) StartLoopbackListener()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            string baseUrl = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(baseUrl + "/");
            try
            {
                listener.Start();
                return (listener, baseUrl);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("无法在 127.0.0.1 上启动 HttpListener 桩服务器。");
    }

    static string WritePackage(string dir)
    {
        string zip = Path.Combine(dir, "pkg.zip");
        using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using Stream stream = archive.CreateEntry("L-Mechrevo.exe").Open();
        stream.Write(new byte[] { 1, 2, 3, 4 });
        return zip;
    }

    static string WriteSignedPackage(string dir)
    {
        string zip = Path.Combine(dir, "pkg.zip");
        using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using Stream stream = archive.CreateEntry("L-Mechrevo.exe").Open();
        using FileStream source = File.OpenRead(EmbeddedSignedExe());
        source.CopyTo(stream);
        return zip;
    }

    static string EmbeddedSignedExe()
    {
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet)) return dotnet;
        foreach (string name in new[] { "MpSigStub.exe", "AcSignOpt.exe", "MRT.exe" })
        {
            string path = Path.Combine(Environment.SystemDirectory, name);
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("本机没有可用的嵌入式 Authenticode 签名 exe（dotnet.exe / MpSigStub.exe）。");
    }

    [Fact]
    public void WrongHashIsRefusedAndTheTempPackageIsDeleted()
    {
        string dir = NewSandbox();
        try
        {
            string zip = WritePackage(dir);
            UpdateInfo info = Offer("https://github.com/x/y.zip", new string('a', 64), size: new FileInfo(zip).Length);
            PackageVerification result = UpdateInstaller.Verify(zip, info);

            Assert.False(result.Ok);
            Assert.Contains("SHA-256", result.Reason);

            UpdateInstaller.DiscardPackage(zip);
            Assert.False(File.Exists(zip), "校验失败后临时包必须被删除");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ADeclaredSizeMismatchIsRefused()
    {
        string dir = NewSandbox();
        try
        {
            string zip = WritePackage(dir);
            UpdateInfo info = Offer("https://github.com/x/y.zip", UpdateInstaller.ComputeSha256(zip), size: 1);
            PackageVerification result = UpdateInstaller.Verify(zip, info);

            Assert.False(result.Ok);
            Assert.Contains("大小", result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EmbeddedSignedDotnetHasValidAuthenticode()
    {
        string signed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        Assert.True(File.Exists(signed), signed);
        Assert.True(UpdatePolicy.TryAcceptAuthenticode(signed, out string reason), reason);
    }

    [Fact]
    public void PackageWithoutValidAuthenticodeIsNotInstalled()
    {
        string dir = NewSandbox();
        try
        {
            string zip = WritePackage(dir);
            UpdateInfo info = Offer(
                "https://github.com/x/y.zip",
                UpdateInstaller.ComputeSha256(zip),
                size: new FileInfo(zip).Length);
            PackageVerification result = UpdateInstaller.Verify(zip, info);

            Assert.False(result.Ok);
            Assert.True(
                result.Reason.Contains("Authenticode", StringComparison.OrdinalIgnoreCase)
                || result.Reason.Contains("签名", StringComparison.Ordinal),
                result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SizeOutsideAllowlistIsNotInstalled()
    {
        UpdateInfo oversized = Offer(
            "https://stats.l-mechrevo.cn/uploads/pkg.zip", Sha, size: UpdateChecker.MaxPackageBytes + 1);
        Assert.False(UpdatePolicy.TryAcceptOffer(oversized, out string allowlistReason));
        Assert.Contains("体积", allowlistReason);

        UpdateInfo missing = Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", Sha, size: null);
        Assert.False(UpdatePolicy.TryAcceptOffer(missing, out string missingReason));
        Assert.Contains("体积", missingReason);

        string dir = NewSandbox();
        try
        {
            string zip = WritePackage(dir);
            UpdateInfo undeclared = Offer(
                "https://github.com/x/y.zip", UpdateInstaller.ComputeSha256(zip), size: null);
            PackageVerification result = UpdateInstaller.Verify(zip, undeclared);
            Assert.False(result.Ok);
            Assert.Contains("大小", result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void AValidPackageWithMatchingHashAndSizeIsAccepted()
    {
        string dir = NewSandbox();
        try
        {
            string zip = WriteSignedPackage(dir);
            UpdateInfo info = Offer("https://github.com/x/y.zip", UpdateInstaller.ComputeSha256(zip), size: new FileInfo(zip).Length);
            PackageVerification result = UpdateInstaller.Verify(zip, info);

            Assert.True(result.Ok, result.Reason);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LooksLikeInstallerPackage_DetectsExeNameAndUrl()
    {
        Assert.True(UpdateInstaller.LooksLikeInstallerPackage(
            Offer("https://stats.l-mechrevo.cn/uploads/pkg-abc.exe", Sha, size: 1) with { FileName = "pkg-abc.exe" }));
        Assert.True(UpdateInstaller.LooksLikeInstallerPackage(
            Offer("https://stats.l-mechrevo.cn/uploads/pkg-abc.exe", Sha, size: 1)));
        Assert.False(UpdateInstaller.LooksLikeInstallerPackage(
            Offer("https://stats.l-mechrevo.cn/uploads/pkg.zip", Sha, size: 1) with { FileName = "pkg.zip" }));
    }

    [Fact]
    public void PeInstallerWithMatchingHashAndSizeIsAcceptedWithoutAuthenticode()
    {
        string dir = NewSandbox();
        try
        {
            string exe = Path.Combine(dir, "L-Mechrevo-setup.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);
            UpdateInfo info = Offer(
                "https://stats.l-mechrevo.cn/uploads/setup.exe",
                UpdateInstaller.ComputeSha256(exe),
                size: new FileInfo(exe).Length) with { FileName = "L-Mechrevo-setup.exe" };
            PackageVerification result = UpdateInstaller.Verify(exe, info);
            Assert.True(result.Ok, result.Reason);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
