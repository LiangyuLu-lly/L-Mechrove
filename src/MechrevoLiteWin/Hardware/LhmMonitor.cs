using LibreHardwareMonitor.Hardware;

namespace MechrevoLite.Hardware;

/// <summary>
/// 实时功耗传感器（LibreHardwareMonitorLib 本地直读——原版控制台同款方案）：
/// CPU Package Power / GPU Power。MQTT 遥测无实时功耗字段。
/// </summary>
public class LhmMonitor : IDisposable
{
    readonly Computer _computer;
    readonly object _updateLock = new();
    long _lastUpdateTicks;
    long _lastOpenAttemptTicks;

    public float? CpuPower { get; private set; }
    public float? GpuPower { get; private set; }
    public int GpuMemoryTotalMb { get; private set; }   // 0=未知
    public int GpuMemoryUsedMb { get; private set; }    // 0=未知（遥测 GpuMem 是总量，已用量必须实读）

    public LhmMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
        };
        try
        {
            _computer.Open();
            Interlocked.Exchange(ref _lastOpenAttemptTicks, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("LHM open fail: " + ex.Message);
        }
    }

    bool _dumpedSensors;

        public void Update()
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastUpdateTicks) < 1000) return;
            lock (_updateLock)
            {
                now = Environment.TickCount64;
                if (now - _lastUpdateTicks < 1000) return;
                _lastUpdateTicks = now;
            try
        {
            CpuPower = null;
            GpuPower = null;
            GpuMemoryTotalMb = 0;
            GpuMemoryUsedMb = 0;
            bool nvidiaPresent = false;
            bool amdPresent = false;
            float? nvidiaPower = null;
            float? amdPower = null;
            float? integratedPower = null;
            if (_computer.Hardware.Count == 0)
                TryReopen();
            foreach (var hw in _computer.Hardware)
            {
                nvidiaPresent |= hw.HardwareType == HardwareType.GpuNvidia;
                amdPresent |= hw.HardwareType == HardwareType.GpuAmd;
                hw.Update();
                foreach (var sensor in hw.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;
                    if (sensor.SensorType == SensorType.Power)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && IsCpuPowerSensorName(sensor.Name))
                            CpuPower = sensor.Value;
                        else if (IsGpuHardwareType(hw.HardwareType) && IsGpuPowerSensorName(sensor.Name))
                        {
                            switch (hw.HardwareType)
                            {
                                case HardwareType.GpuNvidia:
                                    nvidiaPower = sensor.Value;
                                    break;
                                case HardwareType.GpuAmd:
                                    amdPower = sensor.Value;
                                    break;
                                default:
                                    integratedPower = sensor.Value;
                                    break;
                            }
                        }
                    }
                    else if (sensor.SensorType == SensorType.SmallData && IsGpuMemoryTotalSensorName(sensor.Name))
                        GpuMemoryTotalMb = (int)sensor.Value;
                    else if (sensor.SensorType == SensorType.SmallData && IsGpuMemoryUsedSensorName(sensor.Name))
                        GpuMemoryUsedMb = (int)sensor.Value;
                }
            }
            GpuPower = SelectGpuPower(nvidiaPresent, nvidiaPower, amdPresent, amdPower, integratedPower);
            // 诊断：首次刷新时把全部传感器清单导出到专用文件（主日志被 RGB 心跳刷屏会丢）
            if (!_dumpedSensors && _computer.Hardware.Count > 0)
            {
                _dumpedSensors = true;
                try
                {
                    var lines = _computer.Hardware
                        .SelectMany(h => h.Sensors.Select(s => $"{h.HardwareType}/{s.SensorType}/{s.Name}"))
                        .Distinct();
                    File.WriteAllLines(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite", "lhm_sensors.txt"),
                        lines);
                }
            catch (Exception ex) { Logger.WriteLineThrottled("lhm-dump", "LHM sensor dump failed: " + ex.Message, 5000); }
            }
            }
            catch (Exception ex) { Logger.WriteLineThrottled("lhm-update", "LHM sensor update failed: " + ex.Message, 5000); }
        }
    }

    internal static bool IsGpuHardwareType(HardwareType type) =>
        type.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCpuPowerSensorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string text = name.Trim();
        return text.Equals("Package", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Package Power", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("CPU Package Power", StringComparison.OrdinalIgnoreCase);
    }

    internal static float? SelectGpuPower(
        bool nvidiaPresent, float? nvidiaPower,
        bool amdPresent, float? amdPower,
        float? integratedPower) =>
        nvidiaPresent ? nvidiaPower : amdPresent ? amdPower : integratedPower;

    internal static bool IsGpuPowerSensorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string text = name.Trim();
        if (text.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("capacity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("minimum", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Equals("GPU Package", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("GPU Package Power", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("GPU Power", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Total Board Power", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Board Power", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GPU Power", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsGpuMemoryTotalSensorName(string? name) =>
        name is not null && (name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dedicated Memory Total", StringComparison.OrdinalIgnoreCase));

    static bool IsGpuMemoryUsedSensorName(string? name) =>
        name is not null && (name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 重开尝试的硬上限。_computer.Open() 内部会释放并注册 LHM 的内核驱动服务，
    /// 非管理员运行时必然失败。此前这里只有 3 秒节流没有次数上限，Overlay 常开时
    /// 会变成「每 3 秒尝试注册一次内核驱动」的无限循环——日志刷屏且毫无意义。
    /// 现在改为指数退避 + 封顶放弃，放弃后只在 Dispose/重建实例时才会再试。
    /// </summary>
    internal const int MaxReopenAttempts = 5;
    const int ReopenBaseDelayMs = 3000;

    int _reopenAttempts;
    bool _reopenGaveUp;

    /// <summary>第 attempt 次（0 起）重开前需要等待的毫秒数：3s、6s、12s、24s、48s。</summary>
    internal static int ReopenDelayMs(int attempt) =>
        ReopenBaseDelayMs * (1 << Math.Clamp(attempt, 0, MaxReopenAttempts - 1));

    internal static bool ShouldAttemptReopen(int attempts, bool gaveUp, long sinceLastAttemptMs) =>
        !gaveUp && attempts < MaxReopenAttempts && sinceLastAttemptMs >= ReopenDelayMs(attempts);

    void TryReopen()
    {
        long now = Environment.TickCount64;
        if (!ShouldAttemptReopen(_reopenAttempts, _reopenGaveUp, now - Interlocked.Read(ref _lastOpenAttemptTicks)))
            return;

        Interlocked.Exchange(ref _lastOpenAttemptTicks, now);
        _reopenAttempts++;
        try
        {
            _computer.Open();
            if (_computer.Hardware.Count > 0)
            {
                _reopenAttempts = 0;
                Logger.WriteLine("LHM reopened after hardware sensors became available.");
            }
            else if (_reopenAttempts >= MaxReopenAttempts)
            {
                _reopenGaveUp = true;
                Logger.WriteLine($"LHM exposed no sensors after {MaxReopenAttempts} attempts; local power monitoring disabled for this session.");
            }
        }
        catch (Exception ex)
        {
            if (_reopenAttempts >= MaxReopenAttempts)
            {
                _reopenGaveUp = true;
                Logger.WriteLine($"LHM open failed {MaxReopenAttempts} times, giving up (driver registration usually needs administrator rights): {ex.Message}");
            }
            else
                Logger.WriteLineThrottled("lhm-open", "LHM reopen failed: " + ex.Message, 5000);
        }
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }
}
