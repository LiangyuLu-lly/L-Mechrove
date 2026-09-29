using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 真机回归（2026-09-28）：从平衡/狂暴点「自定义」，GCU 在 1.15 s 确认窗口内没有切换
/// （刚处理完上一次模式切换时丢了这条 OPERATING_CUSTOM_MODE），用户再点一次才进自定义。
/// <see cref="MechrevoService.SwitchCustomProfileWithResend"/> 未确认时重发恰好一次；
/// 已确认、或这次请求已被更新的切换取代时不重发。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class CustomProfileResendTests
{
    const string FanControl = "Fan/Control";
    const string FanStatus = "Fan/Status";

    static bool IsCustomModeCommand(string topic, object payload, out int profile)
    {
        profile = -1;
        if (topic != FanControl || payload is not IDictionary<string, object> values) return false;
        if (!values.TryGetValue("Action", out object? action) || action?.ToString() != "OPERATING_CUSTOM_MODE") return false;
        profile = Convert.ToInt32(values["ProfileIndex"]);
        return true;
    }

    static bool IsStatusRequest(string topic, object payload) =>
        topic == FanControl && payload is IDictionary<string, object> values &&
        values.TryGetValue("Action", out object? action) && action?.ToString() == "GETSTATUS";

    [Fact]
    public async Task ADroppedFirstCommand_IsResentOnce_AndTheSwitchConfirms()
    {
        MechrevoHw? hardware = null;
        int customCommands = 0;
        int mode = 1, profile = 0;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (IsCustomModeCommand(topic, payload, out int requested))
            {
                // 第一条被 GCU 丢掉；第二条才生效。
                if (Interlocked.Increment(ref customCommands) >= 2)
                {
                    mode = 3;
                    profile = requested;
                    hardware!.HandleMessage(FanStatus, $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{requested}}}");
                }
            }
            else if (IsStatusRequest(topic, payload))
            {
                hardware!.HandleMessage(FanStatus, $"{{\"OperatingMode\":{mode},\"CustomProfileIndex\":{profile}}}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage(FanStatus, "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchCustomProfileWithResend(0).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, Volatile.Read(ref customCommands));
            Assert.Equal(3, hardware.OperatingMode);
            Assert.Equal(0, hardware.CustomProfileIndex);
        }
    }

    [Fact]
    public async Task InCustomModeOnTheOldProfile_TheCommandIsResentOnce_AndAPersistentRefusalStaysUnconfirmed()
    {
        MechrevoHw? hardware = null;
        int customCommands = 0;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (IsCustomModeCommand(topic, payload, out _)) Interlocked.Increment(ref customCommands);
            else if (IsStatusRequest(topic, payload))
                hardware!.HandleMessage(FanStatus, "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage(FanStatus, "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
            var service = new MechrevoService(hardware);

            // 真机：已进自定义但仍停在旧档。重发一次；硬件始终不认就如实返回未确认，不再无限重试。
            Assert.False(await service.SwitchCustomProfileWithResend(1).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, Volatile.Read(ref customCommands));
            Assert.Equal(0, hardware.CustomProfileIndex);
        }
    }

    [Fact]
    public async Task AlreadyConfirmed_IsNotResent()
    {
        MechrevoHw? hardware = null;
        int customCommands = 0;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (IsCustomModeCommand(topic, payload, out int requested))
            {
                Interlocked.Increment(ref customCommands);
                hardware!.HandleMessage(FanStatus, $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{requested}}}");
            }
            else if (IsStatusRequest(topic, payload))
            {
                hardware!.HandleMessage(FanStatus, $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{hardware.CustomProfileIndex}}}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage(FanStatus, "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchCustomProfileWithResend(2).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, Volatile.Read(ref customCommands));
        }
    }

    [Fact]
    public async Task ASupersededRequest_IsNotResent()
    {
        MechrevoHw? hardware = null;
        int firstProfileCommands = 0;
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (IsCustomModeCommand(topic, payload, out int requested))
            {
                if (requested == 0)
                {
                    Interlocked.Increment(ref firstProfileCommands);
                    firstPublished.TrySetResult();   // 丢掉：硬件留在平衡模式
                }
                else
                {
                    hardware!.HandleMessage(FanStatus, $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{requested}}}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage(FanStatus, "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            var service = new MechrevoService(hardware);

            Task<bool> first = service.SwitchCustomProfileWithResend(0);
            await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<bool> second = service.SwitchCustomProfile(1);

            Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, Volatile.Read(ref firstProfileCommands));
        }
    }
}
