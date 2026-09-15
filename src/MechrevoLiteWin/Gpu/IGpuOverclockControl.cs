namespace MechrevoLite.Gpu;

public sealed record GpuClockOffsetRange(int Minimum, int Maximum, int Current)
{
    public bool IsAdjustable => Maximum > Minimum;
    public bool Contains(int value) => IsAdjustable && value >= Minimum && value <= Maximum;
}

/// <summary>
/// Driver-level GPU clock-offset control. Implementations must report the
/// device/driver range and verify every write by reading the value back.
/// </summary>
public interface IGpuOverclockControl : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    GpuClockOffsetRange CoreOffset { get; }
    GpuClockOffsetRange MemoryOffset { get; }

    /// <summary>
    /// 该后端写入是否必须提权。默认 false（测试/内存替身直接可写）；真实 NVIDIA 直连
    /// 后端在非提权进程里恒为 true——NVAPI 写入返回 NVAPI_INVALID_USER_PRIVILEGE。
    /// </summary>
    bool WritesRequireElevation => false;

    bool Refresh();
    bool SetCoreOffset(int value);
    bool SetMemoryOffset(int value);
}
