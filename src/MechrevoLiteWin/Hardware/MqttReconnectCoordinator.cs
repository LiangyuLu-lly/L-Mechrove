namespace MechrevoLite.Hardware;

/// <summary>
/// Serializes MQTT reconnect so a disconnect during an in-flight loop is never dropped.
/// No MQTTnet types — session-ready is supplied by the caller.
/// </summary>
internal sealed class MqttReconnectCoordinator
{
    int _inLoop;
    int _pendingGeneration;
    int _lastHandledGeneration;

    internal void MarkDisconnectRequested() => Interlocked.Increment(ref _pendingGeneration);

    internal bool TryEnterLoop()
    {
        if (Interlocked.CompareExchange(ref _inLoop, 1, 0) != 0) return false;
        Volatile.Write(ref _lastHandledGeneration, Volatile.Read(ref _pendingGeneration));
        return true;
    }

    /// <returns>True when a disconnect arrived while the loop was running and must be retried.</returns>
    internal bool ExitLoop()
    {
        Interlocked.Exchange(ref _inLoop, 0);
        return Volatile.Read(ref _pendingGeneration) > Volatile.Read(ref _lastHandledGeneration);
    }

    internal bool ShouldContinue(bool sessionReady, bool disposed) => !disposed && !sessionReady;
}
