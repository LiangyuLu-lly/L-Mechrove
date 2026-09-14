using MechrevoLite.Update;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MechrevoLite.Tests;

/// <summary>
/// 客户端半程端到端：本地 <see cref="HttpListener"/> 桩服务器（只绑 127.0.0.1）模拟服务端，
/// 驱动 CheckAsync → DownloadAsync → Verify → ExtractPackage 的完整链路。
///
/// 桩服务器复刻服务端的版本号校验规则：version 参数必须是点分版本（"0.289.0-beta13"），
/// 裸标签（"beta13"）一律回 ok=false。服务端 2026-09-13 实测就是这个规则让
/// update_available 恒为 false —— 这条测试用真实的网络请求把它钉死。
///
/// 纪律：全程只写测试自己的临时沙箱；包体、下载、解压都在沙箱里，绝不触碰真实安装或用户配置。
/// 设置环境变量 <c>LMECHREVO_UPDATE_TEST_SANDBOX</c> 可指定沙箱目录并保留（仅用于取证据）。
/// </summary>
public class UpdateClientEndToEndTests
{
    const string SandboxVariable = "LMECHREVO_UPDATE_TEST_SANDBOX";

    /// <summary>服务端版本号规则：必须以数字（或 v/V 前缀）开头、只含字母/数字/._-。</summary>
    static readonly Regex ServerVersionFormat = new(@"^[vV]?[0-9][0-9A-Za-z._-]*$", RegexOptions.Compiled);

    [Fact]
    public async Task ClientChecksDownloadsVerifiesAndExtractsAgainstLocalStub()
    {
        string clientVersion = InformationalVersionOfMechrevoAssembly();
        string serverLatest = "0.289.0-beta14";
        string sandbox = ResolveSandbox();
        bool keepSandbox = Environment.GetEnvironmentVariable(SandboxVariable) is { Length: > 0 };
        string requestLogPath = Path.Combine(sandbox, "stub-request-log.txt");
        string extractedListingPath = Path.Combine(sandbox, "extracted-files.txt");
        Directory.CreateDirectory(sandbox);

        // 1) 沙箱里造一个假更新包：L-Mechrevo-test.exe + 更新日志.txt
        string sourcePackage = Path.Combine(sandbox, "source", $"L-Mechrevo-{serverLatest}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePackage)!);
        using (ZipArchive archive = ZipFile.Open(sourcePackage, ZipArchiveMode.Create))
        {
            using (Stream exe = archive.CreateEntry("L-Mechrevo-test.exe").Open())
                exe.Write(Encoding.ASCII.GetBytes("MZ-dummy-mechrevo-update-payload"));
            using (Stream notes = archive.CreateEntry("更新日志.txt").Open())
                notes.Write(Encoding.UTF8.GetBytes($"测试更新包：{serverLatest}"));
        }
        byte[] packageBytes = File.ReadAllBytes(sourcePackage);
        string sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));

        var requests = new ConcurrentQueue<string>();
        (HttpListener listener, string baseUrl) = StartLoopbackListener();
        string manifestJson =
            "{\"ok\":true,\"data\":{\"current_version\":\"" + clientVersion + "\",\"latest_version\":\"" + serverLatest +
            "\",\"channel\":\"beta\",\"channel_fallback\":false,\"is_latest\":false,\"update_available\":true," +
            "\"release_date\":\"2026-09-13\",\"notes\":\"- 测试更新包。\",\"filename\":\"L-Mechrevo-" + serverLatest +
            ".zip\",\"size\":" + packageBytes.Length + ",\"sha256\":\"" + sha256 + "\",\"download_url\":\"" + baseUrl +
            "/pkg/L-Mechrevo-" + serverLatest + ".zip\",\"download_page\":\"" + baseUrl +
            "/download.html\",\"checked_at\":\"2026-09-13T00:00:00+08:00\"}}";

        var serverStop = new CancellationTokenSource();
        Task serverTask = Task.Run(() => ServeStub(listener, manifestJson, packageBytes, requests, serverStop.Token));

        string? oldTmp = Environment.GetEnvironmentVariable("TMP");
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP");
        string isolatedTemp = Path.Combine(sandbox, "sys-temp");
        Directory.CreateDirectory(isolatedTemp);

        UpdateChecker.BaseUrlOverride = baseUrl;
        UpdateChecker.HttpGetOverride = null;
        try
        {
            // 让 UpdateInstaller.TempRoot（Path.GetTempPath()）落在沙箱里。
            Environment.SetEnvironmentVariable("TMP", isolatedTemp);
            Environment.SetEnvironmentVariable("TEMP", isolatedTemp);

            // 桩服务器只放行点分版本；能解析出 info 就证明请求带的是合法版本。
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true);
            Assert.NotNull(info);
            Assert.Equal(serverLatest, info!.LatestVersion);
            Assert.True(info.UpdateAvailable);
            Assert.Equal(sha256, info.Sha256);
            Assert.True(info.HasVerifiablePackage);

            string? downloaded = await UpdateInstaller.DownloadAsync(info);
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded), "更新包应已下载：" + downloaded);

            PackageVerification verification = UpdateInstaller.Verify(downloaded!, info);
            Assert.True(verification.Ok, "校验应通过：" + verification.Reason);
            Assert.Contains("SHA-256", verification.Reason);

            string? extractedExe = UpdateInstaller.ExtractPackage(downloaded!, info);
            Assert.NotNull(extractedExe);
            Assert.True(File.Exists(extractedExe), "应解压出 exe：" + extractedExe);
            Assert.StartsWith(UpdateInstaller.ExpectedExePrefix, Path.GetFileName(extractedExe));

            string extractedDir = Path.GetDirectoryName(extractedExe)!;
            File.WriteAllLines(extractedListingPath, Directory
                .GetFiles(extractedDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(extractedDir, f)));

            Assert.Contains(requests, r => r.Contains("version=" + clientVersion));
        }
        finally
        {
            File.WriteAllLines(requestLogPath, requests);
            UpdateChecker.BaseUrlOverride = null;
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            serverStop.Cancel();
            listener.Stop();
            listener.Close();
            await serverTask;
            serverStop.Dispose();
            if (!keepSandbox) TryDeleteDirectory(sandbox);
        }
    }

    static void ServeStub(HttpListener listener, string manifestJson, byte[] packageBytes,
        ConcurrentQueue<string> requests, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            requests.Enqueue(context.Request.RawUrl ?? "");

            byte[] body;
            string contentType;
            if (context.Request.Url!.AbsolutePath == "/api/update_check.php")
            {
                string? version = context.Request.QueryString["version"];
                bool accepted = version is not null && ServerVersionFormat.IsMatch(version);
                body = accepted
                    ? Encoding.UTF8.GetBytes(manifestJson)
                    : Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"版本号格式不正确（字母/数字/._-，如 9.6.2 或 v9.6-beta）\"}");
                contentType = "application/json; charset=utf-8";
            }
            else
            {
                body = packageBytes;
                contentType = "application/octet-stream";
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }
    }

    /// <summary>只绑 127.0.0.1 的临时端口；拿不到就换一个再试，避免端口被占时测试偶发失败。</summary>
    static (HttpListener Listener, string BaseUrl) StartLoopbackListener()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port = FreeLoopbackPort();
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

    static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    static string ResolveSandbox()
    {
        string? explicitDirectory = Environment.GetEnvironmentVariable(SandboxVariable);
        return string.IsNullOrWhiteSpace(explicitDirectory)
            ? Path.Combine(Path.GetTempPath(), "lmechrevo-update-e2e-" + Guid.NewGuid().ToString("N")[..8])
            : Path.GetFullPath(explicitDirectory);
    }

    static string InformationalVersionOfMechrevoAssembly() =>
        (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(Program).Assembly.GetName().Version?.ToString()
         ?? "unknown").Split('+')[0];

    static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
}
