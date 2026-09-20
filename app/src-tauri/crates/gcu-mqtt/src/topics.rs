//! Byte-identical copies of `MqttTopics.cs`.

pub const FAN_CONTROL: &str = "Fan/Control";
pub const FAN_STATUS: &str = "Fan/Status";
pub const FAN_TABLE: &str = "Fan/Table";
pub const FAN_FILTER: &str = "Fan/#";

pub const KEYBOARD_CTRL: &str = "Keyboard/Ctrl";
pub const KEYBOARD_STATUS: &str = "Keyboard/Status";
pub const KEYBOARD_PREFIX: &str = "Keyboard/";
pub const KEYBOARD_FILTER: &str = "Keyboard/#";

pub const LIGHTBAR_CTRL: &str = "HidLightbar/Ctrl";
pub const LIGHTBAR_STATUS: &str = "HidLightbar/Status";
pub const LIGHTBAR_PREFIX: &str = "HidLightbar/";
pub const LIGHTBAR_FILTER: &str = "HidLightbar/#";

pub const LOGO_LIGHT_CTRL: &str = "HidLightbar_Logo/Ctrl";
pub const LOGO_LIGHT_STATUS: &str = "HidLightbar_Logo/Status";
pub const LOGO_LIGHT_FILTER: &str = "HidLightbar_Logo/#";

pub const SETTING_CONTROL: &str = "Setting/Control";
pub const SETTING_STATUS: &str = "Setting/Status";
pub const SETTINGS_DEVICE_SWITCH_ITEM_STATUS: &str = "Settings/DeviceSwitchItemStatus";
pub const SETTING_FILTER: &str = "Setting/#";
pub const SETTINGS_FILTER: &str = "Settings/#";

pub const SYSTEM_CONTROL: &str = "System/Control";
pub const SYSTEM_CPU_INFO: &str = "System/CpuInfo";
pub const SYSTEM_GPU_INFO: &str = "System/GpuInfo";
pub const SYSTEM_MEMORY_INFO: &str = "System/MemoryInfo";
pub const SYSTEM_FAN_INFO: &str = "System/FanInfo";
pub const SYSTEM_BATTERY_INFO: &str = "System/BatteryInfo";
pub const SYSTEM_NETWORK_INFO: &str = "System/NetworkInfo";
pub const SYSTEM_HARDWARE_INFO: &str = "System/HardwareInfo";
pub const SYSTEM_FAN_ERROR_INFO: &str = "System/FanErrorInfo";
pub const SYSTEM_BATTERY_PROTECTION: &str = "System/BatteryProtection";
pub const SYSTEM_FILTER: &str = "System/#";

pub const BATTERY_PROTECTION_CONTROL: &str = "BatteryProtection/Control";

pub const GPU_DEVICE_STATUS: &str = "GPUDevice/Status";
pub const GPU_DEVICE_FILTER: &str = "GPUDevice/#";

pub const BT_LC_CONTROL: &str = "BT_LC/Control";
pub const BT_LC_STATUS: &str = "BT_LC/Status";
pub const BT_LC_FILTER: &str = "BT_LC/#";

pub const LCHWOC_CONTROL: &str = "LCHWOC/Control";
pub const LCHWOC_STATUS: &str = "LCHWOC/Status";
pub const LCHWOC_FILTER: &str = "LCHWOC/#";

pub const WHISPER_MODE_STATUS: &str = "WhisperMode/Status";
pub const CUSTOMIZE_FILTER: &str = "Customize/#";

/// Golden `topics.json` keys paired with the C# constant values.
pub const fn catalog() -> &'static [(&'static str, &'static str)] {
    &[
        ("FanControl", FAN_CONTROL),
        ("FanStatus", FAN_STATUS),
        ("FanTable", FAN_TABLE),
        ("FanFilter", FAN_FILTER),
        ("KeyboardCtrl", KEYBOARD_CTRL),
        ("KeyboardStatus", KEYBOARD_STATUS),
        ("KeyboardPrefix", KEYBOARD_PREFIX),
        ("KeyboardFilter", KEYBOARD_FILTER),
        ("LightbarCtrl", LIGHTBAR_CTRL),
        ("LightbarStatus", LIGHTBAR_STATUS),
        ("LightbarPrefix", LIGHTBAR_PREFIX),
        ("LightbarFilter", LIGHTBAR_FILTER),
        ("LogoLightCtrl", LOGO_LIGHT_CTRL),
        ("LogoLightStatus", LOGO_LIGHT_STATUS),
        ("LogoLightFilter", LOGO_LIGHT_FILTER),
        ("SettingControl", SETTING_CONTROL),
        ("SettingStatus", SETTING_STATUS),
        (
            "SettingsDeviceSwitchItemStatus",
            SETTINGS_DEVICE_SWITCH_ITEM_STATUS,
        ),
        ("SettingFilter", SETTING_FILTER),
        ("SettingsFilter", SETTINGS_FILTER),
        ("SystemControl", SYSTEM_CONTROL),
        ("SystemCpuInfo", SYSTEM_CPU_INFO),
        ("SystemGpuInfo", SYSTEM_GPU_INFO),
        ("SystemMemoryInfo", SYSTEM_MEMORY_INFO),
        ("SystemFanInfo", SYSTEM_FAN_INFO),
        ("SystemBatteryInfo", SYSTEM_BATTERY_INFO),
        ("SystemNetworkInfo", SYSTEM_NETWORK_INFO),
        ("SystemHardwareInfo", SYSTEM_HARDWARE_INFO),
        ("SystemFanErrorInfo", SYSTEM_FAN_ERROR_INFO),
        ("SystemBatteryProtection", SYSTEM_BATTERY_PROTECTION),
        ("SystemFilter", SYSTEM_FILTER),
        ("BatteryProtectionControl", BATTERY_PROTECTION_CONTROL),
        ("GpuDeviceStatus", GPU_DEVICE_STATUS),
        ("GpuDeviceFilter", GPU_DEVICE_FILTER),
        ("BtLcControl", BT_LC_CONTROL),
        ("BtLcStatus", BT_LC_STATUS),
        ("BtLcFilter", BT_LC_FILTER),
        ("LchwocControl", LCHWOC_CONTROL),
        ("LchwocStatus", LCHWOC_STATUS),
        ("LchwocFilter", LCHWOC_FILTER),
        ("WhisperModeStatus", WHISPER_MODE_STATUS),
        ("CustomizeFilter", CUSTOMIZE_FILTER),
    ]
}
