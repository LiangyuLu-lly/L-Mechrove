using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// HandleMessage 在十个状态主题上曾无条件 Raise CapabilitiesChanged，
/// 能力位没变也会把仪表盘 ArrangeDashboard 重跑一遍。这里锁的是：
/// 相同能力位不重复通知；真正翻位时仍然通知。
/// </summary>
public class CapabilitiesChangedRaiseTests
{
    static MechrevoHw NewHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

    [Fact]
    public void HandleMessage_IdenticalCapabilityBits_DoesNotRaiseCapabilitiesChangedAgain()
    {
        using var hardware = NewHardware();
        int raises = 0;
        hardware.CapabilitiesChanged += () => raises++;

        const string payload = """{"effect":"Wave","light":4}""";
        hardware.HandleMessage("Keyboard/Status", payload);
        Assert.Equal(1, raises);

        hardware.HandleMessage("Keyboard/Status", payload);
        Assert.Equal(1, raises);
    }

    [Fact]
    public void HandleMessage_SupportBitFlip_RaisesCapabilitiesChanged()
    {
        using var hardware = NewHardware();
        int raises = 0;
        hardware.CapabilitiesChanged += () => raises++;

        hardware.HandleMessage("Keyboard/Status", """{"effect":"Wave","light":4}""");
        int afterKeyboard = raises;
        Assert.Equal(1, afterKeyboard);
        Assert.True(hardware.SupportsKeyboard);

        hardware.HandleMessage("HidLightbar/Status", """{"type":"RGB","powerStatus":"On"}""");
        Assert.Equal(afterKeyboard + 1, raises);
        Assert.True(hardware.SupportsLightbar);
    }
}
