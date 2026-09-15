using MechrevoLite.Update;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace MechrevoLite.Tests;

/// <summary>
/// 更新链安全加固（静态 OSS 后端）：sha256 必备、下载 host 白名单、严格响应校验、
/// 版本比较用客户端自己的版本、去掉静默回退到旧第三方 host。
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
        UpdateInfo info = Offer("https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v1/pkg.zip", null);

        Assert.False(UpdatePolicy.TryAcceptOffer(info, out string reason));
        Assert.Contains("SHA-256", reason);
        Assert.Null((await UpdateInstaller.DownloadAsync(info)).Path);
    }

    [Fact]
    public async Task NonHexShaIsRefusedBeforeAnyDownload()
    {
        UpdateInfo info = Offer("https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v1/pkg.zip", new string('z', 64));

        Assert.False(UpdatePolicy.TryAcceptOffer(info, out string reason));
        Assert.Contains("SHA-256", reason);
        Assert.Null((await UpdateInstaller.DownloadAsync(info)).Path);
    }

    // ---------------------------------------------------------------- 下载 host 白名单

    [Theory]
    [InlineData("https://github.com/x/y.zip", true)]
    [InlineData("https://objects.githubusercontent.com/x/y.zip", true)]
    [InlineData("https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss/pkg.exe", true)]
    [InlineData("https://evil.example.com/pkg.zip", false)]
    [InlineData("https://github.com.evil.example.com/pkg.zip", false)]
    [InlineData("https://raw.githubusercontent.com/x/y.zip", false)]
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
            Offer("https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v1/pkg.zip", null));
        Assert.Null(noSha.Path);
        Assert.Contains("SHA-256", noSha.Reason!);

        DownloadResult nonHexSha = await UpdateInstaller.DownloadAsync(
            Offer("https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v1/pkg.zip", new string('z', 64)));
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
    public async Task RequestGoesToTheConfiguredBaseAndNeverToTheOldHost()
    {
        const string ossBase = "https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss";
        string? requested = null;
        UpdateChecker.BaseUrlOverride = ossBase;
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
        Assert.StartsWith(ossBase + "/api/update_check.php", requested);
        Assert.DoesNotContain("onismy.cn", requested);
    }

    [Fact]
    public void DefaultBaseIsTheOwnerOssAndTheOldHostIsGone()
    {
        Assert.Contains("lmechrevo.oss-cn-hangzhou.aliyuncs.com", UpdateChecker.DefaultBaseUrl);
        Assert.DoesNotContain("onismy.cn", UpdateChecker.DefaultBaseUrl);
        // 非法基址回退到默认基址（业主 OSS），绝不回退到第三方域名。
        Assert.Equal(UpdateChecker.DefaultBaseUrl, UpdateChecker.NormalizeBaseUrl("http://evil.example.com"));
        Assert.Equal(UpdateChecker.DefaultBaseUrl, UpdateChecker.NormalizeBaseUrl("http://l-mechrevo.onismy.cn"));
        Assert.DoesNotContain("onismy.cn", UpdateChecker.NormalizeBaseUrl("ftp://x"));
    }

    // ---------------------------------------------------------------- 校验失败必须删包

    static string NewSandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lmechrevo-sec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string WritePackage(string dir)
    {
        string zip = Path.Combine(dir, "pkg.zip");
        using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using Stream stream = archive.CreateEntry("L-Mechrevo.exe").Open();
        stream.Write(new byte[] { 1, 2, 3, 4 });
        return zip;
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
    public void AValidPackageWithMatchingHashAndSizeIsAccepted()
    {
        string dir = NewSandbox();
        try
        {
            string zip = WritePackage(dir);
            UpdateInfo info = Offer("https://github.com/x/y.zip", UpdateInstaller.ComputeSha256(zip), size: new FileInfo(zip).Length);
            PackageVerification result = UpdateInstaller.Verify(zip, info);

            Assert.True(result.Ok, result.Reason);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
