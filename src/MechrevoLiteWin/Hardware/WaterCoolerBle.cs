using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>
/// 蓝牙水冷箱 BLE 直连（Tongfang/XMG Oasis LCT21001/LCT22002，Nordic UART Service）。
/// 协议来自社区 watercooler-manager 逆向，8 字节帧 [0xFE, cmd, on, p1, p2, p3, p4, 0xEF]：
///   风扇 FE 1B [on] [duty] 0 0 0 EF —— duty = 0-100 百分比
///   水泵 FE 1C 01 [duty] [电压] 0 0 EF —— duty=0-100%，11V=0x00 12V=0x01 7V=0x02 8V=0x03
///   头灯 FE 1E 01 R G B [mode] EF —— 0=静态 1=呼吸 2=多彩 3=彩色呼吸
///   风扇灯 FE 33 01 R G B [mode] EF —— LCT22002 专有，4=旋转色 5=彩虹
/// 与 GCU 的 BT_LC 通道互斥（外设单连接）：调用方必须先确认 GCU 未持有设备，不能为了直连主动断开 GCU。
/// </summary>
public class WaterCoolerBle : IDisposable
{
    public static readonly Guid ServiceUuid = new("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid CharTxUuid = new("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid CharRxUuid = new("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    public const byte CmdFan = 0x1B, CmdPump = 0x1C, CmdHeadRgb = 0x1E, CmdFanRgb = 0x33;
    public const byte PumpV11 = 0x00, PumpV12 = 0x01, PumpV7 = 0x02, PumpV8 = 0x03;

    public const byte RgbStatic = 0x00, RgbBreath = 0x01, RgbColorful = 0x02, RgbBreatheColor = 0x03;
    public const byte FanRgbRotate = 0x04, FanRgbRainbow = 0x05;
    public const int ProfileUnset = -2, ProfileAutomatic = -1;

    /// <summary>手动档最高索引（与界面档位表一致）。厂商协议没有 100%：泵 3 档（45/60/90）、风扇 4 档（40/50/60/90）。</summary>
    public const int TopPumpProfile = 2, TopFanProfile = 3;
    public const string LightCyanStatic = "cyan_static", LightCyanBreath = "cyan_breath",
        LightColorful = "colorful", LightColorfulBreath = "colorful_breath",
        LightFanRotate = "fan_rotate", LightFanRainbow = "fan_rainbow", LightOff = "off",
        LightCustomStatic = "custom_static", LightCustomBreath = "custom_breath";

    BluetoothLEAdvertisementWatcher? _watcher;
    BluetoothLEDevice? _device;
    GattDeviceService? _service;
    GattCharacteristic? _txChar;
    GattCharacteristic? _rxChar;
    GattSession? _session;
    TaskCompletionSource<string>? _firmwareReply;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _disposeCts = new();
    readonly object _lock = new();
    int _disposed;
    int _lastAutomaticPump = -1, _lastAutomaticFan = -1;
    DateTime _lastSystemConnectionProbeUtc = DateTime.MinValue;
    SystemBluetoothConnectionObservation _lastSystemConnectionObservation;
    DateTime _connectedAtUtc;
    int _consecutiveFlowFaults;
    int _flowNormalSamples;

    /// <summary>扫描发现的 BLE 设备（名称、地址、信号强度）。LCT/水冷相关设备排前。</summary>
    public List<(string Name, ulong Address, short Rssi)> Devices { get; } = new();

    public bool IsConnected { get; private set; }
    public string ConnectedName { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";
    public bool? IsMeterNormal { get; private set; }
    public bool IsFlowMeterReady => IsConnected && HasReachedFlowMeterReady(_flowNormalSamples, DateTime.UtcNow - _connectedAtUtc);
    public bool IsFlowFaultConfirmed => HasConfirmedFlowFault(
        IsConnected, IsMeterNormal, _consecutiveFlowFaults, DateTime.UtcNow - _connectedAtUtc);
    public bool SupportsFanLed => IsFanLedModel(ConnectedName, FirmwareVersion);
    // 旧配置里的 100%（泵 3 / 风扇 4）已移出档位表，读回时归为未设置，
    // 避免「界面上没有这一档、设备却按它跑」。
    public int SavedPumpProfile => NormalizeProfile(AppConfig.Get("lc_pump_profile", ProfileUnset), TopPumpProfile);
    public int SavedFanProfile => NormalizeProfile(AppConfig.Get("lc_fan_profile", ProfileUnset), TopFanProfile);
    public string SavedLightProfile => AppConfig.GetString("lc_light_profile", "") ?? "";
    public Color SavedLightColor => Color.FromArgb(AppConfig.Get("lc_light_color", Color.FromArgb(0, 255, 255).ToArgb()));
    public bool HasSavedDevice => TryGetSavedDevice(out _, out _);
    public event Action<bool>? ConnectionChanged;

    internal static bool IsFanLedModel(string? deviceName, string? firmwareVersion) =>
        (deviceName?.Contains("LCT22002", StringComparison.OrdinalIgnoreCase) == true) ||
        (firmwareVersion?.Contains("LCT22002", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Checks Windows' connected-device inventory without opening a GATT session. This is only
    /// evidence that the water cooler is connected to Windows; it does not imply this process can control it.
    /// </summary>
    public async Task<SystemBluetoothConnectionObservation> ProbeSystemConnectionAsync(
        IEnumerable<string>? knownAddresses = null, bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastSystemConnectionProbeUtc < TimeSpan.FromSeconds(3))
            return _lastSystemConnectionObservation;

        try
        {
            var known = new List<string>();
            if (knownAddresses is not null) known.AddRange(knownAddresses);
            if (TryGetSavedDevice(out ulong savedAddress, out _)) known.Add(savedAddress.ToString("X12", CultureInfo.InvariantCulture));

            string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(selector,
                new[] { "System.Devices.Aep.DeviceAddress" });
            foreach (DeviceInformation device in devices)
            {
                string name = device.Name ?? "";
                string address = device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? rawAddress)
                    ? rawAddress?.ToString() ?? ""
                    : "";
                bool addressMatched = IsKnownWaterCoolerAddress(address, known);
                bool lctName = name.Contains("LCT", StringComparison.OrdinalIgnoreCase);
                if (!addressMatched && !lctName) continue;

                _lastSystemConnectionObservation = new SystemBluetoothConnectionObservation(
                    true, name, address, addressMatched);
                _lastSystemConnectionProbeUtc = DateTime.UtcNow;
                return _lastSystemConnectionObservation;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("lc-system-bluetooth", "BLE connected-device probe failed: " + ex.Message, 10000);
        }

        _lastSystemConnectionObservation = default;
        _lastSystemConnectionProbeUtc = DateTime.UtcNow;
        return _lastSystemConnectionObservation;
    }

    internal static bool IsKnownWaterCoolerAddress(string? address, IEnumerable<string> knownAddresses)
    {
        string normalized = NormalizeBluetoothAddress(address);
        if (normalized.Length != 12) return false;
        return knownAddresses.Any(known => string.Equals(
            normalized, NormalizeBluetoothAddress(known), StringComparison.OrdinalIgnoreCase));
    }

    static string NormalizeBluetoothAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        string normalized = new(address.Where(Uri.IsHexDigit).ToArray());
        return normalized.Length == 12 ? normalized.ToUpperInvariant() : "";
    }

    public void StartScan()
    {
        if (_watcher is not null) return;
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            SignalStrengthFilter = { InRangeThresholdInDBm = -95 },
        };
        watcher.Received += OnReceived;
        watcher.Stopped += OnWatcherStopped;
        _watcher = watcher;
        try { watcher.Start(); }
        catch (Exception ex) { Logger.WriteLine("BLE scan start fail: " + ex.Message); _watcher = null; }
    }

    public void StopScan()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try { watcher.Stop(); } catch { }
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnWatcherStopped;
    }

    void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e) =>
        Logger.WriteLine("BLE watcher stopped: " + e.Error);

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName;
        string display = string.IsNullOrWhiteSpace(name) ? "(未命名设备)" : name;
        lock (_lock)
        {
            int idx = Devices.FindIndex(d => d.Address == args.BluetoothAddress);
            if (idx < 0)
            {
                Devices.Add((display, args.BluetoothAddress, args.RawSignalStrengthInDBm));
            }
            else if (Devices[idx].Name != display || Devices[idx].Rssi != args.RawSignalStrengthInDBm)
            {
                Devices[idx] = (display, args.BluetoothAddress, args.RawSignalStrengthInDBm);
            }
        }
    }

    /// <summary>
    /// 自动连接流程（仿开源 watercooler-manager）：扫描 ≤12s → 按名称含 LCT 过滤水冷箱
    /// → 信号最强优先 → 直连。成功后自动停止扫描。
    /// </summary>
    public async Task<bool> AutoConnectAsync()
    {
        if (TryGetSavedDevice(out ulong savedAddress, out string savedName))
        {
            Logger.WriteLine($"BLE 自动连接：先尝试上次设备 {savedName} ({savedAddress:X12})");
            if (await ConnectAsync(savedAddress, savedName)) return true;
        }

        StartScan();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            (string Name, ulong Address, short Rssi)? best = null;
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    best = Devices
                        .Where(d => d.Name.Contains("LCT", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(d => d.Rssi)
                        .Select(d => ((string, ulong, short)?)(d.Name, d.Address, d.Rssi))
                        .FirstOrDefault();
                }
                if (best is not null) break;
                await Task.Delay(300);
            }
            if (best is null)
            {
                Logger.WriteLine("BLE 自动连接：12s 内未发现名称含 LCT 的设备");
                return false;
            }
            var (name, addr, rssi) = best.Value;
            Logger.WriteLine($"BLE 自动连接：发现 {name} ({addr:X12}) rssi={rssi}");
            return await ConnectAsync(addr, name);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("BLE 自动连接失败: " + ex.Message);
            return false;
        }
        finally
        {
            StopScan();
        }
    }

    /// <summary>按地址直连。</summary>
    public async Task<bool> ConnectAsync(ulong address, string name)
    {
        try
        {
            Disconnect();   // 清理旧连接
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null) { Logger.WriteLine("BLE FromBluetoothAddressAsync: null"); return false; }
            // Uncached 强制重新发现服务（未配对设备用 Cached 拿不到）
            var svcRes = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcRes.Status != GattCommunicationStatus.Success)
            {
                Logger.WriteLine($"BLE GetGattServicesAsync: {svcRes.Status}");
                try { device.Dispose(); } catch { }
                return false;
            }
            var service = svcRes.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);
            if (service is null)
            {
                Logger.WriteLine("BLE NUS service not found");
                try { device.Dispose(); } catch { }
                return false;
            }
            var txRes = await service.GetCharacteristicsForUuidAsync(CharTxUuid, BluetoothCacheMode.Uncached);
            if (txRes.Status != GattCommunicationStatus.Success)
            {
                Logger.WriteLine($"BLE Get TX characteristic: {txRes.Status}");
                try { service.Dispose(); } catch { }
                try { device.Dispose(); } catch { }
                return false;
            }
            var tx = txRes.Characteristics.FirstOrDefault();
            if (tx is null)
            {
                Logger.WriteLine("BLE TX characteristic not found");
                try { service.Dispose(); } catch { }
                try { device.Dispose(); } catch { }
                return false;
            }
            _device = device;
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _service = service;
            _txChar = tx;
            try
            {
                _session = await GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(service.DeviceId));
                if (_session is not null) _session.MaintainConnection = true;
            }
            catch (Exception ex) { Logger.WriteLine("BLE maintain-connection unavailable: " + ex.Message); }
            IsConnected = true;
            ConnectedName = name;
            FirmwareVersion = "";
            IsMeterNormal = null;
            _connectedAtUtc = DateTime.UtcNow;
            _consecutiveFlowFaults = 0;
            _flowNormalSamples = 0;

            var rxRes = await service.GetCharacteristicsForUuidAsync(CharRxUuid, BluetoothCacheMode.Uncached);
            if (rxRes.Status == GattCommunicationStatus.Success && rxRes.Characteristics.FirstOrDefault() is { } rx)
            {
                _rxChar = rx;
                rx.ValueChanged += OnRxValueChanged;
                var notify = await rx.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (notify != GattCommunicationStatus.Success)
                    Logger.WriteLine($"BLE RX notify enable failed: {notify}");
            }
            else
            {
                Logger.WriteLine($"BLE RX characteristic unavailable: {rxRes.Status}");
            }

            // Official clients query "sw" after enabling notifications. Some older firmware
            // still accepts control frames without replying, so timeout is logged but is not fatal.
            var firmwareReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firmwareReply = firmwareReply;
            if (await WriteRawAsync(Encoding.ASCII.GetBytes("sw"), "firmware query") && _rxChar is not null)
            {
                Task completed = await Task.WhenAny(firmwareReply.Task, Task.Delay(1800));
                if (completed != firmwareReply.Task)
                    Logger.WriteLine("BLE firmware query timed out; continuing in compatibility mode");
            }
            _firmwareReply = null;
            AppConfig.Set("lc_last_address", address.ToString("X12", CultureInfo.InvariantCulture));
            AppConfig.Set("lc_last_name", name);
            _lastAutomaticPump = _lastAutomaticFan = -1;
            Logger.WriteLine($"BLE connected: {name} ({address:X12}) fw={FirmwareVersion} fanLed={SupportsFanLed}");
            ConnectionChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("BLE connect fail: " + ex.Message);
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        var was = IsConnected;
        _txChar = null;
        if (_rxChar is not null) _rxChar.ValueChanged -= OnRxValueChanged;
        _rxChar = null;
        _firmwareReply?.TrySetCanceled();
        _firmwareReply = null;
        try { _service?.Dispose(); } catch { }
        _service = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try { _device?.Dispose(); } catch { }
        _device = null;
        IsConnected = false;
        ConnectedName = "";
        FirmwareVersion = "";
        IsMeterNormal = null;
        _connectedAtUtc = default;
        _consecutiveFlowFaults = 0;
        _flowNormalSamples = 0;
        _lastAutomaticPump = _lastAutomaticFan = -1;
        if (was)
        {
            Logger.WriteLine("BLE disconnected");
            ConnectionChanged?.Invoke(false);
        }
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        _txChar = null;
        IsConnected = false;
        IsMeterNormal = null;
        _connectedAtUtc = default;
        _consecutiveFlowFaults = 0;
        _flowNormalSamples = 0;
        Logger.WriteLine($"BLE physical connection lost: {ConnectedName}");
        ConnectionChanged?.Invoke(false);
    }

    /// <summary>风扇占空比百分比（0=停转，1-100=运行）。</summary>
    public Task<bool> WriteFan(int dutyPercent) => WriteFrame(BuildFanFrame(dutyPercent));

    /// <summary>水泵占空比百分比与电压档位（两者必须一起设置）。</summary>
    public Task<bool> WritePump(int dutyPercent, byte voltage) => WriteFrame(BuildPumpFrame(dutyPercent, voltage));

    public Task<bool> WriteHeadRgb(byte mode, byte r, byte g, byte b) =>
        WriteFrame(BuildRgbFrame(CmdHeadRgb, true, mode, r, g, b));

    public Task<bool> WriteFanRgb(byte mode, byte r = 0, byte g = 0, byte b = 0)
    {
        if (!SupportsFanLed)
        {
            Logger.WriteLine($"BLE fan LED mode {mode} ignored: {ConnectedName} does not expose LCT22002 fan lighting");
            return Task.FromResult(false);
        }
        return WriteFrame(BuildRgbFrame(CmdFanRgb, true, mode, r, g, b));
    }

    public async Task<bool> TurnOffLights()
    {
        bool headOff = await WriteFrame(BuildRgbFrame(CmdHeadRgb, false, RgbStatic, 0, 0, 0));
        bool fanOff = !SupportsFanLed || await WriteFrame(BuildRgbFrame(CmdFanRgb, false, 0, 0, 0, 0));
        return headOff && fanOff;
    }

    public async Task<bool> ApplyPumpProfileAsync(int profile, int temperatureC, bool persist = true)
    {
        if (profile is < ProfileAutomatic or > 3) return false;
        int effective;
        if (profile == ProfileAutomatic)
        {
            int? automatic = SelectAutomaticPumpProfileIfUsable(temperatureC, _lastAutomaticPump);
            // 温度读不到/已过期：保持泵当前档位，绝不拿 0°C 去挑最低档；自动意图仍要落配置。
            if (automatic is null)
            {
                if (persist) AppConfig.Set("lc_pump_profile", profile);
                return true;
            }
            effective = automatic.Value;
        }
        else
        {
            effective = profile;
        }
        if (profile == ProfileAutomatic && effective == _lastAutomaticPump)
        {
            if (persist) AppConfig.Set("lc_pump_profile", profile);
            return true;
        }
        var setting = PumpSetting(effective);
        bool ok = await WritePump(setting.Duty, setting.Voltage);
        if (ok)
        {
            _lastAutomaticPump = profile == ProfileAutomatic ? effective : -1;
            if (persist) AppConfig.Set("lc_pump_profile", profile);
        }
        return ok;
    }

    public async Task<bool> ApplyFanProfileAsync(int profile, int temperatureC, bool persist = true)
    {
        if (profile is < ProfileAutomatic or > 4) return false;
        int effective;
        if (profile == ProfileAutomatic)
        {
            int? automatic = SelectAutomaticFanProfileIfUsable(temperatureC, _lastAutomaticFan);
            // 同上：温度不可用时保持风扇当前档位。
            if (automatic is null)
            {
                if (persist) AppConfig.Set("lc_fan_profile", profile);
                return true;
            }
            effective = automatic.Value;
        }
        else
        {
            effective = profile;
        }
        if (profile == ProfileAutomatic && effective == _lastAutomaticFan)
        {
            if (persist) AppConfig.Set("lc_fan_profile", profile);
            return true;
        }
        bool ok = await WriteFan(FanDuty(effective));
        if (ok)
        {
            _lastAutomaticFan = profile == ProfileAutomatic ? effective : -1;
            if (persist) AppConfig.Set("lc_fan_profile", profile);
        }
        return ok;
    }

    public Task<bool> ApplyLightProfileAsync(string profile, bool persist = true) =>
        ApplyLightProfileAsync(profile, SavedLightColor, persist);

    public async Task<bool> ApplyLightProfileAsync(string profile, Color color, bool persist = true)
    {
        Color effectiveColor = color.IsEmpty ? SavedLightColor : color;
        bool ok = profile switch
        {
            LightCyanStatic => await WriteHeadRgb(RgbStatic, 0, 255, 255),
            LightCyanBreath => await WriteHeadRgb(RgbBreath, 0, 255, 255),
            LightCustomStatic => await WriteHeadRgb(RgbStatic, effectiveColor.R, effectiveColor.G, effectiveColor.B),
            LightCustomBreath => await WriteHeadRgb(RgbBreath, effectiveColor.R, effectiveColor.G, effectiveColor.B),
            LightColorful => await WriteHeadRgb(RgbColorful, 0, 0, 0),
            LightColorfulBreath => await WriteHeadRgb(RgbBreatheColor, 0, 0, 0),
            LightFanRotate => await WriteFanRgb(FanRgbRotate),
            LightFanRainbow => await WriteFanRgb(FanRgbRainbow),
            LightOff => await TurnOffLights(),
            _ => false,
        };
        if (ok && persist)
        {
            AppConfig.Set("lc_light_profile", profile);
            if (profile is LightCustomStatic or LightCustomBreath)
                AppConfig.Set("lc_light_color", effectiveColor.ToArgb());
        }
        return ok;
    }

    public async Task RestoreSavedSettingsAsync(int temperatureC)
    {
        int pump = SavedPumpProfile;
        int fan = SavedFanProfile;
        if (pump != ProfileUnset) await ApplyPumpProfileAsync(pump, temperatureC, persist: false);
        if (fan != ProfileUnset) await ApplyFanProfileAsync(fan, temperatureC, persist: false);
        if (!string.IsNullOrWhiteSpace(SavedLightProfile))
            await ApplyLightProfileAsync(SavedLightProfile, persist: false);
    }

    public async Task WaitForStartupStabilizationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (IsConnected && !IsFlowMeterReady && DateTime.UtcNow < deadline)
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAutomaticCoolingAsync(int temperatureC)
    {
        if (!IsConnected || temperatureC <= 0) return;
        if (SavedPumpProfile == ProfileAutomatic)
            await ApplyPumpProfileAsync(ProfileAutomatic, temperatureC, persist: false);
        if (SavedFanProfile == ProfileAutomatic)
            await ApplyFanProfileAsync(ProfileAutomatic, temperatureC, persist: false);
    }

    internal static int SelectAutomaticPumpProfile(int temperatureC, int current = -1) =>
        SelectAutomaticProfile(temperatureC, current, [int.MinValue, 46, 61, 76]);

    /// <summary>自动泵档；温度读不到/已过期时返回 null——保持现状，绝不按 0°C 挑最低档。</summary>
    internal static int? SelectAutomaticPumpProfileIfUsable(int temperatureC, int current = -1) =>
        LiquidCoolingAutoPolicy.TemperatureUsable(temperatureC)
            ? SelectAutomaticPumpProfile(temperatureC, current)
            : null;

    /// <summary>自动风扇档；温度不可用时返回 null。</summary>
    internal static int? SelectAutomaticFanProfileIfUsable(int temperatureC, int current = -1) =>
        LiquidCoolingAutoPolicy.TemperatureUsable(temperatureC)
            ? SelectAutomaticFanProfile(temperatureC, current)
            : null;

    /// <summary>
    /// 自动档是否已经落在目标档位（是则不必再下发）。必须同时看客户端记忆与厂商回读：
    /// 记忆只记录自动档自己写过的值，手动档（或官方控制台）改写设备后它就过期了，
    /// 只比记忆会让「自动」永远不再下发——用户看到的就是切回自动没反应。
    /// </summary>
    internal static bool AutomaticGearAlreadyApplied(int targetGear, int rememberedGear, int reportedGear) =>
        targetGear == rememberedGear && reportedGear == targetGear;

    internal static int SelectAutomaticFanProfile(int temperatureC, int current = -1) =>
        SelectAutomaticProfile(temperatureC, current, [int.MinValue, 43, 56, 69, 81]);

    static int SelectAutomaticProfile(int temperatureC, int current, int[] lowerBounds)
    {
        int candidate = 0;
        for (int i = 1; i < lowerBounds.Length; i++)
            if (temperatureC >= lowerBounds[i]) candidate = i;
        if (current < 0 || current >= lowerBounds.Length || candidate == current) return candidate;

        // Two degrees to rise and three degrees to fall prevents a sensor hovering
        // on a boundary from alternating BLE commands every status tick.
        if (candidate > current && temperatureC < lowerBounds[candidate] + 2) return current;
        if (candidate < current && temperatureC > lowerBounds[current] - 3) return current;
        return candidate;
    }

    internal static (int Duty, byte Voltage) PumpSetting(int profile) => profile switch
    {
        0 => (45, PumpV7),
        1 => (60, PumpV8),
        2 => (90, PumpV11),
        3 => (100, PumpV12),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static int FanDuty(int profile) => profile switch
    {
        0 => 40,
        1 => 50,
        2 => 60,
        3 => 90,
        4 => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static int NormalizeProfile(int profile, int maximum) =>
        profile >= ProfileAutomatic && profile <= maximum ? profile : ProfileUnset;

    internal static bool HasReachedFlowMeterReady(int normalSamples, TimeSpan connectedFor) =>
        normalSamples > 0 || connectedFor >= TimeSpan.FromSeconds(6);

    internal static bool HasConfirmedFlowFault(bool connected, bool? meterNormal, int consecutiveFaults, TimeSpan connectedFor) =>
        connected && meterNormal == false && consecutiveFaults >= 2 && connectedFor >= TimeSpan.FromSeconds(8);

    static bool TryGetSavedDevice(out ulong address, out string name)
    {
        name = AppConfig.GetString("lc_last_name", "水冷箱") ?? "水冷箱";
        return ulong.TryParse(AppConfig.GetString("lc_last_address", ""),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address != 0;
    }

    internal static byte[] BuildFanFrame(int dutyPercent)
    {
        int duty = Math.Clamp(dutyPercent, 0, 100);
        return [0xFE, CmdFan, duty > 0 ? (byte)1 : (byte)0, (byte)duty, 0, 0, 0, 0xEF];
    }

    internal static byte[] BuildPumpFrame(int dutyPercent, byte voltage)
    {
        if (voltage is not PumpV7 and not PumpV8 and not PumpV11 and not PumpV12)
            throw new ArgumentOutOfRangeException(nameof(voltage));
        int duty = Math.Clamp(dutyPercent, 0, 100);
        return [0xFE, CmdPump, duty > 0 ? (byte)1 : (byte)0, (byte)duty, voltage, 0, 0, 0xEF];
    }

    internal static byte[] BuildRgbFrame(byte command, bool on, byte mode, byte r, byte g, byte b)
    {
        if (command is not CmdHeadRgb and not CmdFanRgb)
            throw new ArgumentOutOfRangeException(nameof(command));
        return [0xFE, command, on ? (byte)1 : (byte)0, r, g, b, mode, 0xEF];
    }

    Task<bool> WriteFrame(byte[] frame) => WriteRawAsync(frame, $"command 0x{frame[1]:X2}");

    async Task<bool> WriteRawAsync(byte[] payload, string description)
    {
        bool lockTaken = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            await _writeLock.WaitAsync(_disposeCts.Token);
            lockTaken = true;
            if (Volatile.Read(ref _disposed) != 0) return false;
            var ch = _txChar;
            if (ch is null) { Logger.WriteLine($"BLE write {description}: not connected"); return false; }
            var option = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
            using var writer = new DataWriter();
            writer.WriteBytes(payload);
            var res = await ch.WriteValueWithResultAsync(writer.DetachBuffer(), option);
            if (res.Status != GattCommunicationStatus.Success)
            {
                Logger.WriteLine($"BLE write {description} failed: {res.Status} ({res.ProtocolError})");
                return false;
            }
            Logger.WriteLine($"BLE TX {description}: {Convert.ToHexString(payload)}");
            // NUS commonly exposes WriteWithoutResponse. Serializing plus a short gap prevents
            // rapid combo/menu changes from being reordered or dropped by the peripheral queue.
            await Task.Delay(80);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"BLE write {description} failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (lockTaken) _writeLock.Release();
        }
    }

    void OnRxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] payload = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(payload);
            if (payload.Length == 0) return;
            if (payload[0] == 0xFE)
            {
                if (payload.Length >= 4 && payload[1] is 0x31 or 0x32)
                {
                    IsMeterNormal = payload[3] == 2;
                    if (IsMeterNormal == true)
                    {
                        _flowNormalSamples++;
                        _consecutiveFlowFaults = 0;
                    }
                    else
                    {
                        _consecutiveFlowFaults++;
                        _flowNormalSamples = 0;
                    }
                    Logger.WriteLine($"BLE flow meter: state={payload[3]} normal={IsMeterNormal}");
                }
                return;
            }

            string firmware = Encoding.UTF8.GetString(payload).Trim('\0', '\r', '\n', ' ');
            if (firmware.Length == 0) return;
            FirmwareVersion = firmware;
            _firmwareReply?.TrySetResult(firmware);
        }
        catch (Exception ex) { Logger.WriteLine("BLE RX parse failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        StopScan();
        bool lockTaken = false;
        try
        {
            lockTaken = _writeLock.Wait(3000);
            Disconnect();
        }
        finally
        {
            if (lockTaken)
            {
                _writeLock.Release();
                _writeLock.Dispose();
            }
            else
            {
                Logger.WriteLine("BLE dispose timed out waiting for an in-flight write; lock left for safe completion.");
            }
            _disposeCts.Dispose();
        }
    }
}
