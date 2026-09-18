using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// 进程内 MQTT 传输假件：不连 broker、不开 socket、不起线程。
///
/// T17/T18/T19 的单元 TDD 一律走它（真实 broker 集成是 <c>LMECHREVO_RUN_INTEGRATION=1</c>
/// 显式开启的独立 QA 步骤，跳过记 BLOCKED-HW）。假件只记录连接/订阅调用并把帧推给订阅方，
/// 不做协议判断——协议判断属于被测层。
/// </summary>
public sealed class FakeMqttTransport : IMqttTransport
{
    /// <summary>false 时 <see cref="ConnectAsync"/> 返回 false（模拟 broker 未就绪）。</summary>
    public bool ConnectSucceeds { get; set; } = true;

    public List<string> ConnectCalls { get; } = new();

    public List<IReadOnlyCollection<string>> Subscriptions { get; } = new();

    public bool Disposed { get; private set; }

    public event Action<string, string>? MessageReceived;

    public event Action<string>? Disconnected;

    public Task<bool> ConnectAsync(string clientId)
    {
        ConnectCalls.Add(clientId);
        return Task.FromResult(ConnectSucceeds);
    }

    public Task SubscribeAsync(IReadOnlyCollection<string> topics)
    {
        Subscriptions.Add(topics);
        return Task.CompletedTask;
    }

    /// <summary>模拟 broker 推一帧。</summary>
    public void Deliver(string topic, string payload) => MessageReceived?.Invoke(topic, payload);

    /// <summary>模拟断线。</summary>
    public void Drop(string reason) => Disconnected?.Invoke(reason);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
