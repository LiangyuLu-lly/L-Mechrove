// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MechrevoLite.Mode
{
    internal class PowerNative
    {
        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerWriteDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            int AcValueIndex);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerWriteACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            int AcValueIndex);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerReadACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out IntPtr AcValueIndex
            );

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerReadDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out IntPtr AcValueIndex
            );


        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerSetActiveScheme(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerGetActiveScheme(IntPtr UserPowerKey, out IntPtr ActivePolicyGuid);

        static readonly Guid GUID_CPU = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid GUID_BOOST = new Guid("be337238-0d82-4146-a960-4f3749d470c7");

        private static Guid GUID_SLEEP_SUBGROUP = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        private static Guid GUID_HIBERNATEIDLE = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");

        private static Guid GUID_SYSTEM_BUTTON_SUBGROUP = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        private static Guid GUID_LIDACTION = new Guid("5CA83367-6E45-459F-A27B-476B1D01C936");

        private static Guid GUID_SUB_PCIEXPRESS = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static Guid GUID_PCI_EXPRESS_ASPM = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetActualOverlayScheme")]
        public static extern uint PowerGetActualOverlayScheme(out Guid ActualOverlayGuid);

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayGuid);

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
        public static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

        const string POWER_SILENT = "961cc777-2547-4f9d-8174-7d86181b8a7a";
        const string POWER_BALANCED = "00000000-0000-0000-0000-000000000000";
        const string POWER_TURBO = "ded574b5-45a0-4f42-8737-46345c09c238";

        const string PLAN_BALANCED = "381b4222-f694-41f0-9685-ff5bb260df2e";
        const string PLAN_HIGH_PERFORMANCE = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        static List<string> overlays = new() {
                POWER_BALANCED,
                POWER_TURBO,
                POWER_SILENT,
            };

        public static Dictionary<string, string> powerModes = new Dictionary<string, string>
            {
                { POWER_SILENT, "Best Power Efficiency" },
                { POWER_BALANCED, "Balanced" },
                { POWER_TURBO, "Best Performance" },
                { PLAN_HIGH_PERFORMANCE, "High Performance Plan"},
            };
        static Guid GetActiveScheme()
        {
            IntPtr pActiveSchemeGuid;
            var hr = PowerGetActiveScheme(IntPtr.Zero, out pActiveSchemeGuid);
            Guid activeSchemeGuid = (Guid)Marshal.PtrToStructure(pActiveSchemeGuid, typeof(Guid));
            return activeSchemeGuid;
        }

        public static int GetCPUBoost()
        {
            IntPtr AcValueIndex;
            Guid activeSchemeGuid = GetActiveScheme();

            UInt32 value = PowerReadACValueIndex(IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST, out AcValueIndex);

            return AcValueIndex.ToInt32();

        }

        public static void SetCPUBoost(int boost = 0)
        {
            Guid activeSchemeGuid = GetActiveScheme();

            if (boost == GetCPUBoost()) return;

            var hrAC = PowerWriteACValueIndex(
                 IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST,
                 boost);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            var hrDC = PowerWriteDCValueIndex(
                 IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST,
                 boost);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            Logger.WriteLine("Boost " + boost);
        }

        public static void SetPowerMode(string scheme)
        {

            if (scheme == PLAN_HIGH_PERFORMANCE)
            {
                SetPowerPlan(scheme);
                return;
            }
            else
            {
                // Power plan from config or defaulting to balanced
                string plan = AppConfig.GetModeString("scheme");
                SetPowerPlan(plan);            
            }

            if (!overlays.Contains(scheme)) return;

            Guid guidScheme = new Guid(scheme);

            uint status = PowerGetEffectiveOverlayScheme(out Guid activeScheme);

            if (GetBatterySaverStatus())
            {
                Logger.WriteLine("Battery Saver detected");
                return;
            }

            if (status != 0 || activeScheme != guidScheme)
            {
                status = PowerSetActiveOverlayScheme(guidScheme);
                Logger.WriteLine("Power Mode " + activeScheme + " -> " + scheme + ":" + (status == 0 ? "OK" : status));
            }

        }

        public static void SetPowerPlan(string scheme)
        {
            // Skipping power modes
            if (overlays.Contains(scheme)) return;

            if (scheme is null) scheme = PLAN_BALANCED;
            var activeScheme = GetActiveScheme().ToString();
            if (activeScheme == scheme) return;

            uint status = PowerSetActiveScheme(IntPtr.Zero, new Guid(scheme));
            Logger.WriteLine($"Power Plan {activeScheme} -> {scheme} :" + (status == 0 ? "OK" : status));
        }

        public static string GetDefaultPowerMode(int mode)
        {
            switch (mode)
            {
                case 1: // turbo
                    return POWER_TURBO;
                case 2: //silent
                    return POWER_SILENT;
                case 3:
                    return PLAN_HIGH_PERFORMANCE;
                default: // balanced
                    return POWER_BALANCED;
            }
        }

        public static void SetPowerMode(int mode)
        {
            SetPowerMode(GetDefaultPowerMode(mode));
        }

        internal static bool WouldSilentlyUndo(Guid activePlan, Guid requestedPlan)
        {
            Guid ultimate = new(MechrevoLite.Hardware.WinPowerPlan.UltimatePerformancePlanId);
            Guid balanced = new(PLAN_BALANCED);
            return activePlan == ultimate && requestedPlan == balanced;
        }

        /// <summary>
        /// 只读：Windows 电源模式此刻是否就是 <paramref name="overlayIndex"/>（平衡计划 + 对应覆盖层）。
        /// 模式二级自定义的守护窗口用它判断覆盖层是否被外部（厂商 GCU 的模式同步）改回。
        /// 读不到时返回 null（不据此重写）。
        /// </summary>
        internal static bool? IsOverlayActive(int overlayIndex)
        {
            try
            {
                if (overlayIndex is < 0 or > 2) return null;
                Guid target = new(overlayIndex switch
                {
                    0 => POWER_SILENT,
                    2 => POWER_TURBO,
                    _ => POWER_BALANCED,
                });
                if (PowerGetEffectiveOverlayScheme(out Guid actual) != 0) return null;
                return GetActiveScheme() == new Guid(PLAN_BALANCED) && actual == target;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Power overlay read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 模式二级自定义：把 Windows 电源模式（覆盖层）设为指定档并回读确认。
        /// 0=最佳能效 1=平衡 2=最佳性能。覆盖层只在「平衡」计划下生效，所以先切回平衡计划
        /// （与 g-helper / 高性能电源自动同步同一做法）；当前是卓越性能计划时不动它（那是用户
        /// 在自定义档里明确选的计划，静默改掉就是替用户做决定）。节电模式下 Windows 会忽略覆盖层。
        /// </summary>
        internal static PowerOverlayResult SetOverlayConfirmed(int overlayIndex)
        {
            try
            {
                if (overlayIndex is < 0 or > 2) return PowerOverlayResult.Failed;
                if (GetBatterySaverStatus()) return PowerOverlayResult.BatterySaver;
                Guid balancedPlan = new(PLAN_BALANCED);
                Guid target = new(overlayIndex switch
                {
                    0 => POWER_SILENT,
                    2 => POWER_TURBO,
                    _ => POWER_BALANCED,
                });
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Guid activePlan = GetActiveScheme();
                    if (WouldSilentlyUndo(activePlan, balancedPlan)) return PowerOverlayResult.UltimatePlanKept;
                    uint planStatus = activePlan == balancedPlan ? 0 : PowerSetActiveScheme(IntPtr.Zero, balancedPlan);
                    uint overlayStatus = PowerSetActiveOverlayScheme(target);
                    uint readStatus = PowerGetEffectiveOverlayScheme(out Guid actual);
                    bool confirmed = planStatus == 0 && overlayStatus == 0 && readStatus == 0 &&
                        GetActiveScheme() == balancedPlan && actual == target;
                    Logger.WriteLine($"Power overlay -> {target}: plan={planStatus} overlay={overlayStatus} read={readStatus}/{actual} confirmed={confirmed}");
                    if (confirmed) return PowerOverlayResult.Confirmed;
                    Thread.Sleep(100);
                }
                return PowerOverlayResult.Failed;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Power overlay apply failed: " + ex.Message);
                return PowerOverlayResult.Failed;
            }
        }

        internal static bool ApplyMechrevoPowerModeAutomation(bool enabled, int operatingMode)
        {
            try
            {
                Guid balancedPlan = new(PLAN_BALANCED);
                Guid activeBefore = GetActiveScheme();
                if (WouldSilentlyUndo(activeBefore, balancedPlan))
                {
                    Logger.WriteLine($"High performance power automation FIELD-LOG skip silent undo plan={activeBefore} enabled={enabled} opMode={operatingMode}");
                    return false;
                }

                Guid targetOverlay = new(enabled
                    ? operatingMode switch
                    {
                        0 => POWER_SILENT,
                        2 => POWER_TURBO,
                        _ => POWER_BALANCED,
                    }
                    : POWER_BALANCED);

                Guid actualPlan = Guid.Empty;
                Guid actualOverlay = Guid.Empty;
                uint planStatus = 0;
                uint overlayStatus = 0;
                uint readStatus = 0;
                bool confirmed = false;
                for (int attempt = 0; attempt < 3 && !confirmed; attempt++)
                {
                    Guid activePlan = GetActiveScheme();
                    if (WouldSilentlyUndo(activePlan, balancedPlan))
                    {
                        Logger.WriteLine($"High performance power automation FIELD-LOG skip silent undo plan={activePlan} enabled={enabled} opMode={operatingMode}");
                        return false;
                    }
                    planStatus = activePlan == balancedPlan
                        ? 0
                        : PowerSetActiveScheme(IntPtr.Zero, balancedPlan);
                    overlayStatus = PowerSetActiveOverlayScheme(targetOverlay);
                    readStatus = PowerGetEffectiveOverlayScheme(out actualOverlay);
                    actualPlan = GetActiveScheme();
                    confirmed = planStatus == 0 && overlayStatus == 0 && readStatus == 0 &&
                        actualPlan == balancedPlan && actualOverlay == targetOverlay;
                    if (!confirmed && attempt < 2)
                        Thread.Sleep(100);
                }
                Logger.WriteLine($"High performance power automation enabled={enabled} opMode={operatingMode} " +
                    $"plan={actualPlan} overlay={actualOverlay} status={planStatus}/{overlayStatus}/{readStatus} confirmed={confirmed}");
                return confirmed;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("High performance power automation failed: " + ex.Message);
                return false;
            }
        }

        public static int GetASPM()
        {
            Guid activeSchemeGuid = GetActiveScheme();
            IntPtr activeIndex;

            PowerReadACValueIndex(IntPtr.Zero,
                    activeSchemeGuid,
                    GUID_SUB_PCIEXPRESS,
                    GUID_PCI_EXPRESS_ASPM, out activeIndex);

            return activeIndex.ToInt32();
        }

        public static void SetASPM(int status = 0)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            var currentASPM = GetASPM();
            if (currentASPM == status) return;

            var hrAC = PowerWriteACValueIndex(
                IntPtr.Zero,
                activeSchemeGuid,
                GUID_SUB_PCIEXPRESS,
                GUID_PCI_EXPRESS_ASPM,
                status);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine($"Changed AC ASPM {currentASPM} -> {status}");
        }

        public static void SetBalancedASPM(int status = 0)
        {
            if (GetActiveScheme().ToString() != PLAN_BALANCED) return;
            SetASPM(status);
        }

        [DllImport("Kernel32")]
        private static extern bool GetSystemPowerStatus(SystemPowerStatus sps);
        public enum ACLineStatus : byte
        {
            Offline = 0, Online = 1, Unknown = 255
        }

        public enum BatteryFlag : byte
        {
            High = 1,
            Low = 2,
            Critical = 4,
            Charging = 8,
            NoSystemBattery = 128,
            Unknown = 255
        }

        // Fields must mirror their unmanaged counterparts, in order
        [StructLayout(LayoutKind.Sequential)]
        public class SystemPowerStatus
        {
            public ACLineStatus ACLineStatus;
            public BatteryFlag BatteryFlag;
            public Byte BatteryLifePercent;
            public Byte SystemStatusFlag;
            public Int32 BatteryLifeTime;
            public Int32 BatteryFullLifeTime;
        }

        public static bool GetBatterySaverStatus()
        {
            try
            {
                var status = Registry.GetValue(@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Control\Power", "EnergySaverState", null);
                if (status == null)
                {
                    SystemPowerStatus sps = new SystemPowerStatus();
                    GetSystemPowerStatus(sps);
                    return (sps.SystemStatusFlag > 0);
                }
                return (int)status == 1;
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't check EnergySaverState" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 电池充电状态（用于验证充电上限是否真的生效）：是否接交流电、是否正在充电、电量百分比。
        /// 读不到时返回 null。
        /// </summary>
        internal static (bool OnAc, bool Charging, int Percent)? GetChargeState()
        {
            try
            {
                SystemPowerStatus sps = new SystemPowerStatus();
                if (!GetSystemPowerStatus(sps)) return null;
                if (sps.BatteryFlag == BatteryFlag.Unknown || (sps.BatteryFlag & BatteryFlag.NoSystemBattery) != 0) return null;
                if (sps.BatteryLifePercent > 100) return null;
                return (sps.ACLineStatus == ACLineStatus.Online,
                    (sps.BatteryFlag & BatteryFlag.Charging) != 0,
                    sps.BatteryLifePercent);
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't read battery charge state: " + e.Message);
                return null;
            }
        }
    }

    internal enum PowerOverlayResult
    {
        Confirmed,
        Failed,
        BatterySaver,
        UltimatePlanKept,
    }
}
