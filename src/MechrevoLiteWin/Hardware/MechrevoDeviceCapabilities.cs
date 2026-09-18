using Microsoft.Win32;

namespace MechrevoLite.Hardware;

/// <summary>
/// Static feature flags written by the official GCU for the current project ID.
/// Runtime MQTT fields remain authoritative for adjustable ranges and can enable a
/// feature when an older registry profile is incomplete.
/// </summary>
public sealed class MechrevoDeviceCapabilities
{
    static readonly string[] ItemSupportPaths =
    {
        @"SOFTWARE\OEM\GamingCenter2\ItemSupport",
        @"SOFTWARE\OEM\GamingCenter\ItemSupport",
        @"SOFTWARE\OEM\ControlCenter\ItemSupport",
    };

    static readonly string[] GpuConfigPaths =
    {
        @"SOFTWARE\OEM\GamingCenter2\MySetting\GpuConfig",
        @"SOFTWARE\OEM\GamingCenter\MySetting\GpuConfig",
        @"SOFTWARE\OEM\ControlCenter\MySetting\GpuConfig",
    };

    static readonly string[] RgbKeyboardPaths =
    {
        @"SOFTWARE\OEM\GamingCenter2\RGBKeyboard",
        @"SOFTWARE\OEM\GamingCenter\RGBKeyboard",
        @"SOFTWARE\OEM\ControlCenter\RGBKeyboard",
    };

    public string ProjectId { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string SystemFamily { get; init; } = "";
    public string SystemSku { get; init; } = "";
    public string BaseboardProduct { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public bool IsMechrevo { get; init; }
    public bool ProfileAvailable { get; init; }
    public bool Keyboard { get; init; }
    public bool Lightbar { get; init; }
    public bool RgbLightbar { get; init; }
    public bool LogoLight { get; init; }
    public bool DisplayRefresh { get; init; }
    public bool ColorCalibration { get; init; }
    public bool DgpuDirect { get; init; }
    public bool IgpuOnly { get; init; }
    public bool FanBoost { get; init; }
    public bool FanSettings { get; init; }
    public bool OverclockSettings { get; init; }
    public bool CpuPerformanceTuning { get; init; }
    public bool LiquidCooling { get; init; }
    public bool LiquidCoolingAutoMode { get; init; }
    public bool Numpad { get; init; }
    public bool AcRecovery { get; init; }

    /// <summary>
    /// N15 #17: the AC-recovery ("来电自启") on/off state, or <c>null</c> when it cannot be read.
    /// The vendor service exposes two fields: <c>AcRecoverySwitch_Status</c> (a string) and
    /// <c>ACRecoveryStatus</c> (a hex-encoded byte, GCUService.decompiled.cs:21092). The string is
    /// primary; the byte is the fallback. <c>null</c> means unknown - it must never be reported as
    /// OFF, which is what made the icon show OFF while the feature was actually ON.
    /// </summary>
    public bool? AcRecoveryOn { get; init; }
    public bool LcdOverdrive { get; init; }
    public bool LocalDimming { get; init; }
    public bool TurboMode { get; init; }
    public bool TurboSubMode { get; init; }

    /// <summary>
    /// 「静音狂暴」入口的三态支持判定。官方 CCUWinUI 只在
    /// <c>ItemSupport\IsTurboSubModeSupport == 1</c> 时把 Turbo 拆成「静音狂暴/超频狂暴」两个子模式
    /// （_decompiled/CCUWinUI.decompiled.cs:57721-57739：<c>IsOverClockingSupport = IsTurboSubModeSupport == 1</c>，
    /// 键缺失时官方默认 0 = 不支持）。对应关系：
    /// <c>TurboSubMode</c> 为真 → Supported；读到画像但该位为假 → Unsupported；
    /// 连 ItemSupport 画像都读不到（ProfileAvailable=false）→ Unknown，按「只在确认支持时显示」隐藏。
    /// </summary>
    internal FeatureAvailability SilentTurboAvailability =>
        TurboSubMode ? FeatureAvailability.Supported
        : ProfileAvailable ? FeatureAvailability.Unsupported
        : FeatureAvailability.Unknown;
    public bool SmartBalance { get; init; }
    public bool RamFan15 { get; init; }
    public bool NvidiaGpu { get; init; }
    public bool GpuHotSwap { get; init; }
    public bool AmdPlatform { get; init; }
    public bool IsOldType { get; init; }
    public int KeyboardType { get; init; }
    public int DisplayRefreshLevel { get; init; }

    public string IdentitySummary =>
        $"manufacturer={Manufacturer}, model={Model}, family={SystemFamily}, sku={SystemSku}, " +
        $"board={BaseboardProduct}, bios={BiosVersion}, project={ProjectId}";

    static readonly object CurrentLock = new();
    static MechrevoDeviceCapabilities? _current;
    static long _snapshotRevision;

    /// <summary>测试接缝：替换真正的注册表读取；为 null 时走 <see cref="Load"/>。</summary>
    internal static Func<MechrevoDeviceCapabilities>? SnapshotFactoryOverride;

    /// <summary>
    /// 快照代数：每重建一次（<see cref="Current"/> 首次构建或 <see cref="Refresh"/> 之后）自增。
    /// 给「这份快照是否在配置变更之后」提供可断言的依据。
    /// </summary>
    public static long SnapshotRevision
    {
        get { lock (CurrentLock) return _snapshotRevision; }
    }

    /// <summary>
    /// 进程级共享的机型画像。
    ///
    /// 存在的理由：适配层（<see cref="AsusACPI"/>）和其他拿不到 <c>MechrevoHw</c> 实例的
    /// 代码也必须按**当前这台机器**回答能力问题，而不是按开发机的实测结果写死。
    /// 此前 <c>AsusACPI.IsNVidiaGPU()</c> 直接 <c>=> true</c> 并注释「本机实测为 NVIDIA」，
    /// 在 AMD 独显机型上就是错的。
    ///
    /// <see cref="Load"/> 会读注册表并写一行日志，所以这里缓存一份；GCU 更新了
    /// ItemSupport 之后调用 <see cref="Invalidate"/> 让下次访问重新读取。
    /// </summary>
    public static MechrevoDeviceCapabilities Current
    {
        get
        {
            lock (CurrentLock) return _current ??= BuildLocked();
        }
    }

    static MechrevoDeviceCapabilities BuildLocked()
    {
        MechrevoDeviceCapabilities snapshot = SnapshotFactoryOverride is { } factory ? factory() : Load();
        _snapshotRevision++;
        return snapshot;
    }

    /// <summary>丢弃缓存的机型画像，下一次访问 <see cref="Current"/> 时重新读注册表。</summary>
    public static void Invalidate()
    {
        lock (CurrentLock) _current = null;
    }

    /// <summary>
    /// 重建并返回一份最新画像。用于服务重写了 <c>ItemSupport</c> / 配置变更之后，
    /// 确保旧快照不会越过这次变更继续被消费。
    /// </summary>
    public static MechrevoDeviceCapabilities Refresh()
    {
        Invalidate();
        return Current;
    }

    /// <summary>测试用：直接注入一份画像，避免测试依赖运行机器的注册表。</summary>
    internal static void OverrideCurrent(MechrevoDeviceCapabilities? capabilities)
    {
        lock (CurrentLock) _current = capabilities;
    }

    /// <summary>
    /// 机型注入变量（T31，供 UI 审计子进程用）：非空即把身份字段覆盖成该代号。
    /// 只改身份，能力位仍取本机服务画像——审计要在真机上跑，不能把画像清空。
    /// </summary>
    internal const string ModelOverrideVariable = "LMECHREVO_MODEL_OVERRIDE";

    internal static string? ModelOverride
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(ModelOverrideVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public static MechrevoDeviceCapabilities Load()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var identity = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ReadMachineIdentity(identity);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Cannot read machine identity: " + ex.Message);
        }

        ReadItemSupportInto(values);

        string? modelOverride = ModelOverride;
        if (modelOverride is not null)
        {
            // 子进程注入（UI 审计的 LMECHREVO_MODEL_OVERRIDE=<代号>）：覆盖身份来源，
            // 不碰注册表；能力位仍来自本机服务画像。
            values["BIOS_PROJECT_ID"] = modelOverride;
            identity["SystemProductName"] = modelOverride;
        }

        var capabilities = FromValues(values, identity, DetectLogoLightingRegistry());
        Logger.WriteLine("Device capabilities: " + capabilities.IdentitySummary +
            $", officialProfile={capabilities.ProfileAvailable}, logo={capabilities.LogoLight}");
        return capabilities;
    }

    /// <summary>
    /// 读服务写入的 <c>ItemSupport</c> + <c>GpuConfig</c> 原始值（含 <c>LMECHREVO_MODEL_OVERRIDE</c> 注入），
    /// 不做任何能力推导。供 <see cref="FeatureMatrix"/> 只读消费。
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> ReadServiceValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ReadItemSupportInto(values);
        if (ModelOverride is { } modelOverride) values["BIOS_PROJECT_ID"] = modelOverride;
        return values;
    }

    /// <summary>
    /// 厂商服务是否已写入非空的 <c>ItemSupport</c>（N8 的"服务在服务本机"判据之一）。
    /// 只读；服务缺失/未连时该键为空，返回 <c>false</c>，保留 D1 的只读降级。
    /// </summary>
    internal static bool HasItemSupportContent()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ReadItemSupportInto(values);
        return values.Count > 0;
    }

    // Newer packages use the 64-bit GamingCenter2 key. Older Control Center
    // installers may use a 32-bit registry view or a legacy product root.
    // Canonical values are read first and legacy keys only fill gaps.
    static void ReadItemSupportInto(IDictionary<string, object?> values)
    {
        foreach (string path in ItemSupportPaths.Concat(GpuConfigPaths))
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? key = baseKey.OpenSubKey(path);
                    if (key is null) continue;
                    foreach (string name in key.GetValueNames())
                        values.TryAdd(name, key.GetValue(name));
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Cannot read device capabilities from {path} ({view}): {ex.Message}");
                }
            }
        }
    }

    internal static MechrevoDeviceCapabilities FromValues(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?>? identity = null,
        bool logoRegistryDetected = false)
    {
        object? Value(params string[] names)
        {
            foreach (string name in names)
                if (values.TryGetValue(name, out object? value) && value is not null)
                    return value;
            return null;
        }

        int Number(params string[] names)
        {
            object? value = Value(names);
            if (value is null) return 0;
            if (value is bool boolean) return boolean ? 1 : 0;
            if (int.TryParse(value.ToString(), out int number)) return number;
            return value.ToString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "on" or "supported" or "enable" or "enabled" => 1,
                _ => 0,
            };
        }

        bool Flag(params string[] names) => Number(names) > 0;
        string Identity(params string[] names)
        {
            if (identity is not null)
            {
                foreach (string name in names)
                    if (identity.TryGetValue(name, out object? value) && value is not null)
                    {
                        string result = value.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(result)) return result;
                    }
            }
            return Value(names)?.ToString()?.Trim() ?? "";
        }

        int keyboardType = Number("KeyboardType", "RGBKeyboardType", "KeyboardLightType");
        int refreshLevel = Number("DispalyRefresh", "DisplayRefresh", "DisplayRefreshSupport",
            "DisplayRefreshRateSupport", "RefreshRateSupport");
        int calibration = Number("ColorCalibrationSupport", "DisplayColorCalibrationSupport", "ColorCalibration");
        bool nvidiaGpu = Flag("IsNvGpu", "IsNvidiaGpu");
        bool igpuOnly = Flag("iGPUModeOnlySupport", "IGpuOnlyModeSupport", "IntegratedGpuOnlySupport");
        string manufacturer = Identity("SystemManufacturer", "Manufacturer", "Vendor");
        string baseboardManufacturer = Identity("BaseBoardManufacturer", "BaseboardManufacturer");
        return new MechrevoDeviceCapabilities
        {
            ProjectId = Value("BIOS_PROJECT_ID", "BIOSProjectID", "BIOSProjectId", "ProjectID", "ProjectId")?.ToString() ?? "",
            Manufacturer = manufacturer,
            Model = Identity("SystemProductName", "Model", "ProductName"),
            SystemFamily = Identity("SystemFamily", "Family"),
            SystemSku = Identity("SystemSKU", "SystemSku", "SKU", "SkuNumber"),
            BaseboardProduct = Identity("BaseBoardProduct", "BaseboardProduct", "BoardProduct"),
            BiosVersion = Identity("BIOSVersion", "BiosVersion", "SMBIOSBIOSVersion"),
            IsMechrevo = IsMechrevoName(manufacturer) || IsMechrevoName(baseboardManufacturer),
            ProfileAvailable = values.Count > 0,
            Keyboard = Flag("KeyboardSupport", "RGBKeyboardSupport", "SingleColorKeyboardSupport",
                "KeyboardLightSupport", "KeyboardBacklightSupport") || keyboardType > 0,
            Lightbar = Flag("LightbarSupport", "LightBarSupport", "HidLightbarSupport"),
            RgbLightbar = Flag("RGBLightbarSupport", "RgbLightbarSupport"),
            LogoLight = logoRegistryDetected || Flag("LogoLightSupport", "LogoLightbarSupport",
                "LightbarLogoSupport", "HidLightbarLogoSupport", "RGBLogoLightSupport"),
            DisplayRefresh = refreshLevel > 0,
            ColorCalibration = calibration > 0,
            DgpuDirect = Flag("DGpuDirectConnectionSupport", "DiscreteGpuDirectConnectionSupport", "MuxSwitchSupport"),
            IgpuOnly = igpuOnly,
            FanBoost = Flag("FanBoostBtnSupport", "FanBoostSupport"),
            FanSettings = Flag("FanSettingsSupport", "FanSettingSupport", "CustomFanSupport"),
            OverclockSettings = Flag("OcSettingsSupport", "OCSettingsSupport", "GpuOverclockSupport"),
            CpuPerformanceTuning = Flag("CPUPerformanceAndOverClockMenuSupport", "CpuPerformanceTuningSupport"),
            LiquidCooling = Flag("LiquidCoolingSupport", "WaterCoolingSupport"),
            LiquidCoolingAutoMode = Flag("LiquidCoolingAutoModeSupport", "WaterCoolingAutoModeSupport"),
            Numpad = Flag("NumPadSupport", "NumpadSupport"),
            AcRecovery = Flag("AcRecoverySwitchSupport") || Flag("AcRecoverySwitchBiosSupport"),
            // N15 #17: the string field is primary; the vendor's hex byte is the fallback. Neither
            // present => null (unknown), never false.
            AcRecoveryOn = AcRecoveryState(Value("AcRecoverySwitch_Status"), Value("ACRecoveryStatus")),
            LcdOverdrive = Flag("LCDOverdriveSupport"),
            LocalDimming = Flag("LocalDimmingSupport"),
            TurboMode = Flag("TurboModeSupport"),
            // SilentPerformanceModeSwitch is the current sub-mode state (0/1), not
            // a capability bit. Treating it as support exposes a broken button on
            // devices whose profile explicitly disables turbo sub-modes.
            TurboSubMode = Flag("IsTurboSubModeSupport"),
            SmartBalance = Flag("IsSmartBalanceSupport"),
            RamFan15 = Flag("RamFan1p5Support"),
            NvidiaGpu = nvidiaGpu,
            // T9: the APVersionCheck>23 numeric proxy is gone. Hot swap is decided solely by the two
            // service-written values; either missing => not offered (fail-closed). See t9-hotswap-values.json.
            GpuHotSwap = Flag("GpuHotSwapSwitchSupport") && Flag("lgpuHotSwapSwitchStatus"),
            AmdPlatform = Flag("IsAMDPlatform", "IsAmdPlatform"),
            IsOldType = Flag("IsOldType"),
            KeyboardType = keyboardType,
            DisplayRefreshLevel = refreshLevel,
        };
    }

    /// <summary>
    /// N15 #17: resolve the AC-recovery on/off state. The vendor's string field
    /// (<c>AcRecoverySwitch_Status</c>, values <c>ACRECOVERY_TOGGLE_ON</c>/<c>_OFF</c>) is primary;
    /// the hex-encoded byte <c>ACRecoveryStatus</c> (GCUService.decompiled.cs:21092) is the fallback.
    /// Returns <c>null</c> when neither is readable - unknown must not be reported as OFF.
    /// </summary>
    internal static bool? AcRecoveryState(object? statusString, object? statusByte)
    {
        string? text = statusString?.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            return !text.Contains("OFF", StringComparison.OrdinalIgnoreCase);

        string? hex = statusByte?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
            if (int.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int value))
                return value != 0;
        }

        return null;
    }

    internal static bool HasLogoLightingRegistryLayout(
        IEnumerable<string> valueNames,
        IEnumerable<string> subKeyNames) =>
        valueNames.Any(name => name.Contains("Lightbar_logo_", StringComparison.OrdinalIgnoreCase)) ||
        subKeyNames.Any(name =>
            (name.Contains("lightbar", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("lighbar", StringComparison.OrdinalIgnoreCase)) &&
            name.Contains("logo", StringComparison.OrdinalIgnoreCase));

    static bool DetectLogoLightingRegistry()
    {
        foreach (string path in RgbKeyboardPaths)
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? key = baseKey.OpenSubKey(path);
                    if (key is not null && HasLogoLightingRegistryLayout(key.GetValueNames(), key.GetSubKeyNames()))
                        return true;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Cannot inspect Logo lighting registry at {path} ({view}): {ex.Message}");
                }
            }
        }
        return false;
    }

    static void ReadMachineIdentity(IDictionary<string, object?> identity)
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (bios is null) return;
        foreach (string name in new[]
        {
            "SystemManufacturer", "SystemProductName", "SystemFamily", "SystemSKU",
            "BaseBoardManufacturer", "BaseBoardProduct", "BIOSVendor", "BIOSVersion",
        })
        {
            object? value = bios.GetValue(name);
            if (value is not null) identity[name] = value;
        }
    }

    static bool IsMechrevoName(string value) =>
        value.Contains("MECHREVO", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("MECH REV", StringComparison.OrdinalIgnoreCase);
}
