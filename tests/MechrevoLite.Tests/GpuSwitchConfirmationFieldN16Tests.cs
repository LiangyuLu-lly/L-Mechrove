using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// iGPU-only 热切换的服务回报字段。
///
/// 5.56（50 系）控制台读 <c>CheckDGpuStatusforIGpuOnlyOnSuccess</c>，按
/// <c>Contains("1") ? 1 : Contains("2") ? 2 : 0</c> 解析（CCUWinUI.decompiled.cs:54637-54640）。
///
/// <para>订正（beta21，逐代调研 §6 #7）：N16 曾把 40 系的 <c>CheckDGpuStatusforIGpuOnlySwitch</c>
/// 的 EC 字节（85 / 170）映射成切换成功 / 失败。S40 只在 <c>IGPU_ONLY_CHECK_DGPU_SUPPORT</c> 之后才发它，
/// 它是「独显能否断开」的就绪字节，不是切换结果——映射成成功会让从未发生的切换被判成功。现已不读。</para>
/// </summary>
public class GpuSwitchConfirmationFieldN16Tests
{
    static MechrevoHw Hardware() => new(null, new MechrevoDeviceCapabilities { IgpuOnly = true });

    /// <summary>Given the 5.56 encoding, When the status reports OnSuccess=2, Then the result is 2.</summary>
    [Fact]
    public void TheOnSuccessEncodingStillParses()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2"}""");

        Assert.Equal(2, hw.GpuSwitchResult);
        Assert.True(hw.GpuSwitchResultReported);
    }

    /// <summary>40 系的就绪字节 85 不是「切换成功」：结果保持未上报。</summary>
    [Fact]
    public void TheSwitchReadinessByte85IsNotASwitchResult()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlySwitch":"85"}""");

        Assert.Equal(-1, hw.GpuSwitchResult);
        Assert.False(hw.GpuSwitchResultReported);
    }

    /// <summary>170 同理：不是「切换失败」，只是就绪字节。</summary>
    [Fact]
    public void TheSwitchReadinessByte170IsNotASwitchResult()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","CheckDGpuStatusforIGpuOnlySwitch":"170"}""");

        Assert.Equal(-1, hw.GpuSwitchResult);
        Assert.False(hw.GpuSwitchResultReported);
    }

    /// <summary>两个字段都在时只看 OnSuccess。</summary>
    [Fact]
    public void TheOnSuccessFieldWinsWhenBothArePresent()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2","CheckDGpuStatusforIGpuOnlySwitch":"170"}""");

        Assert.Equal(2, hw.GpuSwitchResult);
    }

    /// <summary>热切换寄存器单独记录：RB_ON / RB_OFF / RB_AUTO。</summary>
    [Theory]
    [InlineData("IGPU_ONLY_CONNECT_RB_ON", MechrevoService.GpuIGpu)]
    [InlineData("IGPU_ONLY_CONNECT_RB_OFF", MechrevoService.GpuStandard)]
    [InlineData("IGPU_ONLY_CONNECT_RB_AUTO", MechrevoService.GpuAuto)]
    [InlineData("SOMETHING_ELSE", -1)]
    public void TheHotSwitchRegisterIsTrackedOnItsOwn(string status, int expected)
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            $$"""{"DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_CONNECT_TOGGLE_OFF","IGpuOnlyConnectionSwitch_Status":"{{status}}"}""");

        Assert.Equal(expected, hw.IgpuOnlyRegister);
    }
}
