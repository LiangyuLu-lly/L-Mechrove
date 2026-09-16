using MechrevoLite.Hardware;

namespace MechrevoLite;

/// <summary>
/// AsusACPI 兼容层（Mechrevo 化）：保留 G-Helper UI 依赖的常量与方法签名，
/// 硬件调用转发到 MechrevoHw（MQTT）。未接通的成员返回默认值，M3+ 逐个实现。
/// </summary>
public class AsusACPI
{
    // ---- 设备地址常量（保留原值，仅作兼容；Mechrevo 不使用 ACPI 地址）----
    // ChargerMode / ChargerBarrel 已删除：它们只用于 ReadPowerSource 区分 Barrel/USBC，
    // 而本机没有该设备码的数据来源，USBC 结果不可达。
    // 这里曾经有六个 ASUS 专有的显卡设备码：GPUEcoROG / GPUEcoVivo /
    // GPUXGConnected / GPUXG / GPUMuxROG / GPUMuxVivo。
    // 前两组是 ROG 与 Vivobook 两条产品线各自的 Eco/Mux 码（我们只用统一的
    // GPUEco / GPUMux，它们在 DeviceGet 里映射到 MechrevoHw 的标志位），
    // 后两个属于 XG Mobile 外置显卡坞——那一族已整体删除。全部零引用。
    public const uint ScreenOverdrive = 0x00050019;
    public const uint ScreenMiniled1 = 0x0005001E;
    public const uint ScreenMiniled2 = 0x0005002E;
    public const uint ScreenFHD = 0x0005001C;
    public const uint ScreenHDRControl = 0x00050071;
    public const uint ScreenOptimalBrightness = 0x0005002A;

    // ---- 枚举值（G-Helper UI 语义）----
    public const int PerformanceBalanced = 0;
    public const int PerformanceTurbo = 1;
    public const int PerformanceSilent = 2;
    // PerformanceFullSpeed = 3 已删除：唯一的写入点是 Modes.InitFullSpeed()，
    // 那是给华硕 Vivobook 自动建 "Full Speed" 档用的，随该方法一起删除。
    public const int PerformanceManual = 4;
    public const int GPUModeEco = 0;
    public const int GPUModeStandard = 1;
    public const int GPUModeUltimate = 2;
    public const int GPUEco = 0;
    public const uint GPUMux = 0x00090016;
    public const uint StatusMode = 0x00090031;
    // VivoBookMode = 0x00110019 已删除：它是华硕 Vivobook 的一个位域，
    // 唯一读取点是已删除的 Modes.InitFullSpeed()。

    // ---- 功耗上下限：**外层安全边界，不是机型限值** ----
    //
    // 这些数字继承自 g-helper，只用于挡住明显荒谬的输入。**任何面向用户的范围都必须用
    // MechrevoHw 的运行时值**：Pl1Minimum/Pl1Maximum、Pl2Minimum/Pl2Maximum、
    // TccMinimum/TccMaximum、GpuTgpMinimum/GpuTgpMaximum、GpuDbMinimum/GpuDbMaximum，
    // 它们来自 Fan/Status 的 *Minimum/*Maximum 字段，逐机型不同。
    //
    // 举例说明差距有多大：开发机（BIOS_PROJECT_ID=IDY）实测 PL1/PL2/PL4 上限都是 210 W，
    // 而 docs/hardware/fan-table-defaults.json 里 PH4TRX1 的出厂表是 PL1=35 / PL2=60。
    // 把下面任何一个常量当成机型限值都会算错。
    public const int MinTotal = 5;
    public const int MinCPU = 5;
    public const int MaxCPU = 210;
    public const int MaxTotal = 210;

    public static bool IsInvalidCurve(byte[] curve)
    {
        // The compatibility format is exactly eight temperatures followed by eight duties.
        // Reject truncated external/profile data before indexing the first eight points.
        if (curve is null || curve.Length != 16) return true;
        // 垃圾数据检测：8 个温度点除首点外应 >= 30°C（机械革命曲线 0,48,52,...）；
        // 残留的 01-00-01-00 类数据温度非单调且几乎全 0，视为无效
        for (int i = 1; i < 8; i++)
        {
            if (curve[i] < 30 && curve[i] != 0) return true;
        }
        return false;
    }

    public bool IsConnected() => Program.hw?.IsConnected == true;

    /// <summary>
    /// ASUS ACPI 读取的适配层。本机只有 GPU 模式两个设备码有真实数据来源（MQTT 缓存），
    /// 其余一律返回 -1 = 不支持。
    ///
    /// 这里曾经对未映射的设备码返回 0，后果是继承自 g-helper 的代码把 0 当成一个
    /// 「读到了、值为 0」的有效读数，于是：
    ///   - Settings.VisualiseScreen 的 `miniled1 >= 0` 成立，在没有 miniled 面板的机器上
    ///     显示出「多区背光」按钮，点击后什么都不会发生；
    ///   - 开启 HDR 后 `hdrControl >= 0` 同样成立，冒出一个同样无效的 HDR 控制按钮；
    ///   - ScreenControl.InitScreen 把伪造的 miniled/overdrive 值写进 config.json；
    ///   - Modes.InitFullSpeed 越过 `vivoMode < 0` 守卫，每次启动打一行无意义的日志。
    ///
    /// 判据很简单：没有数据来源就不能返回一个看起来合法的值。调用方普遍用 `>= 0`
    /// 表示「该硬件存在」，所以未映射必须是 -1。
    /// </summary>
    public int DeviceGet(uint code, int len = 4)
    {
        // 未连接时同样返回 -1：防止 InitGPUMode 用假值高亮/隐藏 GPU 面板（连上后需重调 InitGPUMode）
        if (Program.hw is not { IsConnected: true }) return -1;
        if (code == GPUEco) return Program.hw.GpuEcoFlag;
        if (code == GPUMux) return Program.hw.GpuMuxFlag;
        return NotSupported;
    }

    /// <summary>未映射设备码的返回值。调用方约定 &lt; 0 表示该硬件不存在。</summary>
    public const int NotSupported = -1;

    public async Task SetPerformanceMode(int mode, string logName = "Mode")
    {
        if (Program.hw is null) return;
        try
        {
            if (Program.service is not null)
                await Program.service.SwitchMode(mode);
            else
                await Program.hw.SetMode(mode);
        }
        catch (Exception ex) { Logger.WriteLine("SetMode fail: " + ex.Message); }
    }

    public int GetFan(AsusFan device) => Program.hw is not { IsConnected: true } ? -1 : (device == AsusFan.CPU ? Program.hw.CpuFanRpm : Program.hw.GpuFanRpm);

    /// <summary>
    /// 当前机型画像。优先用已连接实例上的那份（含 MQTT 运行时纠偏），
    /// 未连接时回落到进程级共享快照，保证连接之前也按真实机型回答。
    /// </summary>
    static MechrevoDeviceCapabilities Capabilities =>
        Program.hw?.Capabilities ?? MechrevoDeviceCapabilities.Current;

    // ===== 能力应答 =====
    //
    // 这几个方法此前是写死的字面量，注释还写着「本机实测为 NVIDIA」。本项目要覆盖
    // 机械革命全系机型（官方 ProjectID 枚举 45 项、BIOS_PROJECT_ID 37 项），把开发机的
    // 实测结果当成全体真值会直接算错：AMD 独显机型上 IsNVidiaGPU() 恒真，
    // 支持 LCD Overdrive 的机型上 IsOverdriveSupported() 恒假。
    // 现在一律按当前机型画像回答，数据源是官方自己写的 ItemSupport 注册表。

    /// <summary>是否为 AMD 平台的全 AMD 功耗体系（SPL/SPPT/FPPT 而非 PL1/PL2）。</summary>
    public bool IsAllAmdPPT() => Capabilities.AmdPlatform;

    /// <summary>是否为 NVIDIA 独显机型。</summary>
    public bool IsNVidiaGPU() => Capabilities.NvidiaGpu;

    /// <summary>
    /// 是否存在可读数的第三颗（中置）风扇。这套协议里恒为 false。
    ///
    /// 「中置风扇」是 g-helper 的华硕概念。机械革命侧没有对应遥测：
    /// System/FanInfo 只有 CPU/GPU 的占空比与转速，全协议也没有别的风扇读数主题，
    /// 而官方界面是全机型共用的、只显示两颗，所以没有哪个机型会报第三颗。
    ///
    /// 注意别把它和 ItemSupport 的 RamFan1p5Support 混在一起：后者说的是
    /// 「这台机器的风扇表按内存风扇 1.5 的布局排」，那颗风扇由 GCUService 随风扇表
    /// 自动管理，EC 侧只有风扇表寄存器、没有转速寄存器，读不出转速也没有控制入口。
    /// 用它当「有没有可读的第三颗风扇」的判据会让界面显示一块永远是空的读数。
    /// </summary>
    public bool IsMidFanSupported() => HardwareControl.midFan is not null;

    /// <summary>
    /// ASUS XG Mobile 外置显卡坞。这不是机型差异——机械革命全系都没有这个接口，
    /// 是 g-helper 的硬件概念，因此恒为 false 是正确的，而不是未适配。
    /// </summary>
    public bool IsXGConnected() => false;

    /// <summary>屏幕 Overdrive（LCD 加速）。</summary>
    public bool IsOverdriveSupported() => Capabilities.LcdOverdrive;

    /// <summary>
    /// 某个 ASUS ACPI 设备码在当前机型上是否可用。
    /// 只有能对应到真实能力位的设备码才回答 true；其余一律 false——
    /// 与 <see cref="DeviceGet"/> 返回 <see cref="NotSupported"/> 保持同一套语义，
    /// 避免继承代码基于「看起来可用」去写一个空实现。
    /// </summary>
    public bool IsSupported(uint code)
    {
        if (code == GPUEco || code == GPUMux) return Program.hw is { IsConnected: true };
        if (code == ScreenOverdrive) return Capabilities.LcdOverdrive;
        if (code == ScreenMiniled1 || code == ScreenMiniled2) return Capabilities.LocalDimming;
        return false;
    }

    public int SetGPUEco(int eco) => 0;
}
