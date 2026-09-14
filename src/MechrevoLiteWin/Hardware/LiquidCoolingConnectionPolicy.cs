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

    internal static bool ShouldUseAutomaticDirectFallback(
        bool gcuAvailable,
        bool gcuReportedStatus,
        bool bluetoothObserved) => !gcuAvailable && !gcuReportedStatus && !bluetoothObserved;

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
