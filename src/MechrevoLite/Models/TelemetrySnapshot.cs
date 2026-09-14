using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MechrevoLite.Models;

/// <summary>遥测快照：键名严格对齐 m1-findings.md 抓包的真实 JSON 字段。</summary>
public class TelemetrySnapshot : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // System/CpuInfo
    double _cpuTemp; public double CpuTemp { get => _cpuTemp; set => Set(ref _cpuTemp, value); }
    double _cpuUsage; public double CpuUsage { get => _cpuUsage; set => Set(ref _cpuUsage, value); }
    double _cpuFrequency; public double CpuFrequency { get => _cpuFrequency; set => Set(ref _cpuFrequency, value); }
    double _cpuMaxFrequency; public double CpuMaxFrequency { get => _cpuMaxFrequency; set => Set(ref _cpuMaxFrequency, value); }

    // System/GpuInfo
    double _gpuTemp; public double GpuTemp { get => _gpuTemp; set => Set(ref _gpuTemp, value); }
    double _gpuUsage; public double GpuUsage { get => _gpuUsage; set => Set(ref _gpuUsage, value); }
    double _gpuCoreFreq; public double GpuCoreFreq { get => _gpuCoreFreq; set => Set(ref _gpuCoreFreq, value); }
    double _gpuMemFreq; public double GpuMemFreq { get => _gpuMemFreq; set => Set(ref _gpuMemFreq, value); }

    // System/FanInfo
    int _cpuFanDuty; public int CpuFanDuty { get => _cpuFanDuty; set => Set(ref _cpuFanDuty, value); }
    int _gpuFanDuty; public int GpuFanDuty { get => _gpuFanDuty; set => Set(ref _gpuFanDuty, value); }
    int _cpuFanRpm; public int CpuFanRpm { get => _cpuFanRpm; set => Set(ref _cpuFanRpm, value); }
    int _gpuFanRpm; public int GpuFanRpm { get => _gpuFanRpm; set => Set(ref _gpuFanRpm, value); }

    // System/BatteryInfo
    int _batteryPercent; public int BatteryPercent { get => _batteryPercent; set => Set(ref _batteryPercent, value); }
    int _batteryCycleCount; public int BatteryCycleCount { get => _batteryCycleCount; set => Set(ref _batteryCycleCount, value); }

    // Fan/Status
    bool _isAC; public bool IsAC { get => _isAC; set => Set(ref _isAC, value); }
    int _operatingMode; public int OperatingMode { get => _operatingMode; set => Set(ref _operatingMode, value); }
    string _powerMode = ""; public string PowerMode { get => _powerMode; set => Set(ref _powerMode, value); }
    int _cpuPl1; public int CpuPl1 { get => _cpuPl1; set => Set(ref _cpuPl1, value); }
    int _cpuPl2; public int CpuPl2 { get => _cpuPl2; set => Set(ref _cpuPl2, value); }
    int _cpuTccOffset; public int CpuTccOffset { get => _cpuTccOffset; set => Set(ref _cpuTccOffset, value); }
    bool _fanBoostEnable; public bool FanBoostEnable { get => _fanBoostEnable; set => Set(ref _fanBoostEnable, value); }
    string _profileName = ""; public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
}
