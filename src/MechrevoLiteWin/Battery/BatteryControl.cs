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

        public static void ToggleBatteryLimitFull()
        {
            if (chargeFull) SetBatteryChargeLimit();
            else SetBatteryLimitFull();
        }

        public static void SetBatteryLimitFull()
        {
            chargeFull = true;
            if (EcChargeLimit.IsAvailableOnThisMachine())
            {
                SetBatteryChargeLimit(EcChargeLimit.MaximumPercent);   // 100% = 固件的无上限值
                Program.settingsForm.VisualiseBatteryFull();
                return;
            }
            Program.acpi.DeviceSet(AsusACPI.BatteryLimit, 100, "BatteryLimit");
            Program.settingsForm.VisualiseBatteryFull();
        }

        public static void UnSetBatteryLimitFull()
        {
            chargeFull = false;
            Logger.WriteLine("Battery fully charged");
            Program.settingsForm.Invoke(Program.settingsForm.VisualiseBatteryFull);
        }

        public static void AutoBattery(bool init = false)
        {
            if (chargeFull && !init) SetBatteryLimitFull();
            else SetBatteryChargeLimit();
        }

        public static void SetAsusChargeLimit(int value)
        {
            if (!ProcessHelper.IsUserAdministrator()) return;
            const string keyPath = @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key.SetValue("ChargingRate", value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to set ChargingRate: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置充电上限：直写 EC 的充电阈值寄存器对（上限 0x7B9 + 复充下限 0x7D0），任意百分比都生效。
        ///
        /// 官方那条路（BatteryProtection 三档命令）已按用户决定整体摘除：它只往 EC 的模式位
        /// DBAP(0x7A6) 写 0x08/0x18/0x28，不碰阈值寄存器，而且该模式位的含义随机型而变
        /// （社区工具同样明确不碰它）。
        /// 写不进就如实弹回原值并记日志，不假装设置成功。
        /// </summary>
        public static void SetBatteryChargeLimit(int setLimit = -1)
        {
            int limit = setLimit;
            if (limit < 0) limit = AppConfig.Get("charge_limit");
            if (!EcChargeLimit.IsSupportedLimit(limit)) return;

            if (!EcChargeLimit.IsAvailableOnThisMachine())
            {
                // 未验证的机型不猜寄存器地址（不同机型的 EC 布局不一样）。
                Logger.WriteLine($"EC 充电上限在本机型未验证，忽略 {limit}% 请求");
                RestoreChargeLimitDisplay();
                return;
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
                    Logger.WriteLine($"EC 充电上限写入未确认：请求 {limit}%，保持原值");
                    RestoreChargeLimitDisplay();
                }
            });
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

        /// <summary>把界面回显成配置里的当前上限（写入失败或机型不支持时用来弹回，不谎报）。</summary>
        static void RestoreChargeLimitDisplay()
        {
            int stored = AppConfig.Get("charge_limit");
            if (!EcChargeLimit.IsSupportedLimit(stored)) stored = EcChargeLimit.MaximumPercent;
            var form = Program.settingsForm;
            if (form is null || form.IsDisposed) return;
            if (form.InvokeRequired) form.Invoke(() => form.VisualiseBattery(stored));
            else form.VisualiseBattery(stored);
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
