namespace MechrevoLite.Hardware;

/// <summary>
/// MQTT 主题常量。
///
/// 主题是**逐字节精确匹配**的：任何拼写、大小写或斜杠差异都不会编译报错、也不会
/// 运行时报错，只会静默收不到状态或命令不生效。因此这里的具名主题必须与设备固件
/// 实际发布/订阅的主题逐字节一致——即使某个拼写看起来像笔误，也照实保留；
/// 通配符过滤器必须与订阅出去的主题过滤器逐字节一致。
/// 修改或新增前先用真机抓包核对。
/// </summary>
internal static class MqttTopics
{
    // ---- 风扇 ----

    internal const string FanControl = "Fan/Control";
    internal const string FanStatus = "Fan/Status";
    internal const string FanTable = "Fan/Table";
    /// <summary>订阅过滤器：覆盖 <c>Fan/</c> 下全部主题。</summary>
    internal const string FanFilter = "Fan/#";

    // ---- 键盘 ----

    internal const string KeyboardCtrl = "Keyboard/Ctrl";
    internal const string KeyboardStatus = "Keyboard/Status";
    /// <summary>键盘通道前缀：<c>Keyboard/</c> 开头的报文都算键盘主题。</summary>
    internal const string KeyboardPrefix = "Keyboard/";
    /// <summary>订阅过滤器：覆盖 <c>Keyboard/</c> 下全部主题。</summary>
    internal const string KeyboardFilter = "Keyboard/#";

    // ---- 灯条 ----

    internal const string LightbarCtrl = "HidLightbar/Ctrl";
    internal const string LightbarStatus = "HidLightbar/Status";
    /// <summary>主灯带前缀。Logo 子灯带是独立主题，不在此前缀之内。</summary>
    internal const string LightbarPrefix = "HidLightbar/";
    /// <summary>订阅过滤器：覆盖 <c>HidLightbar/</c> 下全部主题。</summary>
    internal const string LightbarFilter = "HidLightbar/#";

    // ---- Logo 灯（独立子灯带主题）----

    internal const string LogoLightCtrl = "HidLightbar_Logo/Ctrl";
    internal const string LogoLightStatus = "HidLightbar_Logo/Status";
    /// <summary>订阅过滤器：Logo 子灯带，不匹配 <c>HidLightbar/#</c>。</summary>
    internal const string LogoLightFilter = "HidLightbar_Logo/#";

    // ---- 设置 ----

    internal const string SettingControl = "Setting/Control";
    internal const string SettingStatus = "Setting/Status";
    internal const string SettingsDeviceSwitchItemStatus = "Settings/DeviceSwitchItemStatus";
    /// <summary>订阅过滤器：覆盖 <c>Setting/</c> 下全部主题。</summary>
    internal const string SettingFilter = "Setting/#";
    /// <summary>订阅过滤器：复数 Settings，只承载 DeviceSwitchItemStatus。</summary>
    internal const string SettingsFilter = "Settings/#";

    // ---- 系统状态 ----

    internal const string SystemControl = "System/Control";
    internal const string SystemCpuInfo = "System/CpuInfo";
    internal const string SystemGpuInfo = "System/GpuInfo";
    internal const string SystemMemoryInfo = "System/MemoryInfo";
    internal const string SystemFanInfo = "System/FanInfo";
    internal const string SystemBatteryInfo = "System/BatteryInfo";
    internal const string SystemNetworkInfo = "System/NetworkInfo";
    internal const string SystemHardwareInfo = "System/HardwareInfo";
    internal const string SystemFanErrorInfo = "System/FanErrorInfo";
    internal const string SystemBatteryProtection = "System/BatteryProtection";
    /// <summary>订阅过滤器：覆盖 <c>System/</c> 下全部主题。</summary>
    internal const string SystemFilter = "System/#";

    // ---- 电池保护 ----

    internal const string BatteryProtectionControl = "BatteryProtection/Control";

    // ---- 显卡 ----

    internal const string GpuDeviceStatus = "GPUDevice/Status";
    /// <summary>订阅过滤器：覆盖 <c>GPUDevice/</c> 下全部主题。</summary>
    internal const string GpuDeviceFilter = "GPUDevice/#";

    // ---- 液冷（BT_LC）----

    internal const string BtLcControl = "BT_LC/Control";
    internal const string BtLcStatus = "BT_LC/Status";
    /// <summary>订阅过滤器：覆盖 <c>BT_LC/</c> 下全部主题。</summary>
    internal const string BtLcFilter = "BT_LC/#";

    // ---- GPU 超频通道（LCHWOC）----

    internal const string LchwocControl = "LCHWOC/Control";
    internal const string LchwocStatus = "LCHWOC/Status";
    /// <summary>订阅过滤器：覆盖 <c>LCHWOC/</c> 下全部主题。</summary>
    internal const string LchwocFilter = "LCHWOC/#";

    // ---- 其他 ----

    /// <summary>官方静音模式主题。本应用没有订阅 <c>WhisperMode/#</c>，仅诊断留档用。</summary>
    internal const string WhisperModeStatus = "WhisperMode/Status";
    /// <summary>订阅过滤器：官方 Customize 主题族，当前没有解析分支（仅旁路诊断）。</summary>
    internal const string CustomizeFilter = "Customize/#";
}
