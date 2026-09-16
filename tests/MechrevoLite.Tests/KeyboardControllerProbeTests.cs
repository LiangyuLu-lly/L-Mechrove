using System.Diagnostics;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 控制器能力探测（KeyboardRgb.EnsureHidReadyAsync）的三态判定映射：
/// 无候选 → Unsupported（确定性，不重试）；Open/EnterCustomMode 失败 → 100 ms 后重试一次，仍失败 → Unsupported；
/// 全部通过 → Supported（保持流打开，调用方随即启动灯效）；异常或超过 3 s 上限 → Unknown（保持今天的行为，绝不改走 GCU）。
/// 探测复用 Connect 的唯一实现体（探测 ≡ 真实路径）；用假 HidDeviceWin 接缝驱动，不触碰真实硬件。
/// </summary>
public class KeyboardControllerProbeTests
{
    sealed class ProbeFakeHid : HidDeviceWin
    {
        public Func<bool> OnOpen = () => true;
        public Func<byte[], bool> OnSetFeature = _ => true;
        public Func<byte[], bool> OnWrite = _ => true;
        public int OpenCount;
        public int SetFeatureCount;
        public override bool Open() { Interlocked.Increment(ref OpenCount); return OnOpen(); }
        public override bool SetFeature(byte[] report) { Interlocked.Increment(ref SetFeatureCount); return OnSetFeature(report); }
        public override bool Write(byte[] report) => OnWrite(report);
        public override void Dispose() { }
    }

    /// <summary>测试脚手架：临时配置路径 + 替代真实枚举的 seam + 枚举计数 + 临时目录清理。</summary>
    sealed class ProbeScope : IDisposable
    {
        readonly string _directory;
        int _resolves;
        public KeyboardRgb Keyboard { get; }
        public int ResolveCount => _resolves;

        public ProbeScope(ProbeFakeHid? device)
        {
            _directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"));
            Keyboard.ResolveDeviceProbe = () => { _resolves++; return device; };
        }

        public void Dispose()
        {
            Keyboard.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task NoCandidate_IsUnsupportedAndIsNotRetried()
    {
        using var scope = new ProbeScope(null);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Unsupported, scope.Keyboard.ControllerAvailability);
        Assert.Equal(1, scope.ResolveCount);          // 无候选是确定性结论：恰好枚举一次
        Assert.False(scope.Keyboard.IsConnected);     // Unsupported 释放流
    }

    [Fact]
    public async Task OpenFailure_RetriesOnceThenReportsUnsupported()
    {
        var device = new ProbeFakeHid { OnOpen = () => false };
        using var scope = new ProbeScope(device);
        var stopwatch = Stopwatch.StartNew();

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());
        stopwatch.Stop();

        Assert.Equal(FeatureAvailability.Unsupported, scope.Keyboard.ControllerAvailability);
        Assert.Equal(2, device.OpenCount);            // 打开失败恰好重试一次
        Assert.Equal(2, scope.ResolveCount);
        Assert.True(stopwatch.ElapsedMilliseconds >= 80, "重试前应等待约 100 ms。");
        Assert.False(scope.Keyboard.IsConnected);
    }

    [Fact]
    public async Task OpenFailureThenSuccess_IsSupportedAfterTheRetry()
    {
        int opens = 0;
        var device = new ProbeFakeHid();
        device.OnOpen = () => ++opens >= 2;
        using var scope = new ProbeScope(device);

        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Supported, scope.Keyboard.ControllerAvailability);
        Assert.Equal(2, device.OpenCount);
        Assert.True(scope.Keyboard.IsConnected);      // Supported 保持流打开
    }

    [Fact]
    public async Task CustomModeFailure_RetriesOnceThenReportsUnsupported()
    {
        var device = new ProbeFakeHid { OnSetFeature = _ => false };   // EnterCustomMode 第一步被拒
        using var scope = new ProbeScope(device);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Unsupported, scope.Keyboard.ControllerAvailability);
        Assert.Equal(2, device.SetFeatureCount);      // 每次尝试的第一个 feature 就失败 → 恰好两次尝试
        Assert.False(scope.Keyboard.IsConnected);
    }

    [Fact]
    public async Task AllStepsPass_IsSupportedAndKeepsTheStreamOpen()
    {
        var device = new ProbeFakeHid();
        using var scope = new ProbeScope(device);

        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Supported, scope.Keyboard.ControllerAvailability);
        Assert.Equal(1, scope.ResolveCount);
        Assert.Equal(2, device.SetFeatureCount);      // step1 + step2 都被接受
        Assert.True(scope.Keyboard.IsConnected);
    }

    [Fact]
    public async Task ProbeException_IsUnknownAndReleasesTheStream()
    {
        var device = new ProbeFakeHid { OnSetFeature = _ => throw new InvalidOperationException("模拟 HID 异常") };
        using var scope = new ProbeScope(device);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Unknown, scope.Keyboard.ControllerAvailability);
        Assert.False(scope.Keyboard.IsConnected);     // Unknown 释放流，且绝不改走 GCU
    }

    /// <summary>Unknown 不缓存（瞬时失败）：下一次显式调用允许重探；Unknown → 确定性只发生一次，随后缓存冻结。</summary>
    [Fact]
    public async Task Unknown_IsNotCached_NextCallMayTurnDeterministic()
    {
        var device = new ProbeFakeHid { OnSetFeature = _ => throw new InvalidOperationException("瞬时失败") };
        using var scope = new ProbeScope(device);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());
        Assert.Equal(FeatureAvailability.Unknown, scope.Keyboard.ControllerAvailability);

        device.OnSetFeature = _ => true;              // 瞬时故障恢复
        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());
        Assert.Equal(FeatureAvailability.Supported, scope.Keyboard.ControllerAvailability);

        int probes = scope.Keyboard.ControllerProbeCount;
        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());
        Assert.Equal(probes, scope.Keyboard.ControllerProbeCount);   // 确定性结论已缓存，不再重探
    }

    [Fact]
    public async Task SupportedVerdict_IsCachedForTheProcessLifetime()
    {
        var device = new ProbeFakeHid();
        using var scope = new ProbeScope(device);

        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());
        int resolves = scope.ResolveCount;

        Assert.True(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(resolves, scope.ResolveCount);   // 缓存命中：不再枚举/打开
        Assert.Equal(1, scope.Keyboard.ControllerProbeCount);
        Assert.True(scope.Keyboard.IsConnected);
    }

    [Fact]
    public async Task UnsupportedVerdict_IsCachedForTheProcessLifetime()
    {
        var device = new ProbeFakeHid { OnOpen = () => false };
        using var scope = new ProbeScope(device);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());
        int opens = device.OpenCount;

        device.OnOpen = () => true;                   // 假想设备后来可用：不得自动翻转
        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(opens, device.OpenCount);        // 缓存命中：不再探测
        Assert.Equal(FeatureAvailability.Unsupported, scope.Keyboard.ControllerAvailability);
    }

    /// <summary>墙钟上限：探测超过 3 s 必须判 Unknown（只有无候选/重试后仍失败才允许判 Unsupported）。</summary>
    [Fact]
    public async Task ProbeExceedingTheWallClockCap_IsUnknown()
    {
        var device = new ProbeFakeHid { OnOpen = () => { Thread.Sleep(3500); return true; } };
        using var scope = new ProbeScope(device);

        Assert.False(await scope.Keyboard.EnsureHidReadyAsync());

        Assert.Equal(FeatureAvailability.Unknown, scope.Keyboard.ControllerAvailability);
        Assert.False(scope.Keyboard.IsConnected);
    }
}
