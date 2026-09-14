namespace MechrevoLite.Hardware;

internal enum DeviceFeature
{
    KeyboardLighting,
    LightbarLighting,
    LogoLighting,
    DisplayRefresh,
    ColorCalibration,
    LocalDimming,
    LcdOverdrive,
    LiquidCooling,
    FanBoost,
    FanSettings,
    FanRespective,
    GpuOverclock,
    DgpuDirect,
    IgpuOnly,
    GpuHotSwap,
    TurboMode,
    TurboSubMode,
    CustomPerformance,
    ScreenBrightness,
}

internal enum FeatureAvailability
{
    Unknown,
    Unsupported,
    Supported,
}

internal enum FeatureEvidence
{
    None,
    OfficialRegistry,
    RuntimeStatus,
    AdjustableRange,
    DirectDriver,
}

internal readonly record struct FeatureSupport(
    FeatureAvailability Availability,
    FeatureEvidence Evidence)
{
    public bool IsSupported => Availability == FeatureAvailability.Supported;
    public bool IsKnown => Availability != FeatureAvailability.Unknown;
}

internal sealed class DeviceCapabilitySnapshot
{
    static readonly IReadOnlyDictionary<DeviceFeature, FeatureSupport> EmptyFeatures =
        new Dictionary<DeviceFeature, FeatureSupport>();

    readonly IReadOnlyDictionary<DeviceFeature, FeatureSupport> _features;

    public static DeviceCapabilitySnapshot Empty { get; } = new(EmptyFeatures);

    public DeviceCapabilitySnapshot(IReadOnlyDictionary<DeviceFeature, FeatureSupport>? features = null)
    {
        _features = features is null || features.Count == 0
            ? EmptyFeatures
            : new Dictionary<DeviceFeature, FeatureSupport>(features);
        Fingerprint = CreateFingerprint(includeEvidence: true);
        LayoutFingerprint = CreateFingerprint(includeEvidence: false);
    }

    public string Fingerprint { get; }
    public string LayoutFingerprint { get; }

    public FeatureSupport Get(DeviceFeature feature) =>
        _features.TryGetValue(feature, out FeatureSupport support)
            ? support
            : default;

    public bool IsSupported(DeviceFeature feature) => Get(feature).IsSupported;

    public DeviceCapabilitySnapshot With(DeviceFeature feature, FeatureSupport support)
    {
        var features = new Dictionary<DeviceFeature, FeatureSupport>(_features)
        {
            [feature] = support,
        };
        return new DeviceCapabilitySnapshot(features);
    }

    string CreateFingerprint(bool includeEvidence) => string.Join(
        '|',
        Enum.GetValues<DeviceFeature>().Select(feature =>
        {
            FeatureSupport support = Get(feature);
            return includeEvidence
                ? $"{(int)support.Availability}:{(int)support.Evidence}"
                : ((int)support.Availability).ToString();
        }));
}
