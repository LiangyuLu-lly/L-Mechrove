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
    bool Refresh();
    bool SetCoreOffset(int value);
    bool SetMemoryOffset(int value);
}
