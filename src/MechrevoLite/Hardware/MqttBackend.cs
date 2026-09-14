using MQTTnet;
using MQTTnet.Formatter;

namespace MechrevoLite.Hardware;

/// <summary>
/// Phase 1 后端：直连原版 GCUBridge 的本地 MQTT broker。
/// 关键：broker 是 MQTT 3.1.1 协议（V311），MQTTnet 5.x 默认 V5 会被拒绝。
/// 实测：连接 + 订阅后 GCUService 自动推流（约 5s 周期），无需 System_ON。
/// </summary>
public class MqttBackend : IHardwareBackend
{
    const string Host = "localhost";
    const int Port = 13688;
    const string User = "UWPClient_User_5";
    const string Pwd = "UWPClient_Pwd888881772688_5";
    const string ClientId = "UWPClient_5";

    readonly MqttClientFactory _factory = new();
    IMqttClient? _client;
    readonly List<string> _topics = new();
    bool _disposed;
    bool _reconnecting;

    public event Action<bool>? ConnectionChanged;
    public event Action<MqttMessage>? MessageReceived;
    public bool IsConnected => _client?.IsConnected == true;

    public async Task<bool> ConnectAsync()
    {
        _client ??= _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            MessageReceived?.Invoke(new MqttMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += async e =>
        {
            ConnectionChanged?.Invoke(false);
            if (!_disposed) await ReconnectLoopAsync();
        };

        var res = await _client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(Host, Port)
            .WithCredentials(User, Pwd)
            .WithClientId(ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(false)
            .Build());
        var ok = res.ResultCode == MqttClientConnectResultCode.Success;
        if (ok)
        {
            ConnectionChanged?.Invoke(true);
            if (_topics.Count > 0) await SubscribeAsync(_topics);
        }
        return ok;
    }

    async Task ReconnectLoopAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            while (!IsConnected && !_disposed)
            {
                await Task.Delay(5000);
                try
                {
                    var ok = await ConnectAsync();
                    if (ok) return;
                }
                catch { /* 下轮重试 */ }
            }
        }
        finally { _reconnecting = false; }
    }

    public async Task PublishAsync(string topic, object payload, bool retain = false)
    {
        if (_client is null || !IsConnected) throw new InvalidOperationException("MQTT 未连接");
        await _client.PublishStringAsync(topic, Newtonsoft.Json.JsonConvert.SerializeObject(payload),
            MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, retain);
    }

    public async Task SubscribeAsync(IEnumerable<string> topics)
    {
        var list = topics.Where(t => !_topics.Contains(t)).ToList();
        _topics.AddRange(list);
        if (_client is null || !IsConnected) return;
        foreach (var t in list)
            await _client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
    }
}
