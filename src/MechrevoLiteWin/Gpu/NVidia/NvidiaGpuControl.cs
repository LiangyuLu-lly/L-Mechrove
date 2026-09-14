using MechrevoLite.Helpers;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System.Diagnostics;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace MechrevoLite.Gpu.NVidia;

public class NvidiaGpuControl : IGpuControl, IGpuOverclockControl
{

    public static int MinClockLimit = AppConfig.Get("min_gpu_clock", 400);
    public const int MaxClockLimit = 3000;

    private PhysicalGPU? _internalGpu;
    private bool _writeAccessDenied;

    public GpuClockOffsetRange CoreOffset { get; private set; } = new(0, 0, 0);
    public GpuClockOffsetRange MemoryOffset { get; private set; } = new(0, 0, 0);
    public bool IsAvailable => !_writeAccessDenied && IsValid && (CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable);
    public string Name => IsValid ? FullName : "NVIDIA GPU";

    public NvidiaGpuControl()
    {
        _internalGpu = GetInternalDiscreteGpu();
    }

    public static IGpuOverclockControl? TryCreateOverclockControl()
    {
        var control = new NvidiaGpuControl();
        if (control.Refresh()) return control;
        control.Dispose();
        return null;
    }

    internal static bool TryGetActiveDgpuApplications(out IReadOnlyList<DgpuApplication> applications)
    {
        PhysicalGPU? gpu = GetInternalDiscreteGpu();
        if (gpu is null)
        {
            applications = Array.Empty<DgpuApplication>();
            return false;
        }

        try
        {
            // NVAPI 列表里的进程可能在库内部 GetProcessById 之前退出，让整次枚举抛异常。
            // 这是瞬态竞态：稍等重试一次，通常就干净了；两次都失败才按不可用处理。
            Process[] processes;
            int retryAttempt = 0;
            while (true)
            {
                try
                {
                    processes = gpu.GetActiveApplications();
                    break;
                }
                catch (Exception ex)
                {
                    retryAttempt++;
                    if (retryAttempt >= 2) throw;
                    Logger.WriteLine("dGPU application enumeration hit an exiting process, retrying: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
            var result = new List<DgpuApplication>(processes.Length);
            bool identityUnavailable = false;
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        DateTime startedAtUtc;
                        try { startedAtUtc = process.StartTime.ToUniversalTime(); }
                        catch (Exception ex)
                        {
                            Logger.WriteLine("dGPU application enumeration blocked because its start time is unavailable: " + ex.Message);
                            identityUnavailable = true;
                            continue;
                        }
                        result.Add(new DgpuApplication(
                            process.Id,
                            process.ProcessName,
                            process.SessionId,
                            startedAtUtc,
                            process.MainWindowHandle != IntPtr.Zero));
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine("dGPU application query failed: " + ex.Message);
                    }
                }
            }

            MergeComputeApplications(result);

            if (identityUnavailable)
            {
                applications = Array.Empty<DgpuApplication>();
                return false;
            }
            applications = result;
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("dGPU application enumeration unavailable: " + ex.Message);
            applications = Array.Empty<DgpuApplication>();
            return false;
        }
    }

    public bool IsValid => _internalGpu != null;
    public bool IsNvidia => IsValid;

    /// <summary>
    /// 把 nvidia-smi 报告的计算/CUDA 上下文并入占用清单。NVAPI 只列图形应用，
    /// 纯计算上下文（实测 QQ 在独显挂 CUDA 上下文、NVAPI 列表为空）会漏检，
    /// 而任何一个存活上下文都会阻止 EC 给独显断电。
    /// </summary>
    static void MergeComputeApplications(List<DgpuApplication> result)
    {
        try
        {
            IReadOnlyList<(int Pid, string Name)>? computeApps = NvidiaSmi.QueryComputeApplications();
            if (computeApps is null || computeApps.Count == 0) return;

            var known = new HashSet<int>(result.Select(a => a.ProcessId));
            foreach ((int pid, _) in computeApps)
            {
                if (!known.Add(pid)) continue;
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    result.Add(new DgpuApplication(
                        pid,
                        process.ProcessName,
                        process.SessionId,
                        process.StartTime.ToUniversalTime(),
                        process.MainWindowHandle != IntPtr.Zero));
                }
                catch (Exception ex)
                {
                    Logger.WriteLineThrottled("dgpu-compute-enum",
                        $"Compute app {pid} exited before identity read: {ex.Message}", 5000);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("dgpu-compute-enum", "Compute application query failed: " + ex.Message, 5000);
        }
    }


    public string FullName => _internalGpu!.FullName;

    public int? _lastTemp;
    public int _lastTempTime = 0;

    private static bool verboseLog = false;

    private enum GpuState { Active, Asleep, Off }

    private GpuState _lastState = GpuState.Off;
    private long _lastStateTime = -StateCacheMs;
    private const int StateCacheMs = 500; 

    private GpuState GetGpuState()
    {
        if (!IsValid) return GpuState.Off;
        if (Environment.TickCount64 - _lastStateTime < StateCacheMs) return _lastState;
        try
        {
            var perfState = GPUApi.GetCurrentPerformanceState(_internalGpu!.Handle);
            if (verboseLog) Logger.WriteLine($"GPU: {perfState}");
            _lastState = GpuState.Active;
        }
        catch (Exception ex)
        {
            if (verboseLog) Logger.WriteLine($"GPU: {ex.Message}");
            _lastState = ex.Message == "NVAPI_GPU_NOT_POWERED" ? GpuState.Asleep : GpuState.Off;
        }
        _lastStateTime = Environment.TickCount64;
        return _lastState;
    }

    public int? ReadCurrentTemperature(bool log = false)
    {
        if (!IsValid) return null;

        var thermalSettings = GPUApi.GetThermalSettings(_internalGpu!.Handle);
        if (thermalSettings.Sensors is null) return null;

        IThermalSensor? gpuSensor = thermalSettings.Sensors
            .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);

        if (log || verboseLog) Logger.WriteLine($"GPU Temp: {gpuSensor?.CurrentTemperature}C");
        return gpuSensor?.CurrentTemperature;
    }

    private Task<int?>? _readTask;

    public int? GetCurrentTemperature()
    {
        if (!IsValid) return null;

        var state = GetGpuState();
        if (state == GpuState.Off) return null;

        if ((_readTask?.IsCompleted ?? true) && (state == GpuState.Active || ShouldRefresh()))
        {
            _readTask = Task.Run(() =>
            {
                var temp = ReadCurrentTemperature();
                if (temp is not null)
                {
                    _lastTemp = temp;
                    _lastTempTime = Environment.TickCount;
                }
                return temp;
            });
        }

        _readTask?.Wait(500);

        return _lastTemp;
    }

    private bool ShouldRefresh()
    {
        const int minInterval = 5_000;
        const int maxInterval = 120_000;
        const float deltaMin = 5f;
        const float deltaMax = 20f;

        if (_lastTemp is null) return true;

        var cpuTemp = (float)HardwareControl.GetCPUTemp();
        var delta = _lastTemp.Value - cpuTemp;

        if (delta < deltaMin) return false;

        var t = Math.Clamp((delta - deltaMin) / (deltaMax - deltaMin), 0f, 1f);
        var interval = (int)(maxInterval - t * (maxInterval - minInterval));

        var refresh = Environment.TickCount > _lastTempTime + interval;
        if (verboseLog) Logger.WriteLine($"GPU Temp Refresh Interval: {interval}ms {refresh}");

        return refresh;
    }

    public void Dispose()
    {
        _internalGpu = null;
    }

    private static readonly HashSet<string> _systemProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "svchost", "fontdrvhost", "igfxem", "igfxhk", "igfxext",
        "nvcontainer", "nvdisplay.container", "nvsettings", "nvspcaps64",
        "nvsphelper64", "nvwmi64", "nvcplui", "atieclxx", "atiesrxx",
        "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost",
        "searchhost", "startmenuexperiencehost", "textinputhost",
        "applicationframehost", "systemsettings", "dllhost", "conhost",
        "audiodg", "ctfloader", "spoolsv", "wlanext", "msdtc",
    };

    public void KillGPUApps()
    {
        if (!IsValid) return;
        PhysicalGPU internalGpu = _internalGpu!;

        int currentPid = Process.GetCurrentProcess().Id;

        try
        {
            Process[] processes = internalGpu.GetActiveApplications();
            foreach (Process process in processes)
                try
                {
                    if (process.Id == currentPid) continue;
                    if (process.SessionId == 0) continue;
                    if (_systemProcessNames.Contains(process.ProcessName)) continue;

                    Logger.WriteLine("Kill:" + process.ProcessName);
                    ProcessHelper.KillByProcess(process);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }

        //GeneralApi.RestartDisplayDriver();
    }


    public bool GetClocks(out int core, out int memory)
    {
        PhysicalGPU internalGpu = _internalGpu!;

        //Logger.WriteLine(internalGpu.FullName);
        //Logger.WriteLine(internalGpu.ArchitectInformation.ToString());

        try
        {
            var temp = ReadCurrentTemperature(true); // Force wake up GPU for clock reading

            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(internalGpu.Handle);
            core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
            memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
            Logger.WriteLine($"GET GPU CLOCKS: {core}, {memory}");

            foreach (var delta in states.Voltages[PerformanceStateId.P0_3DPerformance])
            {
                Logger.WriteLine("GPU VOLT:" + delta.IsEditable + " - " + delta.ValueDeltaInMicroVolt.DeltaValue);
            }

            return true;

        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU CLOCKS:" + ex.Message);
            core = memory = 0;
            return false;
        }

    }

    public bool Refresh()
    {
        if (!IsValid) return false;
        try
        {
            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(_internalGpu!.Handle);
            if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out IPerformanceStates20ClockEntry[]? clocks))
                return false;

            CoreOffset = ReadOffsetRange(clocks, PublicClockDomain.Graphics);
            MemoryOffset = ReadOffsetRange(clocks, PublicClockDomain.Memory);
            bool available = !_writeAccessDenied && states.IsEditable && (CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable);
            Logger.WriteLineThrottled("nvidia-oc-capability", $"NVIDIA OC capability: {FullName}, editable={states.IsEditable}, " +
                $"core={CoreOffset.Minimum}..{CoreOffset.Maximum} current={CoreOffset.Current}, " +
                $"memory={MemoryOffset.Minimum}..{MemoryOffset.Maximum} current={MemoryOffset.Current}", 10000);
            return available;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("NVIDIA OC capability unavailable: " + ex.Message);
            CoreOffset = MemoryOffset = new GpuClockOffsetRange(0, 0, 0);
            return false;
        }
    }

    static GpuClockOffsetRange ReadOffsetRange(
        IEnumerable<IPerformanceStates20ClockEntry> clocks,
        PublicClockDomain domain)
    {
        IPerformanceStates20ClockEntry? clock = clocks.FirstOrDefault(item => item.DomainId == domain && item.IsEditable);
        if (clock is null) return new GpuClockOffsetRange(0, 0, 0);

        PerformanceStates20ParameterDelta delta = clock.FrequencyDeltaInkHz;
        int minimum = (int)Math.Ceiling(delta.DeltaRange.Minimum / 1000d);
        int maximum = (int)Math.Floor(delta.DeltaRange.Maximum / 1000d);
        int current = (int)Math.Round(delta.DeltaValue / 1000d, MidpointRounding.AwayFromZero);
        return new GpuClockOffsetRange(minimum, maximum, current);
    }

    public bool SetCoreOffset(int value) => SetClockOffset(PublicClockDomain.Graphics, value);

    public bool SetMemoryOffset(int value) => SetClockOffset(PublicClockDomain.Memory, value);

    bool SetClockOffset(PublicClockDomain domain, int value)
    {
        if (!Refresh()) return false;
        GpuClockOffsetRange range = domain == PublicClockDomain.Graphics ? CoreOffset : MemoryOffset;
        if (!range.Contains(value))
        {
            Logger.WriteLine($"NVIDIA OC rejected outside driver range: domain={domain}, value={value}, range={range.Minimum}..{range.Maximum}");
            return false;
        }
        if (range.Current == value) return true;

        try
        {
            var clock = new PerformanceStates20ClockEntryV1(domain, new PerformanceStates20ParameterDelta(value * 1000));
            PerformanceState20[] performanceStates =
            {
                new(PerformanceStateId.P0_3DPerformance, new[] { clock }, Array.Empty<PerformanceStates20BaseVoltageEntryV1>())
            };
            var overclock = new PerformanceStates20InfoV1(performanceStates, 1, 0);
            GPUApi.SetPerformanceStates20(_internalGpu!.Handle, overclock);
            int actual = range.Current;
            bool confirmed = false;
            int[] retryDelays = [60, 120, 240, 400];
            foreach (int delay in retryDelays)
            {
                Thread.Sleep(delay);
                bool refreshed = Refresh();
                actual = domain == PublicClockDomain.Graphics ? CoreOffset.Current : MemoryOffset.Current;
                if (refreshed && actual == value)
                {
                    confirmed = true;
                    break;
                }
            }
            Logger.WriteLine($"NVIDIA OC set: domain={domain}, requested={value}, actual={actual}, confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("NVAPI_INVALID_USER_PRIVILEGE", StringComparison.OrdinalIgnoreCase))
            {
                _writeAccessDenied = true;
                Logger.WriteLine("NVIDIA OC write access denied; disabling the direct backend for this session.");
            }
            Logger.WriteLine($"NVIDIA OC set failed: domain={domain}, value={value}, error={ex.Message}");
            return false;
        }
    }


    private static bool RunPowershellCommand(string script, int timeoutMs = 0)
    {
        try
        {
            ProcessHelper.RunCMD("powershell", script, timeoutMs: timeoutMs);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.ToString());
            return false;
        }

    }

    public int GetMaxGPUCLock()
    {
        PhysicalGPU internalGpu = _internalGpu!;
        try
        {
            PrivateClockBoostLockV2 data = GPUApi.GetClockBoostLock(internalGpu.Handle);
            int limit = (int)data.ClockBoostLocks[0].VoltageInMicroV / 1000;
            Logger.WriteLine("GET CLOCK LIMIT: " + limit);
            return limit;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET CLOCK LIMIT: " + ex.Message);
            return -1;

        }
    }


    public int SetMaxGPUClock(int clock)
    {

        if (clock < MinClockLimit || clock >= MaxClockLimit) clock = 0;

        int _clockLimit = GetMaxGPUCLock();

        if (_clockLimit == clock) return 0;

        if (clock > 0) RunPowershellCommand($"nvidia-smi -lgc 0,{clock}");
        else RunPowershellCommand($"nvidia-smi -rgc");
        return 1;


    }

    public static void RestartNvContainer()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void RestartNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void StopNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Stop-Service -Name 'NvContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Stop-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
    }

    public int SetClocks(int core, int memory)
    {
        if (!Refresh()) return -1;
        if (!CoreOffset.Contains(core) || !MemoryOffset.Contains(memory)) return 0;
        bool coreChanged = CoreOffset.Current != core;
        bool memoryChanged = MemoryOffset.Current != memory;
        if (!coreChanged && !memoryChanged) return 0;
        if (coreChanged && !SetCoreOffset(core)) return -1;
        if (memoryChanged && !SetMemoryOffset(memory)) return -1;
        return 1;
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
            return gpus.FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop) ?? gpus.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }


    public int? GetGpuUse()
    {
        if (!IsValid) return null;
         if (GetGpuState() != GpuState.Active) return null;

        PhysicalGPU internalGpu = _internalGpu!;
        IUtilizationDomainInfo? gpuUsage = GPUApi.GetUsages(internalGpu.Handle).GPU;

        return (int?)gpuUsage?.Percentage;

    }


    public float? GetGpuPower()
    {
        if (!IsValid) return null;
        var state = GetGpuState();
        if (state == GpuState.Off)
        {
            NvmlHelper.Shutdown();
            return null;
        }
        if (state != GpuState.Active) return 0f;
        return NvmlHelper.GetGpuPower() ?? 0f;
    }

    public (long usedMb, long totalMb)? GetVramInfo()
    {
        if (!IsValid) return null;
        if (GetGpuState() != GpuState.Active) return null;
        return NvmlHelper.GetMemoryInfo();
    }

}
