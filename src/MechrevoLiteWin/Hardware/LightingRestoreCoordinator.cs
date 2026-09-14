namespace MechrevoLite.Hardware;

internal enum LightingRestoreDecision
{
    Run,
    SkipCompleted,
    DeferInFlight,
}

/// <summary>串联启动与 GCU 重连恢复，避免同一连接代次重复写入灯效。</summary>
internal sealed class LightingRestoreCoordinator
{
    readonly object _sync = new();
    int _completedGeneration = -1;
    int _inFlightGeneration = -1;
    bool _inFlight;

    internal LightingRestoreDecision TryBegin(int generation, bool force)
    {
        lock (_sync)
        {
            if (_inFlight) return LightingRestoreDecision.DeferInFlight;
            if (!force && generation <= _completedGeneration)
                return LightingRestoreDecision.SkipCompleted;

            _inFlight = true;
            _inFlightGeneration = generation;
            return LightingRestoreDecision.Run;
        }
    }

    internal void Complete(int generation, bool success)
    {
        lock (_sync)
        {
            if (!_inFlight || _inFlightGeneration != generation) return;
            _inFlight = false;
            _inFlightGeneration = -1;
            if (success) _completedGeneration = Math.Max(_completedGeneration, generation);
        }
    }
}
