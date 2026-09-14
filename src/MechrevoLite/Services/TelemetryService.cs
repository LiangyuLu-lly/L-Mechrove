using MechrevoLite.Hardware;
using MechrevoLite.Models;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Services;

/// <summary>
/// 遥测服务：订阅 System/#、Fan/# 等主题，解析为 TelemetrySnapshot 供 UI 绑定。
/// 键名映射依据 m1-findings.md 实测载荷。
/// </summary>
public class TelemetryService
{
    public TelemetrySnapshot Snapshot { get; } = new();
    readonly IHardwareBackend _backend;
    bool _started;

    public TelemetryService(IHardwareBackend backend) { _backend = backend; }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        await _backend.SubscribeAsync(new[]
        {
            "System/#", "Fan/#", "GPUDevice/#", "BatteryProtection/#",
        });
        _backend.MessageReceived += OnMessage;

        // 握手实测：System/BatteryInfo 等需 System_ON 后才推流；
        // Fan/Status、Setting/Status 需显式 GETSTATUS 才回。
        await _backend.PublishAsync("System/Control", new { Action = "System_ON" });
        await _backend.PublishAsync("Fan/Control", new { Action = "GETSTATUS" });
        await _backend.PublishAsync("Setting/Control", new { Action = "GETSTATUS" });
    }

    void OnMessage(MqttMessage msg)
    {
        try
        {
            var o = JObject.Parse(msg.Payload);
            switch (msg.Topic)
            {
                case "System/CpuInfo":
                    Snapshot.CpuTemp = Num(o, "CpuTemperature");
                    Snapshot.CpuUsage = Num(o, "CpuUsage");
                    Snapshot.CpuFrequency = Num(o, "CpuFrequency");
                    Snapshot.CpuMaxFrequency = Num(o, "CpuMaxFrequency");
                    break;
                case "System/GpuInfo":
                    Snapshot.GpuTemp = Num(o, "GpuTemperature");
                    Snapshot.GpuUsage = Num(o, "GpuUsage");
                    Snapshot.GpuCoreFreq = Num(o, "GpuCoreFreq");
                    Snapshot.GpuMemFreq = Num(o, "GpuMemFreq");
                    break;
                case "System/FanInfo":
                    Snapshot.CpuFanDuty = (int)Num(o, "CpuFanDuty");
                    Snapshot.GpuFanDuty = (int)Num(o, "GpuFanDuty");
                    Snapshot.CpuFanRpm = (int)Num(o, "CpuFanRpm");
                    Snapshot.GpuFanRpm = (int)Num(o, "GpuFanRpm");
                    break;
                case "System/BatteryInfo":
                    Snapshot.BatteryPercent = (int)Num(o, "BatteryLifePercent");
                    Snapshot.BatteryCycleCount = (int)Num(o, "BatteryCycleCount");
                    break;
                case "Fan/Status":
                    Snapshot.IsAC = o["IsAC"]?.Value<bool>() ?? false;
                    Snapshot.OperatingMode = (int)Num(o, "OperatingMode");
                    Snapshot.PowerMode = o["PowerMode"]?.ToString() ?? "";
                    Snapshot.CpuPl1 = (int)Num(o, "CPU_PL1");
                    Snapshot.CpuPl2 = (int)Num(o, "CPU_PL2");
                    Snapshot.CpuTccOffset = (int)Num(o, "CPU_TccOffset");
                    Snapshot.FanBoostEnable = Num(o, "FanBoostEnable") > 0;
                    Snapshot.ProfileName = o["ProfileName"]?.ToString() ?? "";
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"telemetry parse fail: {msg.Topic} {ex.Message}");
        }
    }

    static double Num(JObject o, string key)
        => double.TryParse(o[key]?.ToString(), out var v) ? v : 0;
}
