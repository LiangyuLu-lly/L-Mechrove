namespace MechrevoLite.Hardware;

internal enum OfficialRgbLayout
{
    Unknown,
    Baseline,
    ThreePlusOne,
    LogoCapable,
}

internal readonly record struct OfficialConsolePackage(
    string Version,
    int CustomizeTarget,
    OfficialRgbLayout RgbLayout);

internal static class OfficialConsoleCatalog
{
    static readonly string[] KnownProjectIds =
    {
        "PH4AQE3", "PH4AQxx", "PH4ARxx", "PH4AUxf", "PH4AUxx", "PH4AXxx",
        "PH4PGx1", "PH4PGx2", "PH4PRxx", "PH4PUxx", "PH4TQx1", "PH4TRX1",
        "PH4TUX1", "PH6AQxx", "PH6ARxx", "PH6PG0x", "PH6PG0x150W",
        "PH6PG3x", "PH6PG3x150W", "PH6PG7x", "PH6PG7x150W", "PH6PGEx",
        "PH6PRxx", "PH6TRX1",
    };

    public static IReadOnlyList<OfficialConsolePackage> Packages { get; } = new OfficialConsolePackage[]
    {
        new OfficialConsolePackage("5.17.49.19", 17, OfficialRgbLayout.Baseline),
        new OfficialConsolePackage("5.17.51.34", 17, OfficialRgbLayout.ThreePlusOne),
        new OfficialConsolePackage("5.56.60.26", 56, OfficialRgbLayout.LogoCapable),
    };

    public static IReadOnlyList<string> ProjectIds => KnownProjectIds;

    public static OfficialRgbLayout DetectRgbLayout(
        IEnumerable<string> valueNames,
        IEnumerable<string> subKeyNames)
    {
        bool logo = valueNames.Any(name =>
                name.Contains("Lightbar_logo_", StringComparison.OrdinalIgnoreCase)) ||
            subKeyNames.Any(name =>
                name.Contains("logo", StringComparison.OrdinalIgnoreCase) &&
                (name.Contains("lightbar", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("lighbar", StringComparison.OrdinalIgnoreCase)));
        if (logo) return OfficialRgbLayout.LogoCapable;

        if (subKeyNames.Any(name => name.Contains("MEZone_3p1nd_", StringComparison.OrdinalIgnoreCase)) ||
            valueNames.Any(name => name.Contains("MEZone_3p1nd_", StringComparison.OrdinalIgnoreCase)))
            return OfficialRgbLayout.ThreePlusOne;

        return subKeyNames.Any(name => name.Contains("MEZone", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("lightbar", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("lighbar", StringComparison.OrdinalIgnoreCase))
            ? OfficialRgbLayout.Baseline
            : OfficialRgbLayout.Unknown;
    }
}
