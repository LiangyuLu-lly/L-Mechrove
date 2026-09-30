using Microsoft.Win32;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace MechrevoLite.Diagnostics;

/// <summary>
/// 诊断包的「系统信息.txt」正文：应用版本 / 更新器检测串 / Windows 版本 / 机型 / CPU / GPU 与驱动 /
/// 权限 / GCU 连通 / 能力位。全部读本机（注册表、WMI、进程），不做任何网络访问。
///
/// 任何一项取不到都写成「未知」而不是抛异常——诊断包本身绝不能因为读不到某个字段就导不出来。
/// </summary>
internal static class DiagnosticSystemInfo
{
    internal static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("L-Mechrevo 诊断包 · 系统信息");
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (本地时间)");
        sb.AppendLine();

        AppendApp(sb);
        AppendWindows(sb);
        AppendMachine(sb);
        AppendCpu(sb);
        AppendGpus(sb);
        AppendLiquidCooling(sb);
        AppendPowerAndBattery(sb, MechrevoLite.Hardware.MechrevoService.EcReadTransportFactory, HardwareControl.PowerInputDecodeEnabled);
        AppendCapabilities(sb);

        return sb.ToString();
    }

    /// <summary>
    /// 供电 / 电池只读原始值诊断段读取的 EC 地址（hidden-readonly-info-plan §8 P7）：
    /// 0x7CC 供电状态、0x49F 适配器码、0x490 ecPowSource、0x400–0x40F _BIF 镜像、0x434–0x439 _BST 镜像、
    /// 0x4A2–0x4AB 电池温度 / 循环次数 / 电量。给以后补适配器表、核对电池健康用，不做任何解读。
    /// </summary>
    internal static readonly (int Start, int Count)[] PowerDiagnosticEcRanges =
    {
        (0x7CC, 1), (0x49F, 1), (0x490, 1), (0x400, 16), (0x434, 6), (0x4A2, 10),
    };

    /// <summary>
    /// 「供电 / 电池原始值」段。只走只读 EC 接口（<paramref name="transportFactory"/>，唯一的 IOCTL 就是读）
    /// 与 Windows 电池 IOCTL；10/20（<paramref name="decodeEnabled"/> 为 false）不读 EC。
    /// </summary>
    internal static void AppendPowerAndBattery(StringBuilder sb, Func<Probe.IEcReadTransport?> transportFactory, Func<bool> decodeEnabled)
    {
        sb.AppendLine("[供电 / 电池原始值]");
        PowerStatus? power = Safe(() => SystemInformation.PowerStatus);
        if (power is not null)
            sb.AppendLine($"Windows: 交流 {power.PowerLineStatus} · 电量 {power.BatteryLifePercent * 100:0}% · 充电状态 {power.BatteryChargeStatus}");

        MechrevoLite.Battery.BatteryStatusReading? status = Safe(MechrevoLite.Battery.BatteryRateReader.ReadStatus);
        sb.AppendLine(status is null
            ? "BATTERY_STATUS: 读不到"
            : $"BATTERY_STATUS: PowerState=0x{status.PowerState:X} Rate={(status.RateMilliwatts is int rate ? rate + " mW" : "未知")} " +
              $"Capacity={status.CapacityMilliwattHours} mWh Voltage={status.VoltageMillivolts} mV");
        MechrevoLite.Battery.BatteryInformationReading? info = Safe(MechrevoLite.Battery.BatteryRateReader.ReadInformation);
        sb.AppendLine(info is null
            ? "BATTERY_INFORMATION: 读不到"
            : $"BATTERY_INFORMATION: Designed={info.DesignedCapacity} mWh FullCharged={info.FullChargedCapacity} mWh " +
              $"CycleCount={info.CycleCount} SystemBattery={YesNo(info.IsSystemBattery)}");

        MechrevoLite.Hardware.PowerInputSample? sample = Safe(() => HardwareControl.PowerInput);
        sb.AppendLine(sample is null
            ? "供电采样: 尚未采样"
            : $"供电采样: {sample.Kind} · 适配器 {(sample.AdapterWatts is int watts ? watts + " W" : "未知")}");

        if (!(Safe(decodeEnabled)))
        {
            sb.AppendLine("EC: 本机（10/20 服务或独显）不读供电寄存器");
            sb.AppendLine();
            return;
        }
        Probe.IEcReadTransport? transport = Safe(transportFactory);
        if (transport is null)
        {
            sb.AppendLine("EC: 只读通道打不开");
            sb.AppendLine();
            return;
        }
        try
        {
            foreach ((int start, int count) in PowerDiagnosticEcRanges)
            {
                var bytes = new List<string>(count);
                for (int offset = 0; offset < count; offset++)
                {
                    int value;
                    try { value = transport.ReadByte(start + offset); }
                    catch { value = -1; }   // 传输层异常 = 该地址读不到，继续导出其余字节
                    bytes.Add(value is >= 0 and <= 0xFF ? value.ToString("X2") : "??");
                }
                sb.AppendLine($"EC 0x{start:X3}: {string.Join(' ', bytes)}");
            }
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
        sb.AppendLine();
    }

    static void AppendApp(StringBuilder sb)
    {
        sb.AppendLine("[应用]");
        sb.AppendLine($"应用版本（更新器检测串）: {Program.ReleaseVersion}");
        sb.AppendLine($"显示标签: {Program.ReleaseLabel}");
        sb.AppendLine($"可执行文件: {Environment.ProcessPath ?? AppContext.BaseDirectory}");
        sb.AppendLine($"进程架构: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"管理员运行: {YesNo(Safe(() => MechrevoLite.Helpers.ProcessHelper.IsUserAdministrator()))}");
        sb.AppendLine();
    }

    static void AppendWindows(StringBuilder sb)
    {
        string productName = "未知";
        string displayVersion = "";
        string build = Environment.OSVersion.Version.Build.ToString();
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                productName = (key.GetValue("ProductName") as string)?.Trim() ?? productName;
                displayVersion = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                string buildNumber = (key.GetValue("CurrentBuildNumber") as string)?.Trim() ?? build;
                string ubr = key.GetValue("UBR")?.ToString() ?? "";
                build = string.IsNullOrEmpty(ubr) ? buildNumber : $"{buildNumber}.{ubr}";
            }
        }
        catch { /* 注册表读不到就用 Environment 兜底 */ }

        sb.AppendLine("[Windows]");
        sb.AppendLine($"产品: {productName}");
        sb.AppendLine($"版本: {Environment.OSVersion.Version} (build {build}){(displayVersion.Length > 0 ? " · " + displayVersion : "")}");
        sb.AppendLine($"架构: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"描述: {RuntimeInformation.OSDescription}");
        sb.AppendLine();
    }

    static void AppendMachine(StringBuilder sb)
    {
        MechrevoLite.Hardware.MechrevoDeviceCapabilities caps = Safe(() => MechrevoLite.Hardware.MechrevoDeviceCapabilities.Current)
            ?? new MechrevoLite.Hardware.MechrevoDeviceCapabilities();
        sb.AppendLine("[机型]");
        sb.AppendLine($"厂商: {Or(caps.Manufacturer)}");
        sb.AppendLine($"型号: {Or(caps.Model)}");
        sb.AppendLine($"系列: {Or(caps.SystemFamily)}");
        sb.AppendLine($"SKU: {Or(caps.SystemSku)}");
        sb.AppendLine($"主板: {Or(caps.BaseboardProduct)}");
        sb.AppendLine($"BIOS: {Or(caps.BiosVersion)}");
        sb.AppendLine($"项目 ID: {Or(caps.ProjectId)}");
        sb.AppendLine($"奥驰是否识别为机械革命: {YesNo(caps.IsMechrevo)}");
        sb.AppendLine();
    }

    static void AppendCpu(StringBuilder sb)
    {
        string name = Safe(() => PawnIO.CpuInfo.Name) ?? "";
        string caption = Safe(() => PawnIO.CpuInfo.Caption) ?? "";
        sb.AppendLine("[CPU]");
        sb.AppendLine($"名称: {Or(name)}");
        if (caption.Length > 0) sb.AppendLine($"标识: {caption}");
        sb.AppendLine();
    }

    static void AppendGpus(StringBuilder sb)
    {
        sb.AppendLine("[GPU]");
        List<string> gpus = Safe(QueryGpus) ?? new List<string>();
        if (gpus.Count == 0) sb.AppendLine("未能枚举显示适配器（WMI 不可用）。");
        foreach (string gpu in gpus) sb.AppendLine(gpu);
        sb.AppendLine();
    }

    static List<string> QueryGpus()
    {
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                string name = item["Name"]?.ToString()?.Trim() ?? "未知";
                string driver = item["DriverVersion"]?.ToString()?.Trim() ?? "";
                string date = "";
                if (item["DriverDate"] is DateTime dt) date = dt.ToString("yyyy-MM-dd");
                result.Add($"{name}（驱动 {Or(driver)}{(date.Length > 0 ? " · " + date : "")}）");
            }
        }
        catch { /* WMI 不可用时不阻塞导出 */ }
        return result;
    }

    static void AppendLiquidCooling(StringBuilder sb)
    {
        sb.AppendLine("[液冷 / GCU]");
        MechrevoLite.Hardware.MechrevoHw? hw = Program.hw;
        sb.AppendLine($"GCU 已连接: {YesNo(hw?.LcConnected ?? false)}");
        sb.AppendLine($"固件版本: {Or(hw?.LcFwVersion)}");
        sb.AppendLine($"已发现设备数: {hw?.LcDeviceMacs.Count ?? 0}");
        sb.AppendLine();
    }

    static void AppendCapabilities(StringBuilder sb)
    {
        MechrevoLite.Hardware.MechrevoDeviceCapabilities caps = Safe(() => MechrevoLite.Hardware.MechrevoDeviceCapabilities.Current)
            ?? new MechrevoLite.Hardware.MechrevoDeviceCapabilities();
        sb.AppendLine("[能力位]");
        sb.AppendLine($"机型画像可用: {YesNo(caps.ProfileAvailable)}");
        sb.AppendLine($"键盘灯效: {YesNo(caps.Keyboard)}");
        sb.AppendLine($"灯条: {YesNo(caps.Lightbar)}");
        sb.AppendLine($"可寻址灯条: {YesNo(caps.RgbLightbar)}");
        sb.AppendLine($"Logo 灯: {YesNo(caps.LogoLight)}");
        sb.AppendLine($"液冷: {YesNo(caps.LiquidCooling)}");
        sb.AppendLine($"液冷自动模式: {YesNo(caps.LiquidCoolingAutoMode)}");
        sb.AppendLine($"显卡直连: {YesNo(caps.DgpuDirect)}");
        sb.AppendLine($"集显模式: {YesNo(caps.IgpuOnly)}");
        sb.AppendLine($"显卡热切换: {YesNo(caps.GpuHotSwap)}");
        sb.AppendLine($"NVIDIA 显卡: {YesNo(caps.NvidiaGpu)}");
        sb.AppendLine($"AMD 平台: {YesNo(caps.AmdPlatform)}");
        sb.AppendLine($"风扇设置: {YesNo(caps.FanSettings)}");
        sb.AppendLine($"风扇增强: {YesNo(caps.FanBoost)}");
        sb.AppendLine($"屏幕刷新率: {YesNo(caps.DisplayRefresh)}");
        sb.AppendLine($"屏幕校色: {YesNo(caps.ColorCalibration)}");
        sb.AppendLine($"响应加速: {YesNo(caps.LcdOverdrive)}");
        sb.AppendLine($"CPU 调优: {YesNo(caps.CpuPerformanceTuning)}");
        sb.AppendLine($"超频设置: {YesNo(caps.OverclockSettings)}");
        sb.AppendLine($"Turbo 模式: {YesNo(caps.TurboMode)}");
        sb.AppendLine($"Turbo 子模式: {YesNo(caps.TurboSubMode)}");
        sb.AppendLine($"键盘类型: {caps.KeyboardType}");
        sb.AppendLine($"刷新率档位: {caps.DisplayRefreshLevel}");
    }

    static string YesNo(bool value) => value ? "是" : "否";

    static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim();

    /// <summary>逐字段采集都要宽容：任何异常都退化为 null，而不是让整个诊断包导出失败。</summary>
    static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
