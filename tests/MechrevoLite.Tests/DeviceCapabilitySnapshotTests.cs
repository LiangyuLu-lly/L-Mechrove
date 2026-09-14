using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class DeviceCapabilitySnapshotTests
{
    [Fact]
    public void Catalog_ContainsExactlyTheThreeReviewedConsoleVariants()
    {
        Assert.Equal(
            new[] { "5.17.49.19", "5.17.51.34", "5.56.60.26" },
            OfficialConsoleCatalog.Packages.Select(package => package.Version));
        Assert.Equal(24, OfficialConsoleCatalog.ProjectIds.Count);
    }

    [Fact]
    public void ThreePlusOneRgbLayout_IsNotMistakenForLogoLayout()
    {
        OfficialRgbLayout layout = OfficialConsoleCatalog.DetectRgbLayout(
            new[] { "MEZone_3p1nd_101", "MEZone_3p1nd_102" },
            Array.Empty<string>());

        Assert.Equal(OfficialRgbLayout.ThreePlusOne, layout);
    }

    [Fact]
    public void SnapshotFingerprint_ChangesWhenAvailabilityChanges()
    {
        DeviceCapabilitySnapshot before = DeviceCapabilitySnapshot.Empty;
        DeviceCapabilitySnapshot after = before.With(
            DeviceFeature.KeyboardLighting,
            new FeatureSupport(FeatureAvailability.Supported, FeatureEvidence.RuntimeStatus));

        Assert.NotEqual(before.LayoutFingerprint, after.LayoutFingerprint);
    }
}
