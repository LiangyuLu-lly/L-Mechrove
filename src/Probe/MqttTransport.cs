using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Probe;

/// <summary>
/// MQTT 生命周期接缝：只覆盖「连接 + 订阅」，以及订阅之后的收帧/断线通知。
///
/// 发布**不在**接缝内——产品侧唯一的发布路径是 <c>MechrevoHw._publishOverride</c>（既有注入点），
/// 探针 CLI 的诊断发布沿用 <c>MqttProbe.Publish</c>（同一实现，不新增第二套）。
/// 真正的 broker 集成测试受 <c>LMECHREVO_RUN_INTEGRATION=1</c> 门控；
/// 单元 TDD 一律走进程内假件（无 broker、无网络）。
/// </summary>
public interface IMqttTransport : IAsyncDisposable
{
    /// <summary>连接；结果同 CONNACK（false = 未就绪，调用方按 fail-closed 处理）。</summary>
    Task<bool> ConnectAsync(string clientId);

    /// <summary>一次订阅多个主题过滤器；必须在发任何 GETSTATUS 之前 await 完（SUBACK 落地才算订阅生效）。</summary>
    Task SubscribeAsync(IReadOnlyCollection<string> topics);

    /// <summary>收到一帧：topic 与 payload（UTF-8 文本）。</summary>
    event Action<string, string>? MessageReceived;

    /// <summary>连接断开（原因文本）。</summary>
    event Action<string>? Disconnected;
}

/// <summary>一次「连接 + 订阅 + 收帧」计划；只描述意图，不携带任何 MQTTnet 类型。</summary>
public sealed record MqttCollectPlan(string ClientId, IReadOnlyCollection<string> Topics, int Seconds);

/// <summary>
/// 生产实现：唯一构造 <see cref="MqttClientOptions"/> 的地方。
/// 改造前 <c>MqttProbe</c> 持有 host/port/凭据与工厂、<c>GoldenCapture</c> 与 <c>EcSnapshotReport</c>
/// 各自建 client，三份形状会漂；现在只有这里知道连接参数。
/// </summary>
public sealed class MqttNetTransport : IMqttTransport
{
    public const string Host = "localhost";
    public const int Port = 13688;
    const string User = "UWPClient_User_5";
    const string Pwd = "UWPClient_Pwd888881772688_5";

    readonly MqttClientFactory _factory = new();
    IMqttClient? _client;

    /// <summary>唯一的 options 构造点（连接参数集中在此；测试可无网络断言形状）。</summary>
    public static MqttClientOptions BuildOptions(string clientId) => new MqttClientOptionsBuilder()
        .WithTcpServer(Host, Port)
        .WithCredentials(User, Pwd)
        .WithClientId(clientId)
        .WithProtocolVersion(MqttProtocolVersion.V311)
        .WithCleanSession(false)
        .Build();

    /// <summary>探针诊断发布所需的客户端句柄；发布路径不经过接缝，只有 <c>MqttProbe.Publish</c> 用它。</summary>
    public IMqttClient? Client => _client;

    public event Action<string, string>? MessageReceived;
    public event Action<string>? Disconnected;

    public async Task<bool> ConnectAsync(string clientId)
    {
        if (_client is null)
        {
            _client = _factory.CreateMqttClient();
            // 事件只在首次创建时挂一次：重连时重复挂会让同一帧被处理多次。
            _client.ApplicationMessageReceivedAsync += e =>
            {
                MessageReceived?.Invoke(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString());
                return Task.CompletedTask;
            };
            _client.DisconnectedAsync += e =>
            {
                Disconnected?.Invoke(e.Reason.ToString());
                return Task.CompletedTask;
            };
        }
        MqttClientConnectResult result = await _client.ConnectAsync(BuildOptions(clientId));
        Console.WriteLine($"[{clientId}] Connect: {result.ResultCode} Reason={result.ReasonString}");
        return result.ResultCode == MqttClientConnectResultCode.Success;
    }

    public async Task SubscribeAsync(IReadOnlyCollection<string> topics)
    {
        IMqttClient client = _client ?? throw new InvalidOperationException("ConnectAsync must run before SubscribeAsync");
        foreach (string topic in topics)
        {
            await client.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build());
        }
        Console.WriteLine($"subscribed {string.Join(",", topics)}");
    }

    public async ValueTask DisposeAsync()
    {
        IMqttClient? client = _client;
        _client = null;
        if (client is null) return;
        await client.DisconnectAsync();
        client.Dispose();
    }
}
