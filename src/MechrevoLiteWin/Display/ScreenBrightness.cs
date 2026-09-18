namespace MechrevoLite.Display
{
    using System;
    using System.Diagnostics;
    using System.Management;

    public static class ScreenBrightness
    {
        /// <summary>
        /// 测试接缝（先例：<c>NativeMethods.IdleTimeProvider</c> / <c>UpdateChecker.HttpGetOverride</c>）。
        /// 非 null 时由它提供亮度读数；返回 null 表示「读不到」——调用方绝不能把读不到当成 0 去盲目调光。
        /// 生产运行时保持 null，直接走 WMI。
        /// </summary>
        internal static Func<int?>? ReadOverride { get; set; }

        /// <summary>
        /// 测试接缝：非 null 时由它接收亮度写入（测试只记录，绝不真改屏）。生产运行时保持 null，走 WMI。
        /// </summary>
        internal static Action<int>? WriteOverride { get; set; }

        /// <summary>
        /// 读取当前面板亮度。返回 false 表示没有可用的 WMI 实例（读不到），调用方不得据此猜数。
        /// </summary>
        public static bool TryGet(out int brightness)
        {
            if (ReadOverride is { } read)
            {
                int? value = read();
                brightness = value ?? 0;
                return value.HasValue;
            }

            using var mclass = new ManagementClass("WmiMonitorBrightness")
            {
                Scope = new ManagementScope(@"\\.\root\wmi")
            };
            using var instances = mclass.GetInstances();
            foreach (ManagementObject instance in instances)
            {
                brightness = (byte)instance.GetPropertyValue("CurrentBrightness");
                return true;
            }
            brightness = 0;
            return false;
        }

        public static int Get()
        {
            TryGet(out int brightness);
            return brightness;
        }

        public static void Set(int brightness)
        {
            if (WriteOverride is { } write)
            {
                write(brightness);
                return;
            }

            using var mclass = new ManagementClass("WmiMonitorBrightnessMethods")
            {
                Scope = new ManagementScope(@"\\.\root\wmi")
            };
            using var instances = mclass.GetInstances();
            var args = new object[] { 1, brightness };
            foreach (ManagementObject instance in instances)
            {
                instance.InvokeMethod("WmiSetBrightness", args);
            }
        }

        /// <summary>
        /// 写亮度并**报告是否真的写下去了**：没有可写的 WMI 实例、或写抛异常 -> <c>false</c>。
        /// 静默当成功正是「屏幕无法熄屏」缺陷的来源：调用方必须据此给出可检测的失败。
        /// </summary>
        public static bool TrySet(int brightness)
        {
            if (WriteOverride is { } write)
            {
                try { write(brightness); return true; }
                catch (Exception ex)
                {
                    Logger.WriteLine("Screen brightness write failed: " + ex.Message);
                    return false;
                }
            }

            try
            {
                using var mclass = new ManagementClass("WmiMonitorBrightnessMethods")
                {
                    Scope = new ManagementScope(@"\\.\root\wmi")
                };
                using var instances = mclass.GetInstances();
                var args = new object[] { 1, brightness };
                bool wroteAny = false;
                foreach (ManagementObject instance in instances)
                {
                    instance.InvokeMethod("WmiSetBrightness", args);
                    wroteAny = true;
                }
                return wroteAny;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Screen brightness write failed: " + ex.Message);
                return false;
            }
        }

        public static int Adjust(int delta)
        {
            int brightness = Get();
            Debug.WriteLine(brightness);
            brightness = Math.Min(100, Math.Max(0, brightness + delta));
            Set(brightness);
            return brightness;
        }

    }
}
