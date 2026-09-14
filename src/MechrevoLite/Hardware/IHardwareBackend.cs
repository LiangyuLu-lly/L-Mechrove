namespace MechrevoLite.Hardware;

public record MqttMessage(string Topic, string Payload);

public interface IHardwareBackend : IDisposable
{
    event Action<bool>? ConnectionChanged;       // true=已连接
    event Action<MqttMessage>? MessageReceived;   // 订阅到的消息（原始 JSON）

    Task<bool> ConnectAsync();                    // 连接 + 注册握手
    Task PublishAsync(string topic, object payload, bool retain = false);
    Task SubscribeAsync(IEnumerable<string> topics);
    bool IsConnected { get; }
}
