using MechrevoLite.Gpu;
using MechrevoLite.Gpu.NVidia;
using MechrevoLite.Hardware;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MechrevoLite.Tests;

public class GpuOverclockFallbackTests
{
    [Fact]
    public void ElevatedHelperPipeSecurity_AllowsTheUnelevatedUserToken()
    {
        PipeSecurity security = ElevatedGpuOverclockApplier.CreatePipeSecurityForCurrentUser();
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;

        bool allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                         rule.IdentityReference == currentUser &&
                         (rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);

        Assert.True(allowed);
    }

    [Fact]
    public void ElevatedHelperPipe_CanBeCreatedWithTheAcl()
    {
        string pipeName = "LMechrevoGpuOc-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = ElevatedGpuOverclockApplier.CreatePipeServer(pipeName);
        Assert.False(pipe.IsConnected);
    }

    [Fact]
    public void FanStatus_ParsesGpuOverclockAliasesAndBooleanSwitch()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Fan/Status", """
            {"OverclockingSwitch":true,"GpuCoreClockOffset":325,"GpuMemoryClockOffset":750}
            """);

        Assert.True(hardware.OcSwitch);
        Assert.Equal(325, hardware.GpuCoreClockOffset);
        Assert.Equal(750, hardware.GpuMemClockOffset);
    }

    [Fact]
    public void LchwocStatus_ParsesGpuOverclockAliasesAndBooleanValues()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("LCHWOC/Status", """
            {"support":true,"enable":true,"GpuCoreClockOffset":300,"GpuMemoryClockOffset":600}
            """);

        Assert.True(hardware.LchwocSupport);
        Assert.True(hardware.LchwocEnable);
        Assert.Equal(300, hardware.GpuCoreClockOffset);
        Assert.Equal(600, hardware.GpuMemClockOffset);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 0, 0, false)]
    [InlineData(1, 1, 0, true)]
    [InlineData(1, 0, -1, true)]
    public void DriverGpuOverclockEnableNeedsANonZeroPhysicalOffset(
        int requestedSwitch, int coreOffset, int memoryOffset, bool expected) =>
        Assert.Equal(expected,
            MechrevoHw.IsGpuOverclockEnableConfirmed(requestedSwitch, coreOffset, memoryOffset));

    [Fact]
    public async Task EnablingGpuOverclockWithoutAnOffsetIsNotReportedAsApplied()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-500, 500, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        using var hardware = new MechrevoHw(
            (_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status", """
            { "GPU_CoreClockOffsetMinimumHWOC": -500, "GPU_CoreClockOffsetMaximumHWOC": 500,
              "GPU_MemoryClockOffsetMinimumHWOC": -1000, "GPU_MemoryClockOffsetMaximumHWOC": 3000,
              "GPU_CoreClockOffsetOC": 0, "GPU_MemoryClockOffsetOC": 0, "OverClockingSwitch": 0 }
            """);
        var service = new MechrevoService(hardware);

        Assert.False(await service.SetCustomDetail(new() { ["OverClockingSwitch"] = "1" }));
        Assert.False(hardware.GpuOverclockEnabled);
    }

    [Fact]
    public async Task GcuEcho_DoesNotMaskRejectedDetectedDriver()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0),
            rejectWrites: true);
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue))
            {
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"GPU_CoreClockOffsetMinimumHWOC":-200,"GPU_CoreClockOffsetMaximumHWOC":200,
                     "GpuCoreClockOffset":{{coreValue}},"OverclockingSwitch":true}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200," +
                "\"GpuCoreClockOffset\":0,\"OverclockingSwitch\":true}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "150" }));
            Assert.Equal(0, driver.CoreOffset.Current);
        }
    }

    [Fact]
    public async Task GcuExtendedMemoryRange_IsUsedWhenDriverRangeIsNarrow()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-250, 250, 0),
            new GpuClockOffsetRange(-250, 250, 0));
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("GpuMemoryClockOffsetOC", out object? memoryValue))
            {
                // 真实固件：实测范围内(-1000..2000)的值 GCU 会写进驱动。
                // 模型化这一耦合，才有资格断言「驱动读回确认」能通过。
                driver.ReportMemoryOffset(int.Parse(memoryValue.ToString()!));
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"GPU_MemoryClockOffsetMinimumHWOC":-500,"GPU_MemoryClockOffsetMaximumHWOC":500,
                     "GpuMemoryClockOffset":{{memoryValue}},"OverclockingSwitch":true}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_MemoryClockOffsetMinimumHWOC\":-500,\"GPU_MemoryClockOffsetMaximumHWOC\":500," +
                "\"GpuMemoryClockOffset\":0,\"OverclockingSwitch\":true}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            // 滑条范围来自实测硬边界，而不是 GCU 上报（本机上报 -1800/1800 只有 -1000..2000 生效）。
            Assert.Equal(-1000, hardware.GpuMemoryOffsetUserMinimum);
            Assert.Equal(2000, hardware.GpuMemoryOffsetUserMaximum);
            // 1500 超出驱动可写范围(±250)与 GCU 上报范围(±500)，但在实测范围内——GCU 通道应承担。
            Assert.True(await service.SetCustomDetail(new() { ["GpuMemoryClockOffsetOC"] = "1500" }));
            Assert.Equal(1500, driver.MemoryOffset.Current);
            Assert.Equal(1500, hardware.EffectiveGpuMemoryClockOffset);
        }
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(60, 120, false)]
    [InlineData(120, 120, true)]
    public void GpuElevationPrompt_IsSuppressedUntilCooldownExpires(
        long nowSeconds,
        long blockedUntilSeconds,
        bool expected)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(nowSeconds);
        DateTimeOffset blockedUntil = DateTimeOffset.UnixEpoch.AddSeconds(blockedUntilSeconds);

        Assert.Equal(expected,
            ElevatedGpuOverclockApplier.ShouldPromptForElevation(now, blockedUntil));
    }

    [Fact]
    public async Task GcuExtendedRange_IsUsedWhenDriverRangeIsNarrow()
    {
        var commands = new List<IDictionary<string, object>>();
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-150, 150, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action) &&
                action?.ToString() == "SET_OPERATING_MODE_DETAIL")
            {
                commands.Add(new Dictionary<string, object>(values));
                if (values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue) &&
                    int.TryParse(coreValue.ToString(), out int core))
                {
                    // 真实固件：实测范围内(0..250)的值 GCU 会写进驱动。
                    driver.ReportCoreOffset(core);
                    hardware!.HandleMessage("Fan/Status", $$"""
                        {"GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                         "GPU_CoreClockOffsetOC":{{core}},"OverClockingSwitch":1}
                        """);
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-500,\"GPU_CoreClockOffsetMaximumHWOC\":500,\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":0}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            // 滑条上限是实测硬边界 250，GCU 上报的 ±500 不再进用户范围。
            Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
            Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
            // 200 超出驱动可写范围(±150)，但 GCU 通道能真实写进驱动并被读回确认。
            Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "200" }));
            Assert.Single(commands);
            Assert.Equal(200, driver.CoreOffset.Current);
            Assert.Equal(200, hardware.EffectiveGpuCoreClockOffset);
            // 300 超出实测上限：直接拒绝，不发任何命令。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "300" }));
            Assert.Single(commands);
            Assert.Equal(200, driver.CoreOffset.Current);
        }
    }

    [Fact]
    public async Task LchwocSupport_AllowsExtendedRangeWhenDriverCannotRepresentIt()
    {
        var commands = new List<IDictionary<string, object>>();
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-245, 245, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action) &&
                action?.ToString() == "SET_OPERATING_MODE_DETAIL")
            {
                commands.Add(new Dictionary<string, object>(values));
                if (values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue) &&
                    int.TryParse(coreValue.ToString(), out int core))
                {
                    driver.ReportCoreOffset(core);
                    hardware!.HandleMessage("Fan/Status", $$"""
                        {"GPU_CoreClockOffsetMinimumHWOC":-245,"GPU_CoreClockOffsetMaximumHWOC":245,
                         "GPU_CoreClockOffsetOC":{{core}},"OverClockingSwitch":1}
                        """);
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-245,\"GPU_CoreClockOffsetMaximumHWOC\":245,\"GPU_CoreClockOffsetOC\":0}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            // 用户范围取实测边界，驱动范围(±245)与 GCU 上报都不再扩大它。
            Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
            Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
            // 250 = 实测上限，超出驱动可写范围(±245)，由 GCU 通道写入并被驱动读回确认。
            Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "250" }));
            Assert.Single(commands);
            Assert.Equal(250, driver.CoreOffset.Current);
            Assert.Equal(250, hardware.EffectiveGpuCoreClockOffset);
            // 251 越界：拒绝且不发命令。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "251" }));
            Assert.Single(commands);
            Assert.Equal(250, driver.CoreOffset.Current);
        }
    }

    [Fact]
    public async Task GcuSupported_PrefersNativeChannelWhenValueIsInFirmwareRange()
    {
        int publishCount = 0;
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "Fan/Control" || payload is not IDictionary<string, object> values ||
                !values.TryGetValue("Action", out object? action) ||
                action?.ToString() != "SET_OPERATING_MODE_DETAIL")
                return Task.CompletedTask;

            publishCount++;
            int core = values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue)
                ? int.Parse(coreValue.ToString()!)
                : hardware!.GpuCoreClockOffset;
            bool enabled = values.TryGetValue("OverClockingSwitch", out object? switchValue)
                ? switchValue?.ToString() == "1"
                : hardware!.OcSwitch;
            driver.ReportCoreOffset(core);
            hardware!.HandleMessage("Fan/Status", $$"""
                {"GPU_CoreClockOffsetMinimumHWOC":-200,"GPU_CoreClockOffsetMaximumHWOC":200,
                 "GPU_CoreClockOffsetOC":{{core}},"OverClockingSwitch":{{(enabled ? 1 : 0)}}}
                """);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200,\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":0}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SetCustomDetail(new()
            {
                ["OverClockingSwitch"] = "1",
                ["GpuCoreClockOffsetOC"] = "150",
            }));
            Assert.Equal(2, publishCount);
            Assert.Equal(150, driver.CoreOffset.Current);
            Assert.Equal(150, hardware.GpuCoreClockOffset);
            Assert.Equal(150, hardware.EffectiveGpuCoreClockOffset);
            Assert.True(hardware.GpuOverclockEnabled);
        }
    }

    [Fact]
    public async Task DriverWriteRejected_DoesNotTreatGcuStatusEchoAsPhysicalSuccess()
    {
        int publishCount = 0;
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-100, 100, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue))
            {
                publishCount++;
                // 模拟 GCU 丢弃写入却照常回显请求值的撒谎行为（本机实测到的失败形态）。
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"GPU_CoreClockOffsetMinimumHWOC":-200,"GPU_CoreClockOffsetMaximumHWOC":200,
                     "GPU_CoreClockOffsetOC":{{coreValue}},"OverClockingSwitch":1}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200,\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":1}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
            Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
            // 500 超越实测上限：直接拒绝，不发命令。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "500" }));
            Assert.Equal(0, publishCount);
            Assert.Equal(0, driver.CoreOffset.Current);
            Assert.Equal(0, hardware.GpuCoreClockOffset);
            Assert.Equal(0, hardware.EffectiveGpuCoreClockOffset);

            // 150 在范围内、超出驱动可写范围(±100)：走 GCU。GCU 回显 150，但驱动没写进去——
            // 必须报失败，且显示值取驱动真值 0。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "150" }));
            Assert.Equal(1, publishCount);
            Assert.Equal(150, hardware.GpuCoreClockOffset);
            Assert.Equal(0, driver.CoreOffset.Current);
            Assert.Equal(0, hardware.EffectiveGpuCoreClockOffset);
        }
    }

    [Fact]
    public async Task Unelevated_RequestBeyondMeasuredCap_IsRejectedWithoutPublishOrElevation()
    {
        int publishCount = 0;
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0),
            rejectWrites: true);
        var elevated = new FakeElevatedGpuOverclockApplier(driver);
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            publishCount++;
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver, elevated);

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200,\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":1}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            // 500 在 GCU 通道下永远不可能生效（实测上限 250），必须在本地就被拒绝：
            // 不发命令、不弹 UAC、不留任何假成功。
            Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
            Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "500" }));
            Assert.Equal(0, publishCount);
            Assert.Equal(0, elevated.CallCount);
            Assert.Equal(0, driver.CoreOffset.Current);
            Assert.Equal(0, hardware.EffectiveGpuCoreClockOffset);
        }
    }

    [Fact]
    public async Task ElevatedReadback_ConfirmsWhenUnelevatedReadbackIsStale()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0),
            rejectWrites: true);
        var elevated = new FakeElevatedGpuOverclockApplier(driver, updateDriver: false);
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw(
            (topic, payload) =>
            {
                if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                    values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue))
                {
                    hardware!.HandleMessage("Fan/Status", $$"""
                        {"GPU_CoreClockOffsetMinimumHWOC":-200,"GPU_CoreClockOffsetMaximumHWOC":200,
                         "GPU_CoreClockOffsetOC":{{coreValue}},"OverClockingSwitch":1}
                        """);
                }
                return Task.CompletedTask;
            },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver,
            elevated);
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200,\"GPU_CoreClockOffsetOC\":0}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "150" }));
            Assert.Equal(150, hardware.EffectiveGpuCoreClockOffset);
        }
    }

    [Fact]
    public async Task DriverWrite_StillRunsWhenGcuDoesNotEchoGpuProfile()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        using var hardware = new MechrevoHw(
            (_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status",
            "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200,\"GPU_CoreClockOffsetOC\":0}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
        var service = new MechrevoService(hardware);

        Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "150" }));
        Assert.Equal(150, driver.CoreOffset.Current);
    }

    [Fact]
    public async Task DriverRange_RemainsAvailableWhenNormalWriteNeedsElevation()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0),
            rejectWrites: true);
        using var hardware = new MechrevoHw(
            (_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = false },
            driver);
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":false,\"Enable\":false}");
        var service = new MechrevoService(hardware);

        Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "500" }));
        Assert.True(hardware.GpuCoreOffsetAdjustable);
        Assert.True(hardware.SupportsGpuOverclock);
    }

    [Fact]
    public async Task DriverWrite_SucceedsWhenGcuPersistenceIsTemporarilyUnavailable()
    {
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        using var hardware = new MechrevoHw(
            null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status",
            "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
        var service = new MechrevoService(hardware);

        Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "150" }));
        Assert.Equal(150, driver.CoreOffset.Current);
    }

    [Fact]
    public async Task DriverBackend_ExtendsNarrowGcuRangeToFiveHundredWithoutPublishingMqtt()
    {
        int publishCount = 0;
        var driver = new FakeGpuOverclockControl(
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-1000, 3000, 0));
        using var hardware = new MechrevoHw(
            (_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status",
            "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200," +
            "\"GPU_MemoryClockOffsetMinimumHWOC\":-200,\"GPU_MemoryClockOffsetMaximumHWOC\":200}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":false,\"Enable\":false}");
        var service = new MechrevoService(hardware);

        Assert.True(hardware.EnsureDirectGpuOverclock());
        Assert.True(hardware.SupportsGpuOverclock);
        Assert.Equal(-500, hardware.GpuCoreOffsetUserMinimum);
        Assert.Equal(500, hardware.GpuCoreOffsetUserMaximum);
        Assert.Equal(-1000, hardware.GpuMemoryOffsetUserMinimum);
        Assert.Equal(3000, hardware.GpuMemoryOffsetUserMaximum);

        Assert.True(await service.SetCustomDetail(new()
        {
            ["OverClockingSwitch"] = "1",
            ["GpuCoreClockOffsetOC"] = "500",
            ["GpuMemoryClockOffsetOC"] = "1000",
        }));
        Assert.Equal(500, driver.CoreOffset.Current);
        Assert.Equal(1000, driver.MemoryOffset.Current);
        Assert.True(hardware.GpuOverclockEnabled);
        Assert.Equal(0, publishCount);

        Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "501" }));
        Assert.Equal(500, driver.CoreOffset.Current);
        Assert.Equal(0, publishCount);

        Assert.True(await service.SetCustomDetail(new() { ["OverClockingSwitch"] = "0" }));
        Assert.Equal(0, driver.CoreOffset.Current);
        Assert.Equal(0, driver.MemoryOffset.Current);
        Assert.False(hardware.GpuOverclockEnabled);
        Assert.Equal(0, publishCount);
    }

    [Fact]
    public void NvidiaDriver_ReadOnlyProbeReportsEditableClockRange()
    {
        if (Environment.GetEnvironmentVariable("LMECHREVO_RUN_GPU_OC_INTEGRATION") != "1")
            return;

        using IGpuOverclockControl? control = NvidiaGpuControl.TryCreateOverclockControl();
        Assert.NotNull(control);
        Assert.True(control!.IsAvailable);
        Assert.True(control.CoreOffset.IsAdjustable || control.MemoryOffset.IsAdjustable);
        Assert.True(control.CoreOffset.Contains(control.CoreOffset.Current));
        Assert.True(control.MemoryOffset.Contains(control.MemoryOffset.Current));
    }

    sealed class FakeGpuOverclockControl : IGpuOverclockControl
    {
        readonly bool _rejectWrites;
        bool _writeAccessDenied;

        public FakeGpuOverclockControl(
            GpuClockOffsetRange core,
            GpuClockOffsetRange memory,
            bool rejectWrites = false)
        {
            CoreOffset = core;
            MemoryOffset = memory;
            _rejectWrites = rejectWrites;
        }

        public string Name => "Fake NVIDIA GPU";
        public bool IsAvailable => !_writeAccessDenied && (CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable);
        public GpuClockOffsetRange CoreOffset { get; private set; }
        public GpuClockOffsetRange MemoryOffset { get; private set; }
        public bool Refresh() => IsAvailable;

        public bool SetCoreOffset(int value)
        {
            if (_rejectWrites)
            {
                _writeAccessDenied = true;
                return false;
            }
            if (!CoreOffset.Contains(value)) return false;
            CoreOffset = CoreOffset with { Current = value };
            return true;
        }

        public bool SetMemoryOffset(int value)
        {
            if (_rejectWrites)
            {
                _writeAccessDenied = true;
                return false;
            }
            if (!MemoryOffset.Contains(value)) return false;
            MemoryOffset = MemoryOffset with { Current = value };
            return true;
        }

        public void ReportCoreOffset(int value) =>
            CoreOffset = CoreOffset with { Current = value };

        public void ReportMemoryOffset(int value) =>
            MemoryOffset = MemoryOffset with { Current = value };

        public void Dispose() { }
    }

    sealed class FakeElevatedGpuOverclockApplier : IGpuOverclockElevatedApplier
    {
        readonly FakeGpuOverclockControl _driver;
        readonly bool _updateDriver;

        public int CallCount { get; private set; }

        public FakeElevatedGpuOverclockApplier(FakeGpuOverclockControl driver, bool updateDriver = true)
        {
            _driver = driver;
            _updateDriver = updateDriver;
        }

        public Task<GpuOverclockApplyResult> ApplyAsync(GpuOverclockApplyRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_updateDriver && request.CoreOffset is int core) _driver.ReportCoreOffset(core);
            return Task.FromResult(new GpuOverclockApplyResult(
                true,
                request.CoreOffset ?? _driver.CoreOffset.Current,
                _driver.MemoryOffset.Current,
                null));
        }
    }
}
