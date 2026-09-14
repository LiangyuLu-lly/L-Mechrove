// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Helpers;
using Microsoft.Win32;
using System.Diagnostics;

namespace MechrevoLite.Display
{
    public static class ScreenControl
    {

        public const int MAX_REFRESH = 1000;
        public static int MIN_RATE = AppConfig.Get("min_rate", 60);
        public static int MAX_RATE = AppConfig.Get("max_rate");

        public static int GetMaxRate(string? laptopScreen)
        {
            if (MAX_RATE > 0) return MAX_RATE;
            else return ScreenNative.GetMaxRefreshRate(laptopScreen);
        }

        public static void AutoScreen(bool force = false)
        {
            if (force || AppConfig.Is("screen_auto"))
            {
                if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
                    SetScreen(MAX_REFRESH, 1);
                else
                    SetScreen(MIN_RATE, 0);
            }
            else
            {
                SetScreen(overdrive: AppConfig.Get("overdrive"));
            }
        }

        public static void SetAutoRefresh(int auto)
        {
            AppConfig.Set("screen_auto", auto);
            if (auto == 0) SetAsusRefreshFlag(0);
        }

        public static void SetAsusRefreshFlag(int value)
        {
            if (!ProcessHelper.IsUserAdministrator()) return;
            const string keyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8714A8D1-0F08-4681-9DF6-A8C4607A58B4}";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key.SetValue("RefreshFlag", value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to set RefreshFlag: {ex.Message}");
            }
        }

        public static void ToggleScreenRate()
        {
            var laptopScreen = ScreenNative.FindLaptopScreen(true);
            var refreshRate = ScreenNative.GetRefreshRate(laptopScreen);
            if (refreshRate < 0) return;

            ScreenNative.SetRefreshRate(laptopScreen, refreshRate > MIN_RATE ? MIN_RATE : GetMaxRate(laptopScreen));
            InitScreen();
        }


        public static void SetScreen(int frequency = -1, int overdrive = -1, int miniled = -1)
        {
            var laptopScreen = ScreenNative.FindLaptopScreen(true);
            var refreshRate = ScreenNative.GetRefreshRate(laptopScreen);

            if (refreshRate < 0) return;

            if (frequency >= MAX_REFRESH)
            {
                frequency = GetMaxRate(laptopScreen);
            }

            if (frequency > 0 && frequency != refreshRate)
            {
                ScreenNative.SetRefreshRate(laptopScreen, frequency);
            }

            if (Program.acpi.IsOverdriveSupported() && overdrive >= 0)
            {
                if (AppConfig.IsNoOverdrive()) overdrive = 0;
                if (overdrive != Program.acpi.DeviceGet(AsusACPI.ScreenOverdrive))
                {
                    Program.acpi.DeviceSet(AsusACPI.ScreenOverdrive, overdrive, "ScreenOverdrive");
                }
            }

            SetMiniled(miniled);

            InitScreen();
        }

        public static void SetMiniled(int miniled = -1)
        {
            if (miniled >= 0)
            {
                if (Program.acpi.IsSupported(AsusACPI.ScreenMiniled1))
                    Program.acpi.DeviceSet(AsusACPI.ScreenMiniled1, miniled, "Miniled1");
                else
                {
                    Program.acpi.DeviceSet(AsusACPI.ScreenMiniled2, miniled, "Miniled2");
                }
            }
        }

        public static void InitMiniled()
        {
            if (AppConfig.IsForceMiniled())
            {
                SetHDRControl(AppConfig.Get("hdr_control"));
                SetMiniled(AppConfig.Get("miniled"));
            }
        }

        public static void InitOptimalBrightness()
        {
            int optimalBrightness = AppConfig.Get("optimal_brightness");
            if (optimalBrightness >= 0) SetOptimalBrightness(optimalBrightness);
        }

        public static void SetOptimalBrightness(int status)
        {
            AppConfig.Set("optimal_brightness", status);
            if (status == 2) status = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline ? 1 : 0;
            Program.acpi.DeviceSet(AsusACPI.ScreenOptimalBrightness, status, "Optimal Brightness");
        }

        public static int GetOptimalBrightness()
        {
            return Program.acpi.DeviceGet(AsusACPI.ScreenOptimalBrightness);
        }

        public static void ToogleFHD()
        {
            int fhd = Program.acpi.DeviceGet(AsusACPI.ScreenFHD);
            Logger.WriteLine($"FHD Toggle: {fhd}");

            DialogResult dialogResult = MessageBox.Show("Changing display mode requires reboot", Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.Yes)
            {
                Program.acpi.DeviceSet(AsusACPI.ScreenFHD, (fhd == 1) ? 0 : 1, "FHD");
                // 不可逆动作走统一入口：确认框刚点过 → 新鲜输入放行；后台发起，不占用 UI 线程。
                SystemRestart.RequestRestart("FHD toggle", SystemRestart.RebootNowArguments);
            }
        }

        public static void SetHDRControl(int status = -1)
        {
            if (status >= 0)
            {
                AppConfig.Set("hdr_control", status);
                Program.acpi.DeviceSet(AsusACPI.ScreenHDRControl, status, "HDR Control");
            }
        }

        public static void ToogleHDRControl()
        {
            int hdrControl = Program.acpi.DeviceGet(AsusACPI.ScreenHDRControl);
            Logger.WriteLine($"HDR Control Toggle: {hdrControl}");
            SetHDRControl((hdrControl == 1) ? 1 : 0);
            InitScreen();
        }

        public static string ToogleMiniled()
        {
            int miniled1 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled1);
            int miniled2 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled2);

            Logger.WriteLine($"MiniledToggle: {miniled1} {miniled2}");

            int miniled;
            string name;

            if (miniled1 >= 0)
            {
                switch (miniled1)
                {
                    case 1: 
                        miniled = 0;
                        name = Properties.Strings.OneZone;
                        break;
                    default:
                        miniled = 1;
                        name = Properties.Strings.Multizone;
                        break;
                }
            }
            else
            {
                switch (miniled2)
                {
                    case 1: 
                        miniled = 2;
                        name = Properties.Strings.OneZone;
                        break;
                    case 2: 
                        miniled = 0;
                        name = Properties.Strings.Multizone;
                        break;
                    default: 
                        miniled = 1;
                        name = Properties.Strings.MultizoneStrong;
                        break;
                }
            }

            AppConfig.Set("miniled", miniled);
            SetScreen(miniled: miniled);
            
            return name;
        }

        public static void InitScreen()
        {
            var laptopScreen = ScreenNative.FindLaptopScreen();
            int frequency = ScreenNative.GetRefreshRate(laptopScreen);
            int maxFrequency = GetMaxRate(laptopScreen);

            if (maxFrequency > 0) AppConfig.Set("max_frequency", maxFrequency);
            else maxFrequency = AppConfig.Get("max_frequency");

            bool screenAuto = AppConfig.Is("screen_auto");
            bool overdriveSetting = Program.acpi.IsOverdriveSupported() && !AppConfig.IsNoOverdrive();

            int overdrive = AppConfig.IsNoOverdrive() ? 0 : Program.acpi.DeviceGet(AsusACPI.ScreenOverdrive);

            int miniled1 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled1);
            int miniled2 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled2);

            int miniled = (miniled1 >= 0) ? miniled1 : miniled2;
            bool hdr = false;
            bool acm = false;

            if (miniled >= 0)
            {
                Logger.WriteLine($"Miniled: {miniled1} {miniled2}");
                AppConfig.Set("miniled", miniled);
            }

            try
            {
                hdr = ScreenCCD.GetHDRStatus(out acm);
            } catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }

            bool screenEnabled = (frequency >= 0);

            // ROG Zephyrus Duo 的副屏 FHD 切换。门禁 AppConfig.IsDUO() 已删除
            // （ASUS 机型串），而 DeviceGet(ScreenFHD) 对未映射的设备码返回 -1，
            // 所以这里恒为 -1，与不查询等价。
            int fhd = -1;

            int hdrControl = Program.acpi.DeviceGet(AsusACPI.ScreenHDRControl);
            if (hdrControl >= 0) Logger.WriteLine($"HDR Control Status: {hdrControl}");

            AppConfig.Set("frequency", frequency);
            AppConfig.Set("overdrive", overdrive);

            Program.settingsForm.BeginInvoke(delegate
            {
                Program.settingsForm.VisualiseScreen(
                    screenEnabled: screenEnabled,
                    screenAuto: screenAuto,
                    frequency: frequency,
                    maxFrequency: maxFrequency,
                    overdrive: overdrive,
                    overdriveSetting: overdriveSetting,
                    miniled1: miniled1,
                    miniled2: miniled2,
                    hdr: hdr,
                    acm: acm,
                    fhd: fhd,
                    hdrControl: hdrControl
                );
            });

        }
    }
}
