using MechrevoLite.Update;
using System.IO.Compression;
using System.Reflection;

namespace MechrevoLite.Tests;

/// <summary>
/// 更新链路的纯逻辑部分：版本比较、响应解析、下载地址策略、包校验。
///
/// 版本比较的口径来自真机实测（2026-09-11 对 stats.l-mechrevo.cn）：
/// 服务端按"提取数字段后比数值"，所以 beta13 ≡ 13-beta、beta9 &lt; beta10、
/// 而 0.289.0.0（我们的 AssemblyVersion 形态）会被判成比 13-beta 更旧。
/// </summary>
public class UpdateVersionTests
{
    [Theory]
    [InlineData("beta13", "13-beta", 0)]      // 两种写法服务端判等价（实测）
    [InlineData("beta13", "beta13", 0)]
    [InlineData("beta14", "beta13", 1)]
    [InlineData("beta13", "beta14", -1)]
    [InlineData("beta10", "beta9", 1)]        // 数字段按数值比，不是字典序（字典序会判反）
    [InlineData("beta13.1", "beta13", 1)]     // 缺段补 0，多了个非零段就更新
    [InlineData("beta13.0", "beta13", 0)]     // 补 0 的等价情况
    [InlineData("0.289.0.0", "13-beta", -1)]  // 我们的 AssemblyVersion 形态：会被判成更旧
    [InlineData("1.0.0", "beta13", -1)]       // 离开 beta 体系后数字段会突然变小（已知坑）
    [InlineData("0.289.0-beta14", "0.289.0-beta13", 1)]  // 当前发布形态：完整点分版本按数字段比较照样正确
    [InlineData("0.289.0-beta13", "0.289.0-beta13", 0)]
    [InlineData("0.289.0-beta13", "0.289.0-beta14", -1)]
    [InlineData("v9.5", "9.5.0", 0)]          // 前缀字母不参与比较
    [InlineData("9.10", "9.5", 1)]
    public void CompareMatchesTheServerBehaviour(string left, string right, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(UpdateVersion.Compare(left, right)));

    [Theory]
    [InlineData("beta14", "beta13", true)]
    [InlineData("beta13", "beta13", false)]
    [InlineData("beta9", "beta10", false)]
    [InlineData("0.290.3", "0.290.2", true)]
    [InlineData("0.290.3", "0.289.0-beta21", true)]
    [InlineData("0.290.3", "0.289.0-beta20", true)]
    public void IsNewerOnlyForActuallyNewerVersions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));

    [Fact]
    public void EmptyOrUnknownVersionsAreTreatedAsOldest()
    {
        Assert.Equal(0, UpdateVersion.Compare("", ""));
        Assert.True(UpdateVersion.IsNewer("beta1", "unknown"));
    }
}

/// <summary>
/// 检测器的解析与地址策略。响应样例取自真机实测（2026-09-11 抓到的真实 JSON）。
/// </summary>
public class UpdateCheckerTests
{
    const string LiveSample = """
    {"ok":true,"data":{"current_version":"0.1.0.0","latest_version":"13-beta","channel":"beta",
    "channel_fallback":false,"is_latest":false,"update_available":true,"release_date":"2026-08-31",
    "notes":"- 修复重复启动。\r\n- 改进性能模式。","filename":null,"size":null,"sha256":null,
    "download_url":"https://stats.l-mechrevo.cn/lzzl.php?url=https%3A%2F%2Fminestar.lanzouu.com%2FikY8b462fw5a&type=down",
    "download_page":"https://stats.l-mechrevo.cn/download.html","checked_at":"2026-09-11T13:46:55+08:00"}}
    """;

    [Fact]
    public void ParsesTheLiveServerSample()
    {
        UpdateInfo? info = UpdateChecker.ParseResponse(LiveSample);

        Assert.NotNull(info);
        Assert.Equal("13-beta", info!.LatestVersion);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("beta", info.Channel);
        Assert.False(info.ChannelFallback);
        Assert.Equal("2026-08-31", info.ReleaseDate);
        Assert.Contains("改进性能模式", info.Notes);
        Assert.Null(info.Sha256);
        Assert.Contains("lzzl.php", info.DownloadUrl);
        Assert.Equal("https://stats.l-mechrevo.cn/download.html", info.DownloadPage);
        // 网盘发布：没有 sha256 → 不能算"可校验的包"
        Assert.False(info.HasVerifiablePackage);
    }

    [Fact]
    public void PackageIsOnlyVerifiableWhenBothUrlAndHashArePresent()
    {
        string json = LiveSample
            .Replace("\"sha256\":null", "\"sha256\":\"" + new string('a', 64) + "\"")
            .Replace("\"filename\":null", "\"filename\":\"L-Mechrevo-13-beta.zip\"");
        UpdateInfo? info = UpdateChecker.ParseResponse(json);

        Assert.NotNull(info);
        Assert.True(info!.HasVerifiablePackage);
    }

    /// <summary>sha256 必须是 64 位十六进制；长度不对的"看起来像哈希"的串不算数。</summary>
    [Fact]
    public void APartialHashIsNotVerifiable()
    {
        string json = LiveSample.Replace("\"sha256\":null", "\"sha256\":\"e3b0c44298fc1c14\"");
        UpdateInfo? info = UpdateChecker.ParseResponse(json);

        Assert.NotNull(info);
        Assert.False(info!.HasVerifiablePackage);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"缺少 version 参数\"}")]
    [InlineData("{\"ok\":true}")]           // 没有 data
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void MalformedResponsesReturnNull(string? json) =>
        Assert.Null(UpdateChecker.ParseResponse(json));

    /// <summary>服务端从未发布过版本时 latest_version 为 null 且 is_latest=true——不能提示更新。</summary>
    [Fact]
    public void NeverReportsAnUpdateWhenTheServerHasNoRelease()
    {
        UpdateInfo? info = UpdateChecker.ParseResponse(
            """{"ok":true,"data":{"current_version":"beta13","latest_version":null,"is_latest":true,"update_available":true,"channel":"beta"}}""");

        Assert.NotNull(info);
        Assert.Null(info!.LatestVersion);
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public void ToleratesNumericFieldsSentAsStrings()
    {
        UpdateInfo? info = UpdateChecker.ParseResponse(
            """{"ok":true,"data":{"latest_version":"beta14","size":"8388608","update_available":true}}""");

        Assert.Equal(8388608L, info!.Size);
    }

    [Theory]
    [InlineData("https://stats.l-mechrevo.cn", "https://stats.l-mechrevo.cn/api/update_check.php?version=beta13&channel=beta")]
    [InlineData("https://stats.l-mechrevo.cn/", "https://stats.l-mechrevo.cn/api/update_check.php?version=beta13&channel=beta")]
    public void BuildsTheDocumentedCheckUrl(string baseUrl, string expected) =>
        Assert.Equal(expected, UpdateChecker.BuildCheckUrl(baseUrl, "beta13"));

    /// <summary>只认 HTTPS；明文 http 只有回环地址放行（本地联调桩服务器用）。</summary>
    [Theory]
    [InlineData("https://stats.l-mechrevo.cn", "https://stats.l-mechrevo.cn")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://stats.l-mechrevo.cn", UpdateChecker.DefaultBaseUrl)]
    [InlineData("ftp://x", UpdateChecker.DefaultBaseUrl)]
    [InlineData("", UpdateChecker.DefaultBaseUrl)]
    [InlineData(null, UpdateChecker.DefaultBaseUrl)]
    public void OnlyHttpsAndLoopbackSurviveUrlNormalization(string? raw, string expected) =>
        Assert.Equal(expected, UpdateChecker.NormalizeBaseUrl(raw));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(48, true)]
    public void AutoCheckIsThrottledAcrossSessions(int hoursSinceLastCheck, bool expected)
    {
        var now = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, UpdateChecker.ShouldAutoCheck(now, now.AddHours(-hoursSinceLastCheck)));
    }

    /// <summary>
    /// 检测必须传完整点分版本（"0.289.0-beta13"）而不是裸标签 "beta13"。
    /// 服务端只接受形如 9.6.2 / v9.6-beta 的版本号；裸标签会被判格式错误，
    /// update_available 永远返回 false（2026-09-13 真机实测）。这里用注入的取数接缝把真实 URL 抓下来断言。
    /// </summary>
    [Fact]
    public async Task CheckSendsTheFullSemanticVersionNotTheBareLabel()
    {
        string? requested = null;
        UpdateChecker.HttpGetOverride = url => { requested = url; return Task.FromResult(LiveSample); };
        try
        {
            // 不传 versionOverride：走的就是生产默认的线上取值。
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true);

            Assert.NotNull(info);
            Assert.NotNull(requested);
            string sent = QueryValue(requested!, "version");
            Assert.Equal(InformationalVersionOfMechrevoAssembly(), sent);
            Assert.Matches(@"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$", sent);
            if (sent.Contains('-'))
            {
                Assert.NotEqual(Program.ReleaseLabel, sent);
                Assert.DoesNotContain("version=" + Program.ReleaseLabel + "&", requested!);
            }
        }
        finally
        {
            UpdateChecker.HttpGetOverride = null;
        }
    }

    static string InformationalVersionOfMechrevoAssembly() =>
        (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(Program).Assembly.GetName().Version?.ToString()
         ?? "unknown").Split('+')[0];

    static string QueryValue(string url, string key)
    {
        int start = url.IndexOf('?');
        Assert.True(start >= 0, "URL 里没有查询串：" + url);
        foreach (string pair in url[(start + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key) return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        throw new Xunit.Sdk.XunitException($"URL 里没有参数 {key}：{url}");
    }

    /// <summary>
    /// 交叉校验：服务端 update_available=true，但按它的比较规则最新版并不比当前版新
    /// （版本被改回旧的、或两边版本串不同源）时，按无更新处理，不让用户被空更新骚扰。
    /// </summary>
    [Theory]
    [InlineData("beta13", "beta13")]   // 服务端把当前版本又标成最新
    [InlineData("beta14", "beta13")]   // 服务端数据回滚
    public async Task AnImplausibleUpdateIsSuppressedInsteadOfNagingTheUser(string current, string latest)
    {
        string json =
            "{\"ok\":true,\"data\":{\"current_version\":\"" + current + "\",\"latest_version\":\"" + latest +
            "\",\"channel\":\"beta\",\"update_available\":true,\"is_latest\":false}}";
        UpdateChecker.HttpGetOverride = _ => Task.FromResult(json);
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: current);

            Assert.NotNull(info);
            Assert.False(info!.UpdateAvailable);
        }
        finally { UpdateChecker.HttpGetOverride = null; }
    }

    [Fact]
    public void ParseResponse_reads_force_flag()
    {
        UpdateInfo? info = UpdateChecker.ParseResponse(
            """{"ok":true,"data":{"latest_version":"0.290.0-beta1","update_available":true,"force":true}}""");
        Assert.NotNull(info);
        Assert.True(info!.Force);
        Assert.True(info.UpdateAvailable);
        Assert.False(UpdateChecker.ParseResponse(
            """{"ok":true,"data":{"latest_version":"0.290.0-beta1","update_available":true}}""")!.Force);
    }

    [Fact]
    public async Task APlausibleUpdateIsKept()
    {
        const string newer = """
        {"ok":true,"data":{"current_version":"beta13","latest_version":"beta14","channel":"beta",
        "update_available":true,"is_latest":false}}
        """;
        UpdateChecker.HttpGetOverride = _ => Task.FromResult(newer);
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: "beta13");

            Assert.NotNull(info);
            Assert.True(info!.UpdateAvailable);
        }
        finally { UpdateChecker.HttpGetOverride = null; }
    }

    [Fact]
    public async Task NetworkFailureIsSilentAndReturnsNull()
    {
        UpdateChecker.HttpGetOverride = _ => throw new HttpRequestException("boom");
        try
        {
            Assert.Null(await UpdateChecker.CheckAsync(force: true));
        }
        finally
        {
            UpdateChecker.HttpGetOverride = null;
        }
    }
}

/// <summary>
/// 包校验与安装准备。校验能力受服务端现状限制（网盘发布不给 sha256），
/// 所以这里钉死"能验的必须验、验不了的不许假装通过"。
/// </summary>
public class UpdatePackageTests
{
    static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lmechrevo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string CreatePackage(string directory, string entryName, byte[] content, bool includeExe = true)
    {
        string zipPath = Path.Combine(directory, "package.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using Stream stream = entry.Open();
        stream.Write(content);
        if (!includeExe) return zipPath;
        return zipPath;
    }

    static string CreateSignedPackage(string directory)
    {
        string zipPath = Path.Combine(directory, "package.zip");
        string signed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using Stream stream = archive.CreateEntry("L-Mechrevo.exe").Open();
        using FileStream source = File.OpenRead(signed);
        source.CopyTo(stream);
        return zipPath;
    }

    static UpdateInfo Info(string? sha256 = null, long? size = null, string? fileName = null) =>
        new("beta13", "beta14", "beta", false, true, "2026-09-01", "notes",
            fileName, size, sha256, "https://stats.l-mechrevo.cn/pkg.zip", "https://stats.l-mechrevo.cn/download.html");

    [Fact]
    public void Sha256MatchesTheKnownVector()
    {
        string dir = NewTempDirectory();
        try
        {
            string file = Path.Combine(dir, "abc.bin");
            File.WriteAllText(file, "abc");
            Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                UpdateInstaller.ComputeSha256(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AValidPackageWithMatchingHashPasses()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreateSignedPackage(dir);
            string hash = UpdateInstaller.ComputeSha256(zip);

            PackageVerification result = UpdateInstaller.Verify(zip, Info(sha256: hash, size: new FileInfo(zip).Length));

            Assert.True(result.Ok, result.Reason);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AHashMismatchIsRejected()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreatePackage(dir, "L-Mechrevo.exe", [1, 2, 3, 4]);
            PackageVerification result = UpdateInstaller.Verify(
                zip, Info(sha256: new string('0', 64), size: new FileInfo(zip).Length));

            Assert.False(result.Ok);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>没有 sha256 时必须直接拒绝（fail-closed），不能"仅结构校验"就放行。</summary>
    [Fact]
    public void WithoutAHashThePackageIsRefused()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreatePackage(dir, "L-Mechrevo.exe", [1, 2, 3, 4]);
            PackageVerification result = UpdateInstaller.Verify(zip, Info());

            Assert.False(result.Ok);
            Assert.Contains("SHA-256", result.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ADeclaredSizeMismatchIsRejected()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreatePackage(dir, "L-Mechrevo.exe", [1, 2, 3, 4]);
            PackageVerification result = UpdateInstaller.Verify(zip, Info(sha256: UpdateInstaller.ComputeSha256(zip), size: 1));

            Assert.False(result.Ok);
            Assert.Contains("大小", result.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void APackageWithoutTheExecutableIsRejected()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreatePackage(dir, "readme.txt", [1, 2, 3]);
            PackageVerification result = UpdateInstaller.Verify(
                zip, Info(sha256: UpdateInstaller.ComputeSha256(zip), size: new FileInfo(zip).Length));

            Assert.False(result.Ok);
            Assert.Contains(".exe", result.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANonZipFileIsRejected()
    {
        string dir = NewTempDirectory();
        try
        {
            string file = Path.Combine(dir, "not-a-zip.zip");
            File.WriteAllText(file, "hello");
            PackageVerification result = UpdateInstaller.Verify(
                file, Info(sha256: UpdateInstaller.ComputeSha256(file), size: new FileInfo(file).Length));

            Assert.False(result.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// 服务端给的 filename 不可信：必须只取最后一段，不能让路径穿越到别的目录。
    /// 没给文件名时按安装器 exe 命名（beta18 起默认包体是 setup.exe，不是 zip）。
    /// </summary>
    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.exe", "evil.exe")]
    [InlineData("sub/dir/L-Mechrevo-beta14.zip", "L-Mechrevo-beta14.zip")]
    [InlineData(null, "L-Mechrevo-beta14.exe")]
    public void PackageFileNameIgnoresAnyPathFromTheServer(string? serverName, string expected)
    {
        Assert.Equal(expected, UpdateInstaller.PackageFileName(Info(fileName: serverName)));
    }

    /// <summary>
    /// 自更新只在单文件发布下成立（更新器只复制 exe）。框架依赖构建里 exe 只是个 apphost，
    /// 旁边还有同名 dll——真机实测这种情况下更新器会因为缺 dll 静默死掉，用户看到"点了没反应"，
    /// 所以必须提前拒绝并引导到下载页。
    /// </summary>
    [Fact]
    public void SelfInstallIsRefusedForFrameworkDependentBuilds()
    {
        string exePath = @"C:\app\L-Mechrevo.exe";

        Assert.False(UpdateInstaller.CanSelfInstall(exePath, path => path == @"C:\app\L-Mechrevo.dll", out string reason));
        Assert.Contains("下载页", reason);

        Assert.True(UpdateInstaller.CanSelfInstall(exePath, _ => false, out string okReason));
        Assert.Equal("", okReason);
    }

    [Fact]
    public void ExtractFindsTheExecutableInsideThePackage()
    {
        string dir = NewTempDirectory();
        try
        {
            string zip = CreatePackage(dir, "L-Mechrevo-beta14.exe", [9, 9, 9]);
            string? exe = UpdateInstaller.ExtractPackage(zip, Info());

            Assert.NotNull(exe);
            Assert.True(File.Exists(exe));
            Assert.Equal("L-Mechrevo-beta14.exe", Path.GetFileName(exe));
        }
        finally { Directory.Delete(dir, true); }
    }
}
