using System.Diagnostics;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 断线重连不得阻塞发帧：旧实现持 _lock 执行 Sleep(100) + 完整 HID 枚举，
/// 一次写失败会让整条帧流水线停摆数百毫秒（高负载下放大成卡顿/闪烁）。
/// 契约：单次写失败触发重连后，重连在后台单飞进行，期间 SendFrame 立即返回（本帧跳过），恢复后自动续帧。
/// </summary>
public class KeyboardRgbReconnectTests
{
    sealed class FlakyHid : HidDeviceWin
    {
        public volatile bool Failing = true;
        public override bool SetFeature(byte[] report) => !Failing;
        public override bool Write(byte[] report) => !Failing;
        public override void Dispose() { }
    }

    static string TempConfigPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "rgb.cfg");
    }

    [Fact]
    public void WriteFailure_DoesNotStallFramesWhileReconnectIsInFlight()
    {
        string configPath = TempConfigPath();
        string directory = Path.GetDirectoryName(configPath)!;
        var device = new FlakyHid();
        var keyboard = new KeyboardRgb(configPath, device);
        using var reconnectGate = new ManualResetEventSlim(false);
        keyboard.ReconnectProbe = () => { reconnectGate.Wait(25); return true; };
        var frame = new byte[KeyboardRgb.BufSize];
        try
        {
            Assert.False(keyboard.SendFrame(frame));

            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) keyboard.SendFrame(frame);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 500,
                $"重连飞行期间发帧被阻塞：100 帧耗时 {stopwatch.ElapsedMilliseconds}ms（旧实现持锁重连时每帧都要等满 100ms+）。");

            reconnectGate.Set();
            device.Failing = false;
            bool recovered = false;
            for (int i = 0; i < 300 && !recovered; i++)
            {
                recovered = keyboard.SendFrame(frame);
                if (!recovered) Thread.Sleep(2);
            }
            Assert.True(recovered, "重连完成后发帧应自动恢复。");
        }
        finally
        {
            keyboard.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
