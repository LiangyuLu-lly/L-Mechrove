using MechrevoLite.Battery;

namespace MechrevoLite.Tests;

/// <summary>
/// 电池充放瓦数数据链（beta18）：字段由本地 OS 电池 IOCTL 填充（充电为正/放电为负），
/// 读不到保持 null，悬浮窗与托盘提示只在有数值时显示，不编数。
/// </summary>
public class BatteryRateTests
{
    [Theory]
    [InlineData(45000, 45.0)]
    [InlineData(-32400, -32.4)]
    [InlineData(0, 0.0)]
    public void FromMilliwatts_ConvertsSignedMilliwattsToWatts(int milliwatts, double expectedWatts)
    {
        decimal? watts = BatteryRateReader.FromMilliwatts(milliwatts);
        Assert.NotNull(watts);
        Assert.Equal(expectedWatts, (double)watts.Value);
    }

    [Fact]
    public void FromMilliwatts_UnknownSentinelAndNullMeanNoReading()
    {
        Assert.Null(BatteryRateReader.FromMilliwatts(unchecked((int)0x80000000)));
        Assert.Null(BatteryRateReader.FromMilliwatts(null));
    }

    sealed class ReaderSwap : IDisposable
    {
        readonly Func<decimal?> _previousReader;
        readonly decimal? _previousRate;

        public ReaderSwap(Func<decimal?> reader)
        {
            _previousReader = HardwareControl.batteryRateReader;
            _previousRate = HardwareControl.batteryRate;
            Calls = 0;
            HardwareControl.batteryRateReader = () => { Calls++; return reader(); };
        }

        public int Calls { get; private set; }

        public void Dispose()
        {
            HardwareControl.batteryRateReader = _previousReader;
            HardwareControl.batteryRate = _previousRate;
        }
    }

    [Fact]
    public void RefreshBatteryRate_PublishesReaderValue()
    {
        using var swap = new ReaderSwap(() => -42.5m);
        HardwareControl.RefreshBatteryRate(force: true);
        Assert.Equal(-42.5m, HardwareControl.batteryRate);
    }

    [Fact]
    public void RefreshBatteryRate_FailureKeepsUnknownInsteadOfANumber()
    {
        using var swap = new ReaderSwap(() => null);
        HardwareControl.RefreshBatteryRate(force: true);
        Assert.Null(HardwareControl.batteryRate);
    }

    [Fact]
    public void RefreshBatteryRate_ThrottlesUntilForced()
    {
        using var swap = new ReaderSwap(() => 1m);
        HardwareControl.RefreshBatteryRate(force: true);
        int reads = swap.Calls;
        HardwareControl.RefreshBatteryRate();
        Assert.Equal(reads, swap.Calls);
        HardwareControl.RefreshBatteryRate(force: true);
        Assert.Equal(reads + 1, swap.Calls);
    }

    [Fact]
    public void BatteryRateText_FormatsDischargeAndCharge_EmptyWhenUnknown()
    {
        Assert.Equal("", SettingsForm.BatteryRateText(null));
        Assert.Equal("", SettingsForm.BatteryRateText(0));
        Assert.Equal(Properties.Strings.Discharging + ": 45.3W", SettingsForm.BatteryRateText(-45.3m));
        Assert.Equal(Properties.Strings.Charging + ": 12.3W", SettingsForm.BatteryRateText(12.34m));
    }
}
