using System.Diagnostics;

namespace MechrevoLite.Helpers;

/// <summary>
/// 重启/关机这类不可逆系统动作的唯一入口。守卫链与「息屏」开关同源：
/// <list type="number">
/// <item>只接受真实用户动作——程序化赋值、回读同步、定时器都拿不到新鲜输入；</item>
/// <item><see cref="NativeMethods.HasFreshUserInput"/> 要求 500ms 内确有真实输入，自动化调用一律拒绝；</item>
/// <item>确认框（MessageBox Yes/OK）刚点过时用 <see cref="CaptureUserConfirmation"/> 捕获一次短时凭证：
///       确认之后还要做硬件写入/await 的流程（例如 MUX 切换要等 EC 掉电）不会因为 500ms 窗口
///       已过而被判成「陈旧」丢掉重启；凭证只能在新鲜真实输入下取得，程序化路径依旧拿不到；</item>
/// <item>动作经 <see cref="Task.Run(Action)"/> 在后台线程发起，绝不占用 UI 线程；</item>
/// <item>返回 false 表示被守卫拒绝，调用方可据此决定是否回滚界面。</item>
/// </list>
/// <see cref="ProcessStartOverride"/> 是测试接缝：注入后只记录，绝不真的重启。
/// </summary>
internal static class SystemRestart
{
    internal const string RebootNowArguments = "/r /t 1";
    internal const string RebootAfterFiveSecondsArguments = "/r /t 5";

    /// <summary>确认凭证有效期：覆盖确认之后到真正发起重启之间的硬件写入/await（最长约 1 秒）。</summary>
    internal const int ConfirmationValidityMs = UserGestureLease.ValidityMs;

    /// <summary>测试接缝：非 null 时由它代替 Process.Start 发起重启（测试只记录，绝不真重启）。</summary>
    internal static Action<string, string>? ProcessStartOverride { get; set; }

    /// <summary>重启专用的确认凭证（与充电上限写入等其它消费者各持一份，互不放行）。</summary>
    static readonly UserGestureLease Confirmation = new();

    /// <summary>
    /// 在用户刚刚确认（模态框返回 Yes/OK）的那一刻调用：只有确有新鲜真实输入才授予一次短时
    /// 凭证。程序化/自动化路径拿不到新鲜输入，因此拿不到凭证——这是「不可程序化触发」的实质门禁。
    /// </summary>
    internal static bool CaptureUserConfirmation()
    {
        if (!Confirmation.Capture())
        {
            Logger.WriteLine("Restart confirmation not captured: no fresh user input.");
            return false;
        }
        return true;
    }

    /// <summary>测试接缝：清空未被消费的确认凭证，避免凭证跨测试泄漏。</summary>
    internal static void ResetUserConfirmation() => Confirmation.Reset();

    static bool HasCapturedConfirmation() => Confirmation.IsActive;

    /// <summary>
    /// 请求重启。新鲜真实输入，或确认框处捕获的有效凭证，二者之一放行；程序化/自动化调用两者皆无 → 拒绝。
    /// 放行后经 <see cref="Task.Run(Action)"/> 发起，绝不阻塞 UI 线程。
    /// </summary>
    internal static bool RequestRestart(string reason, string arguments = RebootAfterFiveSecondsArguments)
    {
        if (!NativeMethods.HasFreshUserInput() && !HasCapturedConfirmation())
        {
            Logger.WriteLine($"Restart ignored ({reason}): no fresh user input.");
            return false;
        }
        Confirmation.Consume();

        Action<string, string>? overrideStart = ProcessStartOverride;
        _ = Task.Run(() =>
        {
            try
            {
                if (overrideStart is not null)
                {
                    overrideStart("shutdown", arguments);
                    Logger.WriteLine($"Restart requested ({reason}); override captured shutdown {arguments}.");
                    return;
                }
                Process.Start(new ProcessStartInfo("shutdown", arguments)
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                });
                Logger.WriteLine($"Restart requested ({reason}): shutdown {arguments}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Restart failed ({reason}): {ex.Message}");
            }
        });
        return true;
    }
}
