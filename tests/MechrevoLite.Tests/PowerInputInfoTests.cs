using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// 隐藏信息只读展示（docs/hardware/hidden-readonly-info-plan.md §9）：供电寄存器解码、适配器表、
/// 采样器限频/容错、电池行文本、风扇占空比新鲜度、行为不变与只读守卫。
/// </summary>
public class PowerInputDecoderTests
{
    [Theory]
    // §2.1 真值表
    [InlineData(false, 0x81, true, PowerInputKind.Battery)]           // 本机 0x81 + 离线 → 电池（Windows 为准）
    [InlineData(true, 0x81, true, PowerInputKind.Barrel)]             // 本机 0x81 + 在线 → 圆口
    [InlineData(true, 0x02, true, PowerInputKind.TypeC)]
    [InlineData(true, 0x04, true, PowerInputKind.TypeC)]
    [InlineData(true, 0x03, true, PowerInputKind.BarrelAndTypeC)]
    [InlineData(true, 0x00, true, PowerInputKind.ExternalUnknown)]    // 厂商会当圆口；我方不猜
    [InlineData(true, -1, true, PowerInputKind.ExternalUnknown)]      // 读不到 → 只按 Windows
    [InlineData(false, -1, true, PowerInputKind.Battery)]
    [InlineData(true, 0x81, false, PowerInputKind.ExternalUnknown)]   // 10/20 不解码
    [InlineData(false, 0x81, false, PowerInputKind.Battery)]
    // bit4/5/7 厂商从不读，不影响结果
    [InlineData(true, 0xB1, true, PowerInputKind.Barrel)]
    [InlineData(true, 0x30, true, PowerInputKind.ExternalUnknown)]
    [InlineData(true, 0xB4, true, PowerInputKind.TypeC)]
    public void TheTruthTableMatchesThePlan(bool acOnline, int complex, bool decode, PowerInputKind expected) =>
        Assert.Equal(expected, PowerInputDecoder.Decode(acOnline, complex, decode));

    [Fact]
    public void ReconcilePrefersTheCurrentWindowsLineState()
    {
        var barrel = new PowerInputSample(PowerInputKind.Barrel, 230, 0x81, 0x08, 0, true);
        var battery = new PowerInputSample(PowerInputKind.Battery, null, 0x80, 0x08, 0, true);

        Assert.Equal(PowerInputKind.Battery, PowerInputDecoder.Reconcile(barrel, acOnlineNow: false));
        Assert.Equal(PowerInputKind.Barrel, PowerInputDecoder.Reconcile(barrel, acOnlineNow: true));
        // 插电后采样还停在电池：只说外接电源，不沿用旧细分
        Assert.Equal(PowerInputKind.ExternalUnknown, PowerInputDecoder.Reconcile(battery, acOnlineNow: true));
        Assert.Equal(PowerInputKind.ExternalUnknown, PowerInputDecoder.Reconcile(null, acOnlineNow: true));
        Assert.Equal(PowerInputKind.Battery, PowerInputDecoder.Reconcile(null, acOnlineNow: false));
    }
}

public class AdapterRatingTests
{
    [Theory]
    [InlineData(0x00, 330)]
    [InlineData(0x08, 230)]
    [InlineData(0x10, 180)]
    [InlineData(0x18, 150)]
    [InlineData(0x20, 120)]
    [InlineData(0x28, 90)]
    [InlineData(0x30, 65)]
    [InlineData(0x38, 40)]
    [InlineData(0x40, 280)]
    [InlineData(0x1A, 150)]   // bit1 = 狂暴支持，掩码后不影响
    [InlineData(0x87, 330)]   // 码外位（bit0-2、bit7）不影响
    public void TableCodesDecode(int raw, int watts)
    {
        Assert.True(PowerInputDecoder.TryDecodeAdapterWatts(raw, out int decoded));
        Assert.Equal(watts, decoded);
    }

    [Theory]
    [InlineData(0x5A)]   // 本机：码 0x58 表外
    [InlineData(0x48)]
    [InlineData(0x78)]
    [InlineData(-1)]
    public void OutOfTableCodesNeverFallBackTo150(int raw)
    {
        Assert.False(PowerInputDecoder.TryDecodeAdapterWatts(raw, out int watts));
        Assert.Equal(0, watts);
    }

    [Fact]
    public void TheModelExpansionTableKeepsTheVendorDefault()
    {
        // 机型展开保持厂商等价：表外默认 150 W（与展示用的 TryDecodeAdapterWatts 分开）。
        MethodInfo method = typeof(ModelRegistry).GetMethod("AdapterWattFor", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(150, (int)method.Invoke(null, new object[] { 0x5A })!);
        Assert.Equal(230, (int)method.Invoke(null, new object[] { 0x08 })!);
    }
}

/// <summary>记录读过哪些地址的假 EC。</summary>
internal sealed class RecordingEc : IEcReadTransport
{
    public Dictionary<int, int> Values { get; } = new();
    public List<int> Reads { get; } = new();
    public Func<int, int>? Hook { get; set; }

    public int ReadByte(int address)
    {
        Reads.Add(address);
        if (Hook is not null) return Hook(address);
        return Values.TryGetValue(address, out int value) ? value : -1;
    }
}

public class PowerInputSamplerTests
{
    long _now = 10_000;
    bool _ac = true;
    bool _decode = true;
    readonly RecordingEc _ec = new();

    PowerInputSampler NewSampler(Func<IEcReadTransport?>? factory = null) =>
        new(factory ?? (() => _ec), () => _ac, () => _decode, () => _now);

    [Fact]
    public void OnlyTheTwoPowerRegistersAreEverRead()
    {
        _ec.Values[0x7CC] = 0x81;
        _ec.Values[0x49F] = 0x5A;
        PowerInputSampler sampler = NewSampler();

        PowerInputSample sample = sampler.Refresh();

        Assert.Equal(PowerInputKind.Barrel, sample.Kind);
        Assert.Null(sample.AdapterWatts);                  // 0x58 表外：不显示瓦数
        Assert.Equal(0x81, sample.ComplexStatusRaw);
        Assert.Equal(0x5A, sample.BiosInfo3Raw);
        Assert.True(sample.DecodeEnabled);
        Assert.All(_ec.Reads, address => Assert.Contains(address, new[] { 0x7CC, 0x49F }));
    }

    [Fact]
    public void RefreshIsThrottledToFiveSecondsUnlessForced()
    {
        _ec.Values[0x7CC] = 0x81;
        _ec.Values[0x49F] = 0x08;
        PowerInputSampler sampler = NewSampler();

        sampler.Refresh();
        int reads = _ec.Reads.Count;
        _now += 1000;
        Assert.False(sampler.NeedsRefresh());
        sampler.Refresh();
        Assert.Equal(reads, _ec.Reads.Count);

        Assert.True(sampler.NeedsRefresh(force: true));
        sampler.Refresh(force: true);
        Assert.True(_ec.Reads.Count > reads);

        reads = _ec.Reads.Count;
        _now += PowerInputSampler.RefreshIntervalMilliseconds;
        Assert.True(sampler.NeedsRefresh());
        sampler.Refresh();
        Assert.True(_ec.Reads.Count > reads);
    }

    [Fact]
    public void TheAdapterByteIsReadOnlyOnFirstReadStatusChangeOrLineChange()
    {
        _ec.Values[0x7CC] = 0x81;
        _ec.Values[0x49F] = 0x08;
        PowerInputSampler sampler = NewSampler();

        Assert.Equal(230, sampler.Refresh().AdapterWatts);
        Assert.Equal(new[] { 0x7CC, 0x49F }, _ec.Reads);

        _ec.Reads.Clear();
        _now += 6000;
        Assert.Equal(230, sampler.Refresh().AdapterWatts);   // 0x7CC 与交流都没变：沿用适配器码
        Assert.Equal(new[] { 0x7CC }, _ec.Reads);

        _ec.Reads.Clear();
        _ec.Values[0x7CC] = 0x80;
        _now += 6000;
        Assert.Equal(PowerInputKind.ExternalUnknown, sampler.Refresh().Kind);
        Assert.Equal(new[] { 0x7CC, 0x49F }, _ec.Reads);

        _ec.Reads.Clear();
        _ac = false;
        _now += 6000;
        Assert.Equal(PowerInputKind.Battery, sampler.Refresh().Kind);
        Assert.Equal(new[] { 0x7CC, 0x49F }, _ec.Reads);
    }

    [Fact]
    public void ReadFailuresDegradeToWindowsWithoutThrowing()
    {
        _ec.Hook = _ => throw new IOException("bus stuck");
        PowerInputSampler sampler = NewSampler();

        PowerInputSample sample = sampler.Refresh();
        Assert.Equal(PowerInputKind.ExternalUnknown, sample.Kind);
        Assert.False(sample.EcReadable);
        Assert.True(sample.DecodeEnabled);

        _ac = false;
        Assert.Equal(PowerInputKind.Battery, sampler.Refresh(force: true).Kind);
    }

    [Fact]
    public void ANullTransportMeansExternalPowerOnly()
    {
        PowerInputSampler sampler = NewSampler(() => null);

        PowerInputSample sample = sampler.Refresh();

        Assert.Equal(PowerInputKind.ExternalUnknown, sample.Kind);
        Assert.Equal(-1, sample.ComplexStatusRaw);
    }

    [Fact]
    public void ThreeConsecutiveFailuresDropTheCachedAdapterCode()
    {
        _ec.Values[0x7CC] = 0x81;
        _ec.Values[0x49F] = 0x08;
        PowerInputSampler sampler = NewSampler();
        sampler.Refresh();

        _ec.Values.Remove(0x7CC);   // 读回 -1
        Assert.Equal(0x08, sampler.Refresh(force: true).BiosInfo3Raw);
        Assert.Equal(0x08, sampler.Refresh(force: true).BiosInfo3Raw);
        Assert.Equal(-1, sampler.Refresh(force: true).BiosInfo3Raw);

        _ec.Values[0x7CC] = 0x81;
        _ec.Reads.Clear();
        PowerInputSample recovered = sampler.Refresh(force: true);
        Assert.Equal(230, recovered.AdapterWatts);
        Assert.Contains(0x49F, _ec.Reads);   // 缓存已丢，必须重读
    }

    [Fact]
    public void TenTwentyMachinesNeverTouchTheEc()
    {
        _decode = false;
        _ec.Values[0x7CC] = 0x81;
        int factoryCalls = 0;
        PowerInputSampler sampler = NewSampler(() => { factoryCalls++; return _ec; });

        PowerInputSample sample = sampler.Refresh();

        Assert.Equal(PowerInputKind.ExternalUnknown, sample.Kind);
        Assert.False(sample.DecodeEnabled);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(_ec.Reads);
    }

    [Fact]
    public void AHungDriverNeverBlocksTheCallerOrPilesUpThreads()
    {
        using var release = new ManualResetEventSlim(false);
        var ec = new RecordingEc { Hook = _ => { release.Wait(5000); return 0x81; } };
        PowerInputSampler previous = HardwareControl.ReplacePowerInputSamplerForTests(
            new PowerInputSampler(() => ec, () => true, () => true));
        try
        {
            var watch = Stopwatch.StartNew();
            HardwareControl.RefreshPowerInput(force: true);
            Assert.True(watch.ElapsedMilliseconds < 2000, $"waited {watch.ElapsedMilliseconds} ms");

            HardwareControl.RefreshPowerInput(force: true);   // 上一次还卡着：不另起读取
            Assert.Single(ec.Reads);

            release.Set();
            SpinWait.SpinUntil(() => HardwareControl.PowerInput is not null, 5000);
            Assert.Equal(PowerInputKind.Barrel, HardwareControl.PowerInput?.Kind);   // 驱动恢复后结果照常发布
        }
        finally
        {
            release.Set();
            SpinWait.SpinUntil(() => HardwareControl.PowerInput is not null, 5000);
            HardwareControl.ReplacePowerInputSamplerForTests(previous);
        }
    }
}

public class BatteryStatusReadingTests
{
    [Fact]
    public void FlagsAndRateSignCombine()
    {
        BatteryStatusReading charging = BatteryRateReader.FromRaw(0x5, 50_000, 16_560, 45_200);
        Assert.True(charging.IsCharging);
        Assert.False(charging.IsDischarging);
        Assert.True(charging.IsOnLine);

        BatteryStatusReading discharging = BatteryRateReader.FromRaw(0x2, 50_000, 15_900, -18_500);
        Assert.True(discharging.IsDischarging);
        Assert.False(discharging.IsOnLine);

        BatteryStatusReading full = BatteryRateReader.FromRaw(0x1, 99_072, 16_560, 0);
        Assert.False(full.IsCharging);
        Assert.False(full.IsDischarging);
        Assert.True(full.IsOnLine);

        // 标志说在充但 Rate 未给：仍按充电中
        Assert.True(BatteryRateReader.FromRaw(0x4, 0, 0, 0).IsCharging);
    }

    [Fact]
    public void TheUnknownRateSentinelMeansNoPower()
    {
        BatteryStatusReading reading = BatteryRateReader.FromRaw(0x1, 0, 0, unchecked((int)0x80000000));
        Assert.Null(reading.RateMilliwatts);
        Assert.Null(BatteryRateReader.FromMilliwatts(reading.RateMilliwatts));
    }
}

public class BatteryHeadlineTextTests
{
    static IDisposable ZhCn()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        return new Restore(() => CultureInfo.CurrentUICulture = previous);
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    static PowerInputSample Sample(PowerInputKind kind, int complex, int bios, int? watts = null, bool decode = true) =>
        new(kind, watts, complex, bios, 0, decode);

    static SettingsForm.BatteryDisplayInput Input(
        PowerInputSample? power, bool ac, BatteryStatusReading? status, decimal? rate,
        int percent = 100, int limit = -1, bool abnormal = false, int cycles = 17, string? capacity = "0 mWh") =>
        new(power, ac, status, rate, percent, limit, abnormal, cycles, capacity);

    static readonly BatteryStatusReading OnLineIdle = BatteryRateReader.FromRaw(0x1, 99_072, 16_560, 0);

    [Fact]
    public void ThisMachineReadsDcAdapterAndFull()
    {
        using var _ = ZhCn();
        var input = Input(Sample(PowerInputKind.Barrel, 0x81, 0x5A), true, OnLineIdle, 0m);
        Assert.Equal("圆口供电 · 已充满", SettingsForm.BatteryHeadlineText(input));
    }

    [Fact]
    public void KnownWattsAndChargingRate()
    {
        using var _ = ZhCn();
        var input = Input(Sample(PowerInputKind.Barrel, 0x81, 0x08, 230), true,
            BatteryRateReader.FromRaw(0x5, 50_000, 16_560, 45_200), 45.2m, percent: 60);
        Assert.Equal("圆口 230W · 充电 45.2W", SettingsForm.BatteryHeadlineText(input));
    }

    [Fact]
    public void TypeCBatteryAndExternal()
    {
        using var _ = ZhCn();
        Assert.Equal("Type-C 供电 · 充电 38.0W", SettingsForm.BatteryHeadlineText(
            Input(Sample(PowerInputKind.TypeC, 0x04, 0x08), true, BatteryRateReader.FromRaw(0x5, 0, 0, 38_000), 38m, percent: 50)));
        Assert.Equal("电池供电 · 放电 18.5W", SettingsForm.BatteryHeadlineText(
            Input(Sample(PowerInputKind.Battery, 0x80, 0x08), false, BatteryRateReader.FromRaw(0x2, 0, 0, -18_500), -18.5m, percent: 70)));
        Assert.Equal("外接电源 · 未充电", SettingsForm.BatteryHeadlineText(
            Input(null, true, OnLineIdle, 0m, percent: 60)));
    }

    [Fact]
    public void HeldAtTheChargeLimit()
    {
        using var _ = ZhCn();
        var input = Input(Sample(PowerInputKind.Barrel, 0x81, 0x5A), true, OnLineIdle, 0m, percent: 78, limit: 80);
        Assert.Equal("圆口供电 · 已到充电上限", SettingsForm.BatteryHeadlineText(input));
        // 离上限还远：未充电
        Assert.Equal("圆口供电 · 未充电", SettingsForm.BatteryHeadlineText(input with { Percent = 60 }));
    }

    [Fact]
    public void AnAbnormalBatteryIsTagged()
    {
        using var _ = ZhCn();
        var input = Input(Sample(PowerInputKind.Barrel, 0x81, 0x5A), true, OnLineIdle, 0m, abnormal: true);
        Assert.Equal("圆口供电 · 已充满 · 电池异常", SettingsForm.BatteryHeadlineText(input));
        Assert.Contains("电池异常", SettingsForm.BatteryDetailsTooltip(input));
    }

    [Fact]
    public void AStaleBatterySampleAfterPluggingInSaysExternalPowerOnly()
    {
        using var _ = ZhCn();
        var input = Input(Sample(PowerInputKind.Battery, 0x80, 0x5A), true, null, null, percent: 50);
        Assert.Equal("外接电源", SettingsForm.BatteryHeadlineText(input));
        string tip = SettingsForm.BatteryDetailsTooltip(input);
        Assert.Contains("供电：外接电源", tip);
        Assert.DoesNotContain("0x7CC", tip);   // 旧采样的原始值不能佐证 Windows 的判断
    }

    [Fact]
    public void TheTooltipCarriesTheRawDetails()
    {
        using var _ = ZhCn();
        string tip = SettingsForm.BatteryDetailsTooltip(
            Input(Sample(PowerInputKind.Barrel, 0x81, 0x5A), true, OnLineIdle, 0m));

        Assert.Contains("供电：圆口供电（EC 0x7CC=0x81）", tip);
        Assert.Contains("适配器额定功率：未知（编码 0x58 不在官方表内）", tip);
        Assert.Contains("电量：100% · 电压：16.56V", tip);
        Assert.Contains("循环次数：17", tip);
        Assert.DoesNotContain("0 mWh", tip);   // 厂商写死的 0 容量不显示

        string known = SettingsForm.BatteryDetailsTooltip(
            Input(Sample(PowerInputKind.Barrel, 0x81, 0x08, 230), true, OnLineIdle, 0m));
        Assert.Contains("适配器额定功率：230W（按官方表）", known);

        string dual = SettingsForm.BatteryDetailsTooltip(
            Input(Sample(PowerInputKind.BarrelAndTypeC, 0x83, 0x08, 230), true, OnLineIdle, 0m));
        Assert.Contains("圆口 + Type-C", dual);
        Assert.Contains("本机型未核实", dual);
    }

    [Fact]
    public void EcFailureAndTenTwentyAreWordedDifferently()
    {
        using var _ = ZhCn();
        string failed = SettingsForm.BatteryDetailsTooltip(
            Input(Sample(PowerInputKind.ExternalUnknown, -1, -1), true, OnLineIdle, 0m));
        Assert.Contains("供电类型未知（EC 读取失败）", failed);

        string legacy = SettingsForm.BatteryDetailsTooltip(
            Input(Sample(PowerInputKind.ExternalUnknown, -1, -1, decode: false), true, OnLineIdle, 0m));
        Assert.Contains("供电：外接电源", legacy);
        Assert.DoesNotContain("EC 读取失败", legacy);
    }

    [Fact]
    public void UnknownSegmentsNeverShowPlaceholders()
    {
        using var _ = ZhCn();
        var inputs = new[]
        {
            Input(null, true, null, null, percent: -1, cycles: -1, capacity: null),
            Input(null, false, null, null, percent: -1, cycles: -1, capacity: ""),
            Input(Sample(PowerInputKind.ExternalUnknown, -1, -1), true, null, null, percent: -1, cycles: -1),
            Input(Sample(PowerInputKind.Barrel, 0x81, -1), true, BatteryRateReader.FromRaw(0x1, 0, uint.MaxValue, 0), null),
        };
        foreach (var input in inputs)
        {
            string text = SettingsForm.BatteryHeadlineText(input) + "\n" + SettingsForm.BatteryDetailsTooltip(input);
            Assert.DoesNotContain("-1", text);
            Assert.DoesNotContain("?", text);
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("4294967", text);   // BATTERY_UNKNOWN_VOLTAGE
        }
    }
}

public class TelemetryFanDutyTests
{
    [Fact]
    public void AFreshZeroMeansTheFanStopped() =>
        Assert.Equal("0rpm 0%", SettingsForm.TelemetryRest(null, 0, 0, fanFresh: true, zeroIsMeaningful: true));

    [Fact]
    public void StaleFanDataIsHidden() =>
        Assert.Equal("25W", SettingsForm.TelemetryRest(25f, 2047, 43, fanFresh: false, zeroIsMeaningful: true));

    [Fact]
    public void MissingFieldsAreNeverShown() =>
        Assert.Equal("", SettingsForm.TelemetryRest(null, -1, -1, fanFresh: true, zeroIsMeaningful: true));

    [Fact]
    public void RealReadingsAreShown() =>
        Assert.Equal("54W 2047rpm 43%", SettingsForm.TelemetryRest(54.4f, 2047, 43, fanFresh: true, zeroIsMeaningful: true));

    [Fact]
    public void TheLegacyServiceKeepsHidingZero() =>
        Assert.Equal("", SettingsForm.TelemetryRest(null, 0, 0, fanFresh: true, zeroIsMeaningful: false));

    [Theory]
    [InlineData(-1, true, "—")]
    [InlineData(0, true, "—")]
    [InlineData(54, false, "—")]
    [InlineData(54, true, "54°C")]
    public void TemperatureNeedsAFreshPositiveReading(int temp, bool fresh, string expected) =>
        Assert.Equal(expected, SettingsForm.TelemetryTemp(temp, fresh));

    [Fact]
    public void FanFreshnessFollowsTheFanInfoFrames()
    {
        using var hardware = new MechrevoHw();
        Assert.False(hardware.IsFanInfoFresh(MechrevoHw.FanInfoMaximumAge));

        hardware.HandleMessage("System/FanInfo", """{"CpuFanDuty":0,"GpuFanDuty":0,"CpuFanRpm":0,"GpuFanRpm":0}""");
        long now = Environment.TickCount64;
        Assert.True(hardware.IsFanInfoFresh(MechrevoHw.FanInfoMaximumAge, now));
        Assert.False(hardware.IsFanInfoFresh(MechrevoHw.FanInfoMaximumAge, now + 7000));

        Assert.False(hardware.IsSensorInfoFresh(cpu: true, MechrevoHw.FanInfoMaximumAge));
        hardware.HandleMessage("System/CpuInfo", """{"CpuTemperature":54}""");
        Assert.True(hardware.IsSensorInfoFresh(cpu: true, MechrevoHw.FanInfoMaximumAge));
        Assert.False(hardware.IsSensorInfoFresh(cpu: false, MechrevoHw.FanInfoMaximumAge));
    }
}

public class PowerSourceBehaviourUnchangedTests
{
    [Fact]
    public void TypeCDisplayDoesNotChangeTheBehaviouralPowerSource()
    {
        var ec = new RecordingEc();
        ec.Values[0x7CC] = 0x04;   // Type-C 在充
        ec.Values[0x49F] = 0x08;
        PowerInputSampler previous = HardwareControl.ReplacePowerInputSamplerForTests(
            new PowerInputSampler(() => ec, () => true, () => true));
        try
        {
            // 上一个用例的后台读取可能还没清掉在途标记：循环触发直到采样发布。
            SpinWait.SpinUntil(() =>
            {
                HardwareControl.RefreshPowerInput(force: true);
                return HardwareControl.PowerInput is not null;
            }, 3000);
            Assert.Equal(PowerInputKind.TypeC, HardwareControl.PowerInput?.Kind);

            // 行为口径只看 Windows：插电 = Barrel，离电 = Battery；绝不返回 USBC。
            Program.PowerSource expected = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online
                ? Program.PowerSource.Barrel
                : Program.PowerSource.Battery;
            Assert.Equal(expected, Program.ReadPowerSource());
        }
        finally
        {
            HardwareControl.ReplacePowerInputSamplerForTests(previous);
        }
    }
}

/// <summary>只读守卫：供电采样与诊断段不引用写 IOCTL、不调用含写路径的 EC 类型。</summary>
public class PowerInputReadOnlyGuardTests
{
    static readonly string[] ForbiddenTypes = { "MechrevoLite.Hardware.EcChargeLimit", "Probe.EcProbe", "Probe.AcpiDriverIo" };

    static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(flags)) yield return method;
        foreach (MethodBase method in type.GetConstructors(flags)) yield return method;
        foreach (Type nested in type.GetNestedTypes(flags))
            foreach (MethodBase method in MethodsOf(nested)) yield return method;
    }

    [Theory]
    [InlineData(typeof(PowerInputSampler))]
    [InlineData(typeof(PowerInputDecoder))]
    [InlineData(typeof(MechrevoLite.Diagnostics.DiagnosticSystemInfo))]
    public void PowerInputCodeOnlyReads(Type type)
    {
        foreach (MethodBase method in MethodsOf(type))
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null || il.Length == 0) continue;
            EcWriteGuard.IlScanResult scan = EcWriteGuard.IlScan.Run(method.Module, il);

            Assert.DoesNotContain(EcWriteGuard.EcWriteIoctl, scan.Immediates);
            foreach (MethodBase called in scan.Calls)
            {
                string owner = called.DeclaringType?.FullName ?? "";
                Assert.DoesNotContain(owner, ForbiddenTypes);
                if (called.DeclaringType == typeof(IEcReadTransport))
                    Assert.Equal(nameof(IEcReadTransport.ReadByte), called.Name);
            }
        }
    }
}

/// <summary>
/// 真机只读探针（Category=Integration，默认不跑）：读本机 EC 0x7CC/0x49F 与电池 IOCTL，打印电池行文本与诊断段。
/// 只读——唯一的 EC 调用是读 IOCTL。运行：<c>dotnet test --filter "FullyQualifiedName~PowerInputMachineProbe"</c>。
/// </summary>
[Trait("Category", "Integration")]
public class PowerInputMachineProbe(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void PrintsThisMachinesPowerReadback()
    {
        var sampler = new PowerInputSampler(
            () => MechrevoService.EcReadTransportFactory(),
            () => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online,
            () => true);
        var watch = Stopwatch.StartNew();
        PowerInputSample sample = sampler.Refresh(force: true);
        output.WriteLine($"sample: {sample} ({watch.ElapsedMilliseconds} ms)");

        BatteryStatusReading? status = BatteryRateReader.ReadStatus();
        output.WriteLine($"status: {status}");
        output.WriteLine($"information: {BatteryRateReader.ReadInformation()}");

        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        try
        {
            var input = new SettingsForm.BatteryDisplayInput(sample,
                SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online, status,
                BatteryRateReader.FromMilliwatts(status?.RateMilliwatts),
                (int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100), -1, false, 17, "0 mWh");
            output.WriteLine("headline: " + SettingsForm.BatteryHeadlineText(input));
            output.WriteLine("tooltip:\n" + SettingsForm.BatteryDetailsTooltip(input));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        var sb = new System.Text.StringBuilder();
        MechrevoLite.Diagnostics.DiagnosticSystemInfo.AppendPowerAndBattery(sb, MechrevoService.EcReadTransportFactory, () => true);
        output.WriteLine(sb.ToString());
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }
}
