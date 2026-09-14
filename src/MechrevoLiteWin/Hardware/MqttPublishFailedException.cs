namespace MechrevoLite.Hardware;

/// <summary>
/// 向 GCU 发布命令失败。
///
/// 存在的理由：<see cref="MechrevoHw.Publish"/> 过去在未连接时抛裸
/// <see cref="InvalidOperationException"/>，而「检查连接」和「实际发布」之间存在
/// TOCTOU 窗口——断线正好落在中间时抛出的是 MQTTnet 自己的异常类型。
/// 调用方（UI 事件处理器、服务层的 bool API）因此无法按类型可靠地区分
/// 「命令没发出去」和「别的程序错误」，只能用 catch-all，容易连带吞掉真正的 bug。
/// </summary>
public sealed class MqttPublishFailedException : Exception
{
    public MqttPublishFailedException(string topic, string message)
        : base($"发布到 {topic} 失败：{message}") => Topic = topic;

    public MqttPublishFailedException(string topic, string message, Exception innerException)
        : base($"发布到 {topic} 失败：{message}", innerException) => Topic = topic;

    /// <summary>发布失败的目标主题，便于日志定位。</summary>
    public string Topic { get; }
}
