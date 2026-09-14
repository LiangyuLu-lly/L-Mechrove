using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

namespace Probe;

// 权威驱动层读回：NVAPI PStates20 的频率偏移 delta（GCU echo 只能证明"收下了"，不证明"写进驱动了"）。
static class NvOcProbe
{
    public static void Read()
    {
        try
        {
            foreach (PhysicalGPU gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                Console.WriteLine($"GPU: {gpu.FullName}");
                IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(gpu.Handle);
                foreach (var pair in states.Clocks)
                {
                    if (pair.Value is not IPerformanceStates20ClockEntry[] clocks) continue;
                    foreach (var c in clocks.Where(c => c.DomainId is PublicClockDomain.Graphics or PublicClockDomain.Memory))
                    {
                        string name = c.DomainId == PublicClockDomain.Graphics ? "core" : "memory";
                        Console.WriteLine(
                            $"[P{pair.Key}] {name} editable={c.IsEditable} " +
                            $"delta={c.FrequencyDeltaInkHz.DeltaValue}kHz " +
                            $"range={c.FrequencyDeltaInkHz.DeltaRange.Minimum}..{c.FrequencyDeltaInkHz.DeltaRange.Maximum}kHz");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("nvoc read failed: " + ex.Message);
        }
        finally
        {
            // NvAPIWrapper 首次调用自动初始化，无显式反初始化可调（与主工程一致）。
        }
    }
}
