using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechrevoLite.Usage;

namespace MechrevoLite.Tests;

public class TelemetryLogDeliveryTests
{
    static TelemetryLogBatch Batch(string log = "测试错误😀", int errors = 1) =>
        new("testinstallation", "0.290.6", Guid.NewGuid().ToString("N"), log, 0, errors);

    [Fact]
    public void PendingBatchSurvivesRestartAndOnlyTheAcknowledgedFileIsRemoved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LMechrevo-outbox", Guid.NewGuid().ToString("N"));
        var outbox = new TelemetryLogOutbox(directory);
        TelemetryLogBatch first = Batch();
        string path = outbox.Enqueue(first);
        var restarted = new TelemetryLogOutbox(directory);
        Assert.Equal(first, restarted.Peek()!.Value.Batch);
        TelemetryLogBatch second = Batch("later failure", 2);
        restarted.Enqueue(second);
        restarted.Acknowledge(path);
        Assert.Equal(second, restarted.Peek()!.Value.Batch);
        restarted.Clear();
        Assert.Null(restarted.Peek());
    }

    [Fact]
    public void BacklogIsBoundedAndNewestEvidenceIsRetained()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LMechrevo-outbox", Guid.NewGuid().ToString("N"));
        var outbox = new TelemetryLogOutbox(directory);
        for (int i = 0; i < TelemetryLogOutbox.MaxBatches + 3; i++) outbox.Enqueue(Batch($"failure {i}"));
        Assert.Equal(TelemetryLogOutbox.MaxBatches, Directory.GetFiles(directory, "*.json").Length);
        Assert.Equal("failure 3", outbox.Peek()!.Value.Batch.Log);
        Assert.Contains(Directory.GetFiles(directory), p => File.ReadAllText(p).Contains("failure 34"));
        outbox.Clear();
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("{\"Id\":null,\"Batch\":null,\"Log\":null}")]
    public void CorruptBatchDoesNotBlockLaterLogs(string corrupt)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LMechrevo-outbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "000.json"), corrupt);
        var outbox = new TelemetryLogOutbox(directory);
        TelemetryLogBatch batch = Batch();
        outbox.Enqueue(batch);
        Assert.Equal(batch, outbox.Peek()!.Value.Batch);
        Assert.True(File.Exists(Path.Combine(directory, "000.json.invalid")));
        outbox.Clear();
    }

    [Theory]
    [InlineData(500, "valid", false)]
    [InlineData(200, "html", false)]
    [InlineData(200, "wrong-batch", false)]
    [InlineData(200, "wrong-hash", false)]
    [InlineData(200, "wrong-size", false)]
    [InlineData(200, "valid", true)]
    public async Task HttpDeliveryRequiresTheExactServerReceipt(int status, string receiptKind, bool expected)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        string url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);
        listener.Start();
        TelemetryLogBatch batch = Batch();
        Task server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            using JsonDocument request = JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.Equal(batch.Batch, request.RootElement.GetProperty("batch").GetString());
            Assert.Equal(batch.Log, request.RootElement.GetProperty("log").GetString());
            string reply = receiptKind == "html" ? "<html>not a receipt</html>" : JsonSerializer.Serialize(new
            {
                ok = true,
                batch = receiptKind == "wrong-batch" ? "other" : batch.Batch,
                bytes = Encoding.UTF8.GetByteCount(batch.Log) + (receiptKind == "wrong-size" ? 1 : 0),
                sha256 = receiptKind == "wrong-hash" ? "bad" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batch.Log)))
            });
            context.Response.StatusCode = status;
            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        using var client = new HttpClient();
        Assert.Equal(expected, await UsageTelemetry.SendLogAsync(batch, url, client, CancellationToken.None));
        await server;
    }

    [Fact]
    public void ErrorEvidenceSurvivesHighVolumeInformationalLogs()
    {
        string marker = "delivery failure " + Guid.NewGuid().ToString("N");
        Logger.WriteError(marker);
        for (int i = 0; i < 1000; i++) Logger.WriteInfo(new string('x', 100));
        Assert.DoesNotContain(marker, Logger.SnapshotRingBuffer());
        Assert.Contains(marker, Logger.SnapshotErrorBuffer());
    }

    [Fact]
    public async Task RealErrorEventPersistsWhileOfflineAndRetriesTheSameBatchBeforeDeletingIt()
    {
        string[] keys = [UsageTelemetry.EnabledKey, UsageTelemetry.NoticeVersionKey, UsageTelemetry.BaseUrlKey];
        var saved = keys.ToDictionary(k => k, k => AppConfig.GetString(k));
        var previousClient = UsageTelemetry.LogClientOverride;
        TimeSpan previousDebounce = UsageTelemetry.LogDebounce;
        bool previousAudit = Program.UiAuditMode;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        string url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);
        listener.Start();
        using var client = new HttpClient();
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            string? firstBatch = null;
            for (int index = 0; index < 3; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(25));
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                using JsonDocument request = JsonDocument.Parse(await reader.ReadToEndAsync());
                string batch = request.RootElement.GetProperty("batch").GetString()!;
                string log = request.RootElement.GetProperty("log").GetString()!;
                string reply;
                if (index == 0)
                {
                    firstBatch = batch;
                    context.Response.StatusCode = 503;
                    reply = "{\"ok\":false}";
                    arrived.SetResult(batch);
                    await releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    if (index == 1) Assert.Equal(firstBatch, batch);
                    if (index == 2) Assert.Contains("offline-event-evidence", log);
                    reply = JsonSerializer.Serialize(new { ok = true, batch, bytes = Encoding.UTF8.GetByteCount(log),
                        sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log))) });
                }
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply));
                context.Response.Close();
                if (index == 0) failed.SetResult(batch);
            }
        });
        string directory = Path.Combine(Logger.appPath, "telemetry-outbox");
        try
        {
            UsageTelemetry.SetEnabled(false);
            AppConfig.Set(UsageTelemetry.BaseUrlKey, url.TrimEnd('/'));
            AppConfig.Set(UsageTelemetry.NoticeVersionKey, UsageTelemetry.CurrentNoticeVersion);
            Program.UiAuditMode = false;
            UsageTelemetry.LogDebounce = TimeSpan.FromMilliseconds(50);
            UsageTelemetry.LogClientOverride = () => client;
            UsageTelemetry.SetEnabled(true);
            string batch = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(Directory.GetFiles(directory, "*.json"), p => p.Contains(batch));
            Logger.WriteError("offline-event-evidence");
            Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(directory, "*.json").Length >= 2, 3000));
            releaseResponse.SetResult(true);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server;
            Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(directory, "*.json").Length == 0, 3000));
        }
        finally
        {
            releaseResponse.TrySetResult(true);
            UsageTelemetry.SetEnabled(false);
            UsageTelemetry.LogClientOverride = previousClient;
            UsageTelemetry.LogDebounce = previousDebounce;
            Program.UiAuditMode = previousAudit;
            foreach (var pair in saved)
                if (pair.Value is null) AppConfig.Remove(pair.Key); else AppConfig.Set(pair.Key, pair.Value);
        }
    }
}
