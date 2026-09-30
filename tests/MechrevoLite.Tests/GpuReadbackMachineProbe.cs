using MechrevoLite.Gpu;
using Xunit.Abstractions;

namespace MechrevoLite.Tests;

/// <summary>
/// 真机只读探针（Category=Integration，默认不跑）：读本机真实的服务档位、内屏接线、独显在位与
/// NVIDIA 首选 GPU，打印出来供人工核对。全部只读——不发 MQTT、不写驱动配置、不动设备。
/// 运行：<c>dotnet test --filter "FullyQualifiedName~GpuReadbackMachineProbe"</c>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
[Trait("Category", "Integration")]
public class GpuReadbackMachineProbe(ITestOutputHelper output)
{
    [Fact]
    public void PrintsThisMachinesGpuReadback()
    {
        Func<GpuRouteReadback>? route = GpuRouteProbe.Override;
        Func<GcuServiceTier>? tier = GcuServiceTierProbe.Override;
        Func<NvPreferredGpu>? driver = NvPreferredGpuReader.DriverOverride;
        try
        {
            GpuRouteProbe.Override = null;
            GcuServiceTierProbe.Override = null;
            NvPreferredGpuReader.DriverOverride = null;
            GcuServiceTierProbe.Invalidate();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            GpuRouteReadback readback = GpuRouteProbe.Read();
            long routeMs = watch.ElapsedMilliseconds;
            output.WriteLine($"route: {readback} ({routeMs} ms)");
            output.WriteLine($"tier: {GcuServiceTierProbe.Current()}");
            watch.Restart();
            output.WriteLine($"nv preferred gpu (DRS): {NvPreferredGpuReader.ReadFromDriver()} ({watch.ElapsedMilliseconds} ms)");
            try
            {
                using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();
                var profile = session.CurrentGlobalProfile;
                output.WriteLine(profile is null ? "DRS global profile: null" : $"DRS global profile: {profile.Name}");
                var setting = profile?.GetSetting(NvPreferredGpuReader.ShimRenderingModeId);
                output.WriteLine(setting is null
                    ? "DRS raw: setting not present in the global profile (driver default)"
                    : $"DRS raw: {setting.CurrentValue} (type {setting.CurrentValue?.GetType().Name})");
            }
            catch (Exception ex)
            {
                output.WriteLine($"DRS raw read threw {ex}");
            }
            output.WriteLine(UefiDisplayModeDiagnostics.Describe());

            Assert.True(routeMs < 500, $"route readback took {routeMs} ms");
        }
        finally
        {
            GpuRouteProbe.Override = route;
            GcuServiceTierProbe.Override = tier;
            NvPreferredGpuReader.DriverOverride = driver;
            GcuServiceTierProbe.Invalidate();
        }
    }
}
