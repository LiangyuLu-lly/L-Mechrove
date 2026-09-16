// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using Microsoft.Win32;
using System.Diagnostics;

namespace MechrevoLite.Battery
{
    public static class BatteryControl
    {

        static bool _chargeFull = AppConfig.Is("charge_full");
        public static bool chargeFull
        {
            get
            {
                return _chargeFull;
            }
            set
            {
                AppConfig.Set("charge_full", value ? 1 : 0);
                _chargeFull = value;
            }
        }

        /// <summary>
        /// 充电上限写入的手势凭证（与重启凭证同源、相互独立）。只有真实滑条手势
        /// （MouseUp/KeyUp）当刻能捕获；程序化写 Checked / UIA 拖拽 / 回读同步都拿不到。
        /// </summary>
        static readonly UserGestureLease _userLimitLease = new();

        /// <summary>滑条真实手势当刻调用：确有新鲜真实输入才授予一次短时凭证。</summary>
        internal static bool CaptureChargeLimitGesture() => _userLimitLease.Capture();

        /// <summary>测试接缝：清空未被消费的手势凭证，避免跨测试泄漏。</summary>
        internal static void ResetChargeLimitGesture() => _userLimitLease.Reset();

        /// <summary>
        /// 用户从滑条发起的「设置新上限」。必须先有真实手势凭证（<see cref="CaptureChargeLimitGesture"/>），
        /// 否则拒绝并如实回显——这样自动化（UIA 拖拽、程序化写 Value）无法静默改写充电上限，
        /// 而用户的真实手势也不会因为去抖计时器/硬件耗时烧掉 500ms 窗口而被丢掉。
        /// 凭证一次性：消费后交给 <see cref="SetBatteryChargeLimit"/> 做真正的 EC 写入。
        /// </summary>
        public static bool ApplyChargeLimitFromUserGesture(int limit)
        {
            if (!_userLimitLease.IsActive)
            {
                Logger.WriteLine($"EC 充电上限写入被拒绝：没有真实用户手势凭证（请求 {limit}%）");
                RestoreChargeLimitDisplay();
                return false;
            }
            _userLimitLease.Consume();
            return SetBatteryChargeLimit(limit);
        }

        public static void ToggleBatteryLimitFull()
        {
            if (chargeFull) SetBatteryChargeLimit();
            else SetBatteryLimitFull();
        }

        public static void SetBatteryLimitFull()
        {
            chargeFull = true;
            if (EcChargeLimit.IsAvailableOnThisMachine())
                SetBatteryChargeLimit(EcChargeLimit.MaximumPercent);   // 100% = 固件的无上限值
            Program.settingsForm.VisualiseBatteryFull();
        }

        public static void AutoBattery(bool init = false)
        {
            if (chargeFull && !init) SetBatteryLimitFull();
            else SetBatteryChargeLimit();
        }

        /// <summary>
        /// 设置充电上限：直写 EC 的充电阈值寄存器对（上限 0x7B9 + 复充下限 0x7D0），任意百分比都生效。
        ///
        /// 官方那条路（BatteryProtection 三档命令）已按用户决定整体摘除：它只往 EC 的模式位
        /// DBAP(0x7A6) 写 0x08/0x18/0x28，不碰阈值寄存器，而且该模式位的含义随机型而变
        /// （社区工具同样明确不碰它）。
        /// 写不进就如实弹回原值并记日志，不假装设置成功。
        /// </summary>
        public static bool SetBatteryChargeLimit(int setLimit = -1)
        {
            int limit = setLimit;
            if (limit < 0) limit = AppConfig.Get("charge_limit");
            if (!EcChargeLimit.IsSupportedLimit(limit))
            {
                // 不静默：请求值不在可写区间（含配置缺失时的 -1 哨兵）就说清楚，并如实回显。
                Logger.WriteLine(
                    $"充电上限 {limit}% 不在支持区间（{EcChargeLimit.MinimumPercent}–{EcChargeLimit.MaximumPercent}%），未写入");
                RestoreChargeLimitDisplay();
                return false;
            }

            if (!EcChargeLimit.IsAvailableOnThisMachine())
            {
                // 未验证的机型不猜寄存器地址（不同机型的 EC 布局不一样）。
                Logger.WriteLine($"EC 充电上限在本机型未验证，忽略 {limit}% 请求");
                RestoreChargeLimitDisplay();
                return false;
            }

            // EC 写是驱动调用（毫秒级，但可能被 EC 总线拖住），放后台，别卡住 UI 线程。
            _ = Task.Run(() =>
            {
                if (EcChargeLimit.TrySet(limit, out int confirmedPercent))
                {
                    Logger.WriteLine(
                        $"EC 充电阈值已确认：上限 {confirmedPercent}%、复充下限 {EcChargeLimit.LowerValueFor(confirmedPercent)}%" +
                        $"（0x{EcChargeLimit.UpperRegister:X3}/0x{EcChargeLimit.LowerRegister:X3}）");
                    CommitChargeLimit(confirmedPercent);
                }
                else
                {
                    // 写入当刻的回读可能撞上 EC 总线时序：驱动器调用返回失败，但硬件其实已经改了。
                    // 再读一次实际阈值，若已是请求值就按成功收尾（持久化 + 显示），不再显示未知。
                    int actual = EcChargeLimit.ReadPercent();
                    if (actual == limit)
                    {
                        Logger.WriteLine($"EC 充电上限回读确认延迟：请求 {limit}%，实际阈值已是 {actual}%，按成功收尾");
                        CommitChargeLimit(actual);
                    }
                    else
                    {
                        Logger.WriteLine($"EC 充电上限写入未确认：请求 {limit}%，保持原值");
                        RestoreChargeLimitDisplay();
                    }
                }
            });
            return true;
        }

        static void CommitChargeLimit(int limit)
        {
            AppConfig.Set("charge_limit", limit);
            chargeFull = limit >= EcChargeLimit.MaximumPercent;
            var form = Program.settingsForm;
            if (form is null || form.IsDisposed) return;
            if (form.InvokeRequired) form.Invoke(() => form.VisualiseBattery(limit));
            else form.VisualiseBattery(limit);
        }

        /// <summary>
        /// 显示用上限的唯一真源：优先 EC 的实际阈值（<see cref="EcChargeLimit.ReadPercent"/>，
        /// 用户设的值真正的落点），读不到时退回已持久化的配置值；两者都不可信时返回 -1（未知）。
        /// 机型未验证时不读 EC（寄存器布局未知，读了也是猜）。
        ///
        /// 这样刷新只回显「硬件的真实状态」，不会再拿缺失键的 -1 哨兵或陈旧配置去覆盖读数的显示。
        /// </summary>
        public static int ResolveDisplayLimitPercent()
        {
            if (EcChargeLimit.IsAvailableOnThisMachine())
            {
                int fromEc = EcChargeLimit.ReadPercent();
                if (EcChargeLimit.IsSupportedLimit(fromEc)) return fromEc;
            }
            int stored = AppConfig.Get("charge_limit");
            return EcChargeLimit.IsSupportedLimit(stored) ? stored : -1;
        }

        /// <summary>
        /// 把界面回显成当前上限（EC 实际阈值优先，其次已持久化的值）；两者都不可信时
        /// 如实显示「未知」，绝不拿 100% 或 -1% 冒充一个并不存在的上限。
        /// </summary>
        static void RestoreChargeLimitDisplay()
        {
            int resolved = ResolveDisplayLimitPercent();
            bool known = EcChargeLimit.IsSupportedLimit(resolved);
            var form = Program.settingsForm;
            if (form is null || form.IsDisposed) return;
            Action apply = known ? () => form.VisualiseBattery(resolved) : form.VisualiseBatteryUnknown;
            if (form.InvokeRequired) form.Invoke(apply);
            else apply();
        }

        public static void BatteryReport()
        {
            var reportDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                using var cmd = new Process();
                cmd.StartInfo.WorkingDirectory = reportDir;
                cmd.StartInfo.UseShellExecute = false;
                cmd.StartInfo.CreateNoWindow = true;
                cmd.StartInfo.FileName = "powershell";
                cmd.StartInfo.Arguments = "powercfg /batteryreport; explorer battery-report.html";
                cmd.Start();
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }

    }
}
