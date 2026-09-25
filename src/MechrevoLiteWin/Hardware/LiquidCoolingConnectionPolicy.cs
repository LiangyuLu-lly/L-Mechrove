namespace MechrevoLite.Hardware;

public enum LiquidCoolingControlRoute
{
    None,
    BluetoothObserved,
    Gcu,
    DirectBle,
}

public readonly record struct SystemBluetoothConnectionObservation(
    bool IsConnected,
    string Name,
    string Address,
    bool MatchedKnownAddress);

internal static class LiquidCoolingConnectionPolicy
{
    internal static LiquidCoolingControlRoute ResolveRoute(
        bool directBleConnected,
        bool gcuControllable,
        bool bluetoothObserved)
    {
        if (directBleConnected) return LiquidCoolingControlRoute.DirectBle;
        if (gcuControllable) return LiquidCoolingControlRoute.Gcu;
        return bluetoothObserved
            ? LiquidCoolingControlRoute.BluetoothObserved
            : LiquidCoolingControlRoute.None;
    }

    /// <summary>
    /// 本应用替代官方控制中心：GCU 通道拿不到控制权时必须自动回落到我们自己的直连，
    /// 不得因为官方 GCU/服务在线、已报告状态，或 Windows 观测到蓝牙连接而阻止回落。
    /// 唯一不必回落的情形是我们已经持有直连。
    /// </summary>
    internal static bool ShouldUseAutomaticDirectFallback(bool directBleConnected) => !directBleConnected;

    /// <summary>
    /// GCU 正在扫/连水冷时占着蓝牙外设。这时直连必失败，必须等 GCU 结束，不能抢无线电。
    /// </summary>
    internal static bool ShouldSkipDirectFallbackBecauseGcuHoldsRadio(string? gcuConnectString) =>
        gcuConnectString is "Scanning" or "Connecting" or "IsConnectable";

    internal static bool ShouldRetryGcuConnection(
        bool hardwareConnected,
        bool serviceAvailable,
        LiquidCoolingControlRoute route,
        bool actionSupportReported,
        bool actionSupported,
        int attempts,
        long sinceLastAttemptMs) =>
        hardwareConnected && serviceAvailable &&
        (route is LiquidCoolingControlRoute.None or LiquidCoolingControlRoute.BluetoothObserved) &&
        !(actionSupportReported && !actionSupported) &&
        attempts < 5 && sinceLastAttemptMs >= 8000;

    internal static bool ShouldRestoreSavedGcuLighting(
        bool directBleConnected,
        bool gcuControllable,
        bool gcuStatusFresh,
        string? savedProfile) =>
        !directBleConnected &&
        gcuControllable &&
        gcuStatusFresh &&
        !string.IsNullOrWhiteSpace(savedProfile);
}
