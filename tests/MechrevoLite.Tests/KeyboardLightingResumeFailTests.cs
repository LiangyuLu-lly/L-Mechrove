using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T32 失败/边界路径：关灯不得被点亮；没有键盘对象不得发请求；并发唤醒不得重复请求。
/// happy 路径见 <see cref="KeyboardLightingResumeTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class KeyboardLightingResumeFailTests
{
    [Fact]
    public void ThePolicyNeverRequestsARestoreWhenTheLightIsOff()
    {
        Assert.False(KeyboardResumePolicy.ShouldRestoreEffect(false));
        Assert.Equal(0, KeyboardResumePolicy.EffectRestoreRequests(false));
    }

    [Fact]
    public async Task ResumeWithNoKeyboardObjectDoesNotCrashOrRequestARestore()
    {
        using var harness = new KeyboardLightingResumeTests.Harness(kbPowerOn: true);
        int requestBefore = Program.LightingRestoreRequestId;
        Program.rgb = null!;

        Program.ScheduleKeyboardLightingRestoreAfterResume();
        await KeyboardLightingResumeTests.WaitForResumeRestoreAsync();

        Assert.Equal(requestBefore, Program.LightingRestoreRequestId);
    }

    [Fact]
    public async Task ResumeWithTheLightOffKeepsTheEffectStoppedAcrossTheRetryLoop()
    {
        using var harness = new KeyboardLightingResumeTests.Harness(kbPowerOn: false);
        harness.Keyboard.StopCurrentEffect();
        int generationBefore = harness.Keyboard.EffectGeneration;

        Program.ScheduleKeyboardLightingRestoreAfterResume();
        await KeyboardLightingResumeTests.WaitForResumeRestoreAsync();

        Assert.Equal(generationBefore, harness.Keyboard.EffectGeneration);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
        Assert.Equal(0, harness.PowerOnCount("Keyboard/Ctrl"));
    }

    [Fact]
    public async Task AConcurrentResumeDoesNotDoubleRequest()
    {
        using var harness = new KeyboardLightingResumeTests.Harness(kbPowerOn: true);
        int requestBefore = Program.LightingRestoreRequestId;

        Program.ScheduleKeyboardLightingRestoreAfterResume();
        Program.ScheduleKeyboardLightingRestoreAfterResume();

        Assert.Equal(requestBefore + 1, Program.LightingRestoreRequestId);

        await KeyboardLightingResumeTests.WaitForResumeRestoreAsync();
        Assert.Equal(requestBefore + 1, Program.LightingRestoreRequestId);
    }

    [Fact]
    public void ThePolicyIsTheOnlyGateAndRejectsNonBooleanIntent()
    {
        // 只有 true 才放行；其余（含默认 false）一律不重放。
        Assert.False(KeyboardResumePolicy.ShouldRestoreEffect(default));
        Assert.Equal(0, KeyboardResumePolicy.EffectRestoreRequests(default));
    }
}
