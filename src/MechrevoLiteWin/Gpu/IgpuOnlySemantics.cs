namespace MechrevoLite.Gpu;

/// <summary>
/// iGPU-only 重试/回滚语义的**厂商对照常量与纯函数**（50 系实测：CCUWinUI <c>53527-53729</c>）。
/// 单点真源：服务层的轮询上限、重发节奏、成功判据、回滚取值都从这里取，避免散落的魔数漂移。
///
/// <para>只读、纯函数：不含 EC 写、不含固件变量写；命令一律走 MQTT。</para>
/// </summary>
public static class IgpuOnlySemantics
{
    /// <summary>每次轮询间隔（<c>Task.Delay(2000)</c>）。</summary>
    public const int PollIntervalMilliseconds = 2000;

    /// <summary>轮询次数上限（厂商 <c>count &gt; 60</c>；61 次 × 2 s ≈ 122 s）。</summary>
    public const int PollLimit = 61;

    /// <summary>每第 4 次轮询额外重发一次目标载荷（<c>count % 4 == 0</c>）。</summary>
    public const int RetryEveryPolls = 4;

    /// <summary>成功判据：打开 iGPU-only 时 <c>CheckDGpuStatusforIGpuOnlyOnSuccess == 2</c>。</summary>
    public const int SuccessOn = 2;

    /// <summary>成功判据：关闭 iGPU-only 时 <c>CheckDGpuStatusforIGpuOnlyOnSuccess == 1</c>。</summary>
    public const int SuccessOff = 1;

    /// <summary>该轮询序号是否应额外重发目标载荷。</summary>
    public static bool ShouldResend(int poll) => poll >= 0 && poll % RetryEveryPolls == 0;

    /// <summary>厂商成功判据：<paramref name="turningOn"/> 决定 2/1 哪一个是"成功"。</summary>
    public static bool IsSuccess(int reported, bool turningOn) =>
        reported == (turningOn ? SuccessOn : SuccessOff);

    /// <summary>AUTO 成功依赖 AC：插电期望 runtime=1，电池期望 runtime=2。</summary>
    public static int AutomaticRuntime(bool plugged) => plugged ? 1 : 2;

    /// <summary>
    /// 超时回滚时发给 <c>IGPUONLYCONNECTIONSWITCH_STATUS</c> 的开关值：
    /// RB_OFF=0 / RB_ON=1 / RB_AUTO=2。未知前值一律回落到 OFF（0）。
    /// </summary>
    public static int RollbackStatus(int modeBeforeSwitch) => modeBeforeSwitch switch
    {
        MechrevoLite.Hardware.MechrevoService.GpuAuto => 2,
        MechrevoLite.Hardware.MechrevoService.GpuIGpu => 1,
        _ => 0,
    };
}
