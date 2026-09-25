using MechrevoLite.Update;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MechrevoLite.Tests;

/// <summary>
/// 更新 HTTP 不得跟随重定向：3xx 是硬失败。host 白名单只做请求前检查，
/// 不得靠跟 302 落到未审主机，也不得为 GitHub 302 特开新 host。
/// 回环 HTTP 无重定向的桩服务器仍可下载。
/// </summary>
public class UpdateRedirectFailClosedTests
{
    const string ValidSha = "0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef";

    [Fact]
    public async Task DownloadRefuses302ToEvilHostAndWritesNoBytes()
    {
        byte[] evilPayload = Encoding.ASCII.GetBytes("EVIL-PAYLOAD-MUST-NOT-BE-WRITTEN");
        var evilHits = new ConcurrentQueue<string>();
        (HttpListener evil, string evilUrl) = StartLoopbackListener();
        (HttpListener origin, string originUrl) = StartLoopbackListener();
        using var stop = new CancellationTokenSource();
        Task evilTask = Task.Run(() => ServeBytes(evil, evilPayload, evilHits, stop.Token));
        Task originTask = Task.Run(() => ServeRedirect(origin, HttpStatusCode.Found, evilUrl + "/malware.zip", stop.Token));

        string sandbox = NewSandbox();
        string isolatedTemp = Path.Combine(sandbox, "sys-temp");
        Directory.CreateDirectory(isolatedTemp);
        string? oldTmp = Environment.GetEnvironmentVariable("TMP");
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            Environment.SetEnvironmentVariable("TMP", isolatedTemp);
            Environment.SetEnvironmentVariable("TEMP", isolatedTemp);

            UpdateInfo info = Offer(originUrl + "/pkg.zip", ValidSha, evilPayload.Length);
            DownloadResult result = await UpdateInstaller.DownloadAsync(info);

            Assert.Null(result.Path);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.Contains("302", result.Reason);
            Assert.Contains("重定向", result.Reason);
            Assert.True(evilHits.IsEmpty, "不得跟随 Location 去 evil host");
            AssertNoPackageBytes(isolatedTemp);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            await StopAsync(stop, origin, originTask, evil, evilTask);
            TryDelete(sandbox);
        }
    }

    [Fact]
    public async Task CheckRefuses302ToEvilHost()
    {
        const string evilJson =
            """{"ok":true,"data":{"current_version":"0.289.0-beta17","latest_version":"0.289.0-beta18","update_available":true,"channel":"beta"}}""";
        var evilHits = new ConcurrentQueue<string>();
        (HttpListener evil, string evilUrl) = StartLoopbackListener();
        (HttpListener origin, string originUrl) = StartLoopbackListener();
        using var stop = new CancellationTokenSource();
        Task evilTask = Task.Run(() => ServeBytes(evil, Encoding.UTF8.GetBytes(evilJson), evilHits, stop.Token, "application/json"));
        Task originTask = Task.Run(() => ServeRedirect(origin, HttpStatusCode.Found, evilUrl + "/api/update_check.php", stop.Token));

        UpdateChecker.HttpGetOverride = null;
        UpdateChecker.BaseUrlOverride = originUrl;
        try
        {
            UpdateInfo? info = await UpdateChecker.CheckAsync(force: true, versionOverride: "0.289.0-beta17");
            Assert.Null(info);
            Assert.True(evilHits.IsEmpty, "检测不得跟随 Location 去 evil host");
        }
        finally
        {
            UpdateChecker.BaseUrlOverride = null;
            await StopAsync(stop, origin, originTask, evil, evilTask);
        }
    }

    [Fact]
    public async Task Loopback200WithValidShaStillDownloads()
    {
        byte[] payload = Encoding.ASCII.GetBytes("loopback-ok-payload");
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

            UpdateInfo info = Offer(baseUrl + "/pkg.zip", sha, payload.Length);
            DownloadResult result = await UpdateInstaller.DownloadAsync(info);

            Assert.NotNull(result.Path);
            Assert.True(File.Exists(result.Path), "回环 200 应写出包体：" + result.Path);
            Assert.Equal(payload, File.ReadAllBytes(result.Path!));
            Assert.Contains(hits, h => h.Contains("/pkg.zip"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            stop.Cancel();
            listener.Stop();
            listener.Close();
            await server;
            TryDelete(sandbox);
        }
    }

    static UpdateInfo Offer(string url, string sha, long size) =>
        new("0.289.0-beta17", "0.289.0-beta18", "beta", false, true, null, null, "pkg.zip", size, sha, url, null);

    static void AssertNoPackageBytes(string isolatedTemp)
    {
        if (!Directory.Exists(isolatedTemp)) return;
        foreach (string file in Directory.GetFiles(isolatedTemp, "*", SearchOption.AllDirectories))
            Assert.True(new FileInfo(file).Length == 0, "3xx 不得写出包体：" + file);
    }

    static void ServeRedirect(HttpListener listener, HttpStatusCode status, string location, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            context.Response.StatusCode = (int)status;
            context.Response.RedirectLocation = location;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Close();
        }
    }

    static void ServeBytes(HttpListener listener, byte[] body, ConcurrentQueue<string> hits, CancellationToken token,
        string contentType = "application/octet-stream")
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
            context.Response.ContentType = contentType;
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

    static async Task StopAsync(CancellationTokenSource stop, HttpListener origin, Task originTask,
        HttpListener evil, Task evilTask)
    {
        stop.Cancel();
        origin.Stop();
        origin.Close();
        evil.Stop();
        evil.Close();
        await originTask;
        await evilTask;
    }

    static string NewSandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lmechrevo-redir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
}
